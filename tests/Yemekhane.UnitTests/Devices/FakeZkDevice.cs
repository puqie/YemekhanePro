using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Yemekhane.Devices.ZkTeco.Protocol;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Loopback'te UDP dinleyen SAHTE ZK CIHAZI. Gercek <see cref="ZkProtocolSdk"/> ve gercek
/// <see cref="ZkUdpTransport"/> buna karsi sinanir; boylece paket bicimi, oturum/sifre el sikismasi,
/// parcali kullanici tablosu okuma ve gercek zamanli olay yolu ucundan ucuna gercek soketle
/// dogrulanir. Cihaz tarafi davranisi pyzk/node-zklib'in bekledigi yanitlardan turetilmistir.
/// </summary>
internal sealed class FakeZkDevice : IAsyncDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private IPEndPoint? _client;
    private ushort _session;
    private bool _authenticated;
    private ushort _nextSession = 0x1000;

    public FakeZkDevice()
    {
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _loop = RunAsync(_stopping.Token);
    }

    public int Port { get; }
    public int CommKey { get; set; }
    public string DeviceName { get; set; } = "SC403";
    public string SerialNumber { get; set; } = "SN-FAKE-001";
    public string Firmware { get; set; } = "Ver 6.60 Jan 1 2015";
    public int UserRecordSize { get; set; } = ZkProtocolSdk.LegacyUserRecordSize;
    public int UserCapacity { get; set; } = 30_000;
    public int RecordCapacity { get; set; } = 50_000;
    public int Records { get; set; }

    /// <summary>Kullanici tablosunu tek CMD_DATA yerine PREPARE_DATA + parcalarla gonderir.</summary>
    public bool ChunkedUserRead { get; set; }

    /// <summary>USER_WRQ komutuna ACK_ERROR doner (bellek dolu / gecersiz kayit senaryosu).</summary>
    public bool RejectUserWrites { get; set; }

    /// <summary>Bir sonraki komuta hic cevap vermez (UDP kaybi senaryosu).</summary>
    public int DropNextReplies { get; set; }

    public Dictionary<ushort, ZkDeviceUser> Users { get; } = [];
    public List<uint> Unlocks { get; } = [];
    public List<uint> RegisteredEventFlags { get; } = [];
    public List<ushort> ReceivedCommands { get; } = [];
    public int ConnectCount { get; private set; }
    public int ExitCount { get; private set; }

    /// <summary>Gercek zamanli gecis kaydi olayi gonderir (pyzk live_capture 10 baytlik yerlesim).</summary>
    public async Task EmitAttendanceAsync(ushort pin, DateTime time)
    {
        var data = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(data, pin);
        data[2] = 1;
        data[3] = 0;
        WriteTime(data.AsSpan(4), time);
        await EmitEventAsync((ushort)ZkCommands.Events.AttendanceLog, data).ConfigureAwait(false);
    }

    /// <summary>
    /// Kart numarasi olayi (0x0400). Sahada olculen bicim: kart uint32 LE + 1 dolgu bayti;
    /// kayitli olsun olmasin her okutmada, gecis kaydindan ~1,3 sn ONCE gelir.
    /// </summary>
    public Task EmitCardNumberAsync(uint cardNumber)
    {
        var data = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(data, cardNumber);
        return EmitEventAsync((ushort)ZkCommands.Events.CardNumber, data);
    }

    /// <summary>Alarm olayi (0x0200, 4 bayt). Sahada 4 okutmadan sonra kod 55 goruldu; kart degildir.</summary>
    public Task EmitAlarmAsync(uint alarmCode)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, alarmCode);
        return EmitEventAsync((ushort)ZkCommands.Events.Alarm, data);
    }

    /// <summary>Yeni firmware yerlesimi: 32 bayt, 24 karakterlik PIN metni.</summary>
    public async Task EmitAttendanceExtendedAsync(string pin, DateTime time)
    {
        var data = new byte[32];
        Encoding.ASCII.GetBytes(pin).AsSpan(0, Math.Min(24, pin.Length)).CopyTo(data);
        data[24] = 1;
        data[25] = 0;
        WriteTime(data.AsSpan(26), time);
        await EmitEventAsync((ushort)ZkCommands.Events.AttendanceLog, data).ConfigureAwait(false);
    }

    /// <summary>Tanimsiz uzunlukta olay: SDK bunu dusurmeli ama tanilama notu birakmali.</summary>
    public Task EmitGarbageEventAsync() => EmitEventAsync((ushort)ZkCommands.Events.AttendanceLog, [1, 2, 3]);

    private async Task EmitEventAsync(ushort eventCode, byte[] data)
    {
        var client = _client ?? throw new InvalidOperationException("Henuz baglanan istemci yok.");
        // Gercek cihaz olay kodunu oturum alaninda tasir (sahada dogrulandi: 0x0400 kart, 0x0001 gecis, 0x0200 alarm).
        var packet = BuildRaw(ZkCommands.RegisterEvent, eventCode, 0, data);
        await _socket.SendAsync(packet, packet.Length, client).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _client = received.RemoteEndPoint;
            ZkPacket request;
            try { request = ZkPacket.Parse(received.Buffer); }
            catch (Exception) { continue; }

            ReceivedCommands.Add(request.Command);
            if (DropNextReplies > 0) { DropNextReplies--; continue; }

            foreach (var reply in Handle(request))
            {
                await _socket.SendAsync(reply, reply.Length, received.RemoteEndPoint).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<byte[]> Handle(ZkPacket request)
    {
        byte[] Ack(ushort command, byte[]? data = null) => BuildRaw(command, _session, request.ReplyId, data ?? []);

        switch (request.Command)
        {
            case ZkCommands.Connect:
                ConnectCount++;
                _session = _nextSession++;
                _authenticated = CommKey == 0;
                yield return Ack(_authenticated ? ZkCommands.AckOk : ZkCommands.AckUnauthorized);
                yield break;

            case ZkCommands.Auth:
                var expected = ZkCommKey.Create(CommKey, _session);
                _authenticated = request.Data.AsSpan().SequenceEqual(expected);
                yield return Ack(_authenticated ? ZkCommands.AckOk : ZkCommands.AckUnauthorized);
                yield break;
        }

        if (!_authenticated || request.SessionId != _session)
        {
            yield return Ack(ZkCommands.AckUnauthorized);
            yield break;
        }

        switch (request.Command)
        {
            case ZkCommands.Exit:
                ExitCount++;
                _session = 0;
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.GetVersion:
                yield return Ack(ZkCommands.AckOk, Encoding.ASCII.GetBytes(Firmware));
                break;

            case ZkCommands.OptionsRead:
                var name = ZkProtocolSdk.DecodeString(request.Data);
                var value = name switch
                {
                    "~DeviceName" => DeviceName,
                    "~SerialNumber" => SerialNumber,
                    _ => null
                };
                yield return value is null
                    ? Ack(ZkCommands.AckError)
                    : Ack(ZkCommands.AckOk, Encoding.ASCII.GetBytes($"{name}={value}\0"));
                break;

            case ZkCommands.GetFreeSizes:
                var sizes = new byte[92];
                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(4 * 4), Users.Count);
                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(8 * 4), Records);
                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(15 * 4), UserCapacity);
                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(17 * 4), RecordCapacity);
                yield return Ack(ZkCommands.AckOk, sizes);
                break;

            case ZkCommands.RegisterEvent:
                RegisteredEventFlags.Add(BinaryPrimitives.ReadUInt32LittleEndian(request.Data));
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.Unlock:
                Unlocks.Add(BinaryPrimitives.ReadUInt32LittleEndian(request.Data));
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.UserWrite:
                if (RejectUserWrites || request.Data.Length != UserRecordSize)
                {
                    yield return Ack(ZkCommands.AckError);
                    break;
                }

                var user = ZkProtocolSdk.ParseUserRecords(request.Data, UserRecordSize)[0];
                Users[user.Uid] = user;
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.DeleteUser:
                Users.Remove(BinaryPrimitives.ReadUInt16LittleEndian(request.Data));
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.RefreshData:
            case ZkCommands.EnableDevice:
            case ZkCommands.DisableDevice:
            case ZkCommands.FreeData:
                yield return Ack(ZkCommands.AckOk);
                break;

            case ZkCommands.DataWriteReadRequest:
                var table = BuildUserTable();
                if (!ChunkedUserRead)
                {
                    yield return Ack(ZkCommands.Data, table);
                    break;
                }

                _pendingTable = table;
                var prepare = new byte[5];
                BinaryPrimitives.WriteUInt32LittleEndian(prepare.AsSpan(1), (uint)table.Length);
                yield return Ack(ZkCommands.PrepareData, prepare);
                break;

            case ZkCommands.ReadBuffer:
                var start = BinaryPrimitives.ReadInt32LittleEndian(request.Data);
                var size = BinaryPrimitives.ReadInt32LittleEndian(request.Data.AsSpan(4));
                var slice = (_pendingTable ?? []).AsSpan(start, Math.Min(size, (_pendingTable ?? []).Length - start)).ToArray();
                var header = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)slice.Length);
                yield return Ack(ZkCommands.PrepareData, header);
                for (var offset = 0; offset < slice.Length; offset += 1024)
                {
                    yield return Ack(ZkCommands.Data, slice.AsSpan(offset, Math.Min(1024, slice.Length - offset)).ToArray());
                }

                yield return Ack(ZkCommands.AckOk);
                break;

            default:
                yield return Ack(ZkCommands.AckError);
                break;
        }
    }

    private byte[]? _pendingTable;

    private byte[] BuildUserTable()
    {
        var records = Users.Values.OrderBy(user => user.Uid).ToList();
        var table = new byte[4 + records.Count * UserRecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)(records.Count * UserRecordSize));
        var offset = 4;
        foreach (var user in records)
        {
            var record = table.AsSpan(offset, UserRecordSize);
            BinaryPrimitives.WriteUInt16LittleEndian(record, user.Uid);
            if (UserRecordSize == ZkProtocolSdk.LegacyUserRecordSize)
            {
                Encoding.ASCII.GetBytes(user.Name).AsSpan(0, Math.Min(8, user.Name.Length)).CopyTo(record[8..]);
                BinaryPrimitives.WriteUInt32LittleEndian(record[16..], user.Card);
                BinaryPrimitives.WriteUInt32LittleEndian(record[24..], uint.Parse(user.Pin));
            }
            else
            {
                Encoding.ASCII.GetBytes(user.Name).AsSpan(0, Math.Min(24, user.Name.Length)).CopyTo(record[11..]);
                BinaryPrimitives.WriteUInt32LittleEndian(record[35..], user.Card);
                Encoding.ASCII.GetBytes(user.Pin).AsSpan(0, Math.Min(24, user.Pin.Length)).CopyTo(record[48..]);
            }

            offset += UserRecordSize;
        }

        return table;
    }

    /// <summary>Cihaz yaniti: istegin reply_id'si oldugu gibi yansitilir (pyzk bunu bir sonrakine temel alir).</summary>
    private static byte[] BuildRaw(ushort command, ushort session, ushort replyId, byte[] data)
    {
        var buffer = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, command);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), session);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), replyId);
        data.CopyTo(buffer, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), ZkPacket.ComputeChecksum(buffer));
        return buffer;
    }

    private static void WriteTime(Span<byte> target, DateTime time)
    {
        target[0] = (byte)(time.Year - 2000);
        target[1] = (byte)time.Month;
        target[2] = (byte)time.Day;
        target[3] = (byte)time.Hour;
        target[4] = (byte)time.Minute;
        target[5] = (byte)time.Second;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _socket.Dispose();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
}
