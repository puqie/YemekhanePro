using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// ZKTeco standalone cihazlarla ham tel protokolu (UDP 4370) uzerinden konusan
/// <see cref="IZkTecoSdk"/> uygulamasi.
///
/// <para>
/// NEDEN VAR: bu arayuzun uretimde HIC uygulamasi yoktu (fabrika <c>_ =&gt; null</c> veriyordu);
/// adaptor her komutu ZK_SDK_NOT_CONFIGURED ile reddediyor, aga tek bayt gitmiyordu. Uretici
/// SDK'si (zkemkeeper.dll) 32-bit COM oldugu icin 64-bit API'ye baglanamaz; protokol ise iki
/// bagimsiz acik kaynak uygulamada (pyzk, node-zklib) belgelidir ve buradaki davranis o ikisinden
/// birebir kopyalanmistir -- tahmin edilen hicbir sey yoktur.
/// </para>
/// <para>
/// TEK ALICI DONGU: gercek zamanli kart olaylari, komut yanitlariyla AYNI sokette ic ice gelir.
/// Bu yuzden tek bir dongu tum datagramlari okur ve REG_EVENT paketlerini olay kanalina, gerisini
/// yanit kanalina yonlendirir. Tek bir TaskCompletionSource ile yanit beklemek, araya giren bir
/// kart olayini "yanit" sanir ya da tersi olurdu.
/// </para>
/// <para>
/// KULLANICI ESLEME: cihaz kullanicilari 16-bit <c>uid</c> ve bir PIN ile tutar; bizim
/// <c>externalUserId</c> ise bir GUID'dir ve hicbir alana sigmaz. Bu yuzden cihazda
/// <c>PIN = uid</c> yazilir, ad alanina kart numarasi konur ve kart↔uid eslemesi cihazin kendi
/// kullanici tablosundan okunur; GUID cihaza hic gitmez. Sistem karti kimin oldugunu zaten
/// veritabanindan bilir, cihazdan yalnizca KART NUMARASI gelmesi yeter.
/// </para>
/// </summary>
public sealed class ZkProtocolSdk : IZkTecoSdk
{
    /// <summary>pyzk: reply_id baslangici USHRT_MAX - 1.</summary>
    private const ushort InitialReplyId = 65534;

    /// <summary>pyzk UDP icin okuma parcasi ust siniri.</summary>
    private const int MaxChunk = 16 * 1024;

    /// <summary>Eski firmware (ZEM500 vb.) kullanici kaydi; pyzk varsayilani.</summary>
    internal const int LegacyUserRecordSize = 28;

    /// <summary>Yeni firmware kullanici kaydi.</summary>
    internal const int ExtendedUserRecordSize = 72;

    private static readonly IReadOnlySet<DeviceCapability> SupportedCapabilities = new HashSet<DeviceCapability>
    {
        DeviceCapability.DeviceInfo, DeviceCapability.Status, DeviceCapability.ReadCard,
        DeviceCapability.ReadUser, DeviceCapability.SendCard, DeviceCapability.SendUser,
        DeviceCapability.SyncCard, DeviceCapability.SyncUser, DeviceCapability.DeleteCard,
        DeviceCapability.GrantAccess, DeviceCapability.DenyAccess
    };

    private readonly IZkTransport _transport;
    private readonly int _commKey;
    private readonly TimeSpan _replyTimeout;
    private readonly uint _eventFlags;
    private readonly int? _forcedUserRecordSize;
    private readonly Action<string>? _diagnostics;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Dictionary<ushort, ZkDeviceUser> _usersByUid = [];

    private Channel<ZkPacket> _replies = Channel.CreateUnbounded<ZkPacket>();
    private Channel<ZkPacket> _events = Channel.CreateUnbounded<ZkPacket>();
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;
    private ushort _session;
    private ushort _replyId = InitialReplyId;
    private int _userRecordSize = LegacyUserRecordSize;
    private bool _eventsRegistered;
    private int _disposed;
    private DateTimeOffset _lastCardNumberEventAt = DateTimeOffset.MinValue;
    private bool _clockSkewReported;

    /// <summary>
    /// Kart olayindan (0x0400) sonra bu pencere icinde gelen gecis kaydi (0x0001) ayni okutmanin
    /// yankisidir. Sahada olculen gecikme ~1,3 sn; pencere UDP gecikmesine pay birakir.
    /// </summary>
    internal static readonly TimeSpan AttendanceEchoWindow = TimeSpan.FromSeconds(3);

    /// <summary>Cihaz saati bilgisayardan bu kadar sapmissa tanilama notu birakilir (oturumda bir kez).</summary>
    internal static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    /// <param name="transport">Datagram tasima; uretimde <see cref="ZkUdpTransport"/>.</param>
    /// <param name="commKey">Cihazin iletisim sifresi; 0 = sifresiz.</param>
    /// <param name="replyTimeout">
    /// Tek bir komut yanitinin beklenecegi sure. Adaptorun kendi zaman asimindan KISA olmalidir:
    /// UDP'de kaybolan bir datagram bu sure sonunda GECICI hata olarak bildirilir ve adaptor
    /// yeniden dener; adaptorun zaman asimi once dolarsa yeniden deneme hic olmaz.
    /// </param>
    /// <param name="eventFlags">
    /// REG_EVENT bayraklari; varsayilan gecis kaydi + kart numarasi (<see cref="ZkCommands.Events.Default"/>).
    /// Kart numarasi olayi sahada olculdu ve kayitsiz kartlari da tasir; gecis kaydi, kart olayi
    /// gondermeyen firmware icin yedektir.
    /// </param>
    /// <param name="userRecordSize">28 ya da 72; null ise cihazdan olculur.</param>
    /// <param name="diagnostics">Cozulemeyen olaylar gibi tanilama notlari icin.</param>
    /// <param name="timeProvider">
    /// Olaylar BU saatle damgalanir, cihaz saatiyle degil: sahada cihaz saati 83 dakika gerideydi
    /// ve ogun penceresi/gun buna gore kayardi. Null ise sistem saati.
    /// </param>
    public ZkProtocolSdk(IZkTransport transport, int commKey = 0, TimeSpan? replyTimeout = null,
        uint eventFlags = ZkCommands.Events.Default, int? userRecordSize = null,
        Action<string>? diagnostics = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (userRecordSize is not (null or LegacyUserRecordSize or ExtendedUserRecordSize))
        {
            throw new ArgumentOutOfRangeException(nameof(userRecordSize), "Kullanici kaydi 28 ya da 72 bayt olabilir.");
        }

        _transport = transport;
        _commKey = commKey;
        _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(3);
        _eventFlags = eventFlags;
        _forcedUserRecordSize = userRecordSize;
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (userRecordSize is { } forced) _userRecordSize = forced;
    }

    public bool IsConnected => _session != 0 && _transport.IsOpen && _pump is { IsCompleted: false };

    /// <summary>Cihazdan okunan kullanici kaydi boyutu (28/72). Tanilama icin.</summary>
    public int UserRecordSize => _userRecordSize;

    public async Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(endpoint);
        if (IsConnected) return;

        await TearDownAsync(sendExit: false).ConfigureAwait(false);
        var host = endpoint.IpAddress ?? throw new ArgumentException("IP adresi gerekli.", nameof(endpoint));
        var port = endpoint.IpPort ?? Sc403Adapter.DefaultPort;

        await _transport.OpenAsync(host, port, cancellationToken).ConfigureAwait(false);
        StartPump();
        _session = 0;
        _replyId = InitialReplyId;
        _eventsRegistered = false;
        _usersByUid.Clear();

        try
        {
            var reply = await ExecuteAsync(ZkCommands.Connect, ReadOnlyMemory<byte>.Empty, cancellationToken,
                requireSession: false).ConfigureAwait(false);

            if (reply.Command == ZkCommands.AckUnauthorized)
            {
                // Cihaz iletisim sifresi istiyor; oturum kimligi bu yanitta gelir ve sifre onunla turetilir.
                _session = reply.SessionId;
                var auth = await ExecuteAsync(ZkCommands.Auth, ZkCommKey.Create(_commKey, _session),
                    cancellationToken).ConfigureAwait(false);
                if (auth.Command != ZkCommands.AckOk)
                {
                    throw new ZkTecoProtocolException(
                        "ZK cihazi iletisim sifresini (comm key) reddetti. Cihazdaki sifreyi Devices:ZkCommKey ayarina yazin.",
                        isTransient: false, ZkTecoErrorCodes.ConnectFailed);
                }
            }
            else if (reply.Command == ZkCommands.AckOk)
            {
                _session = reply.SessionId;
            }
            else
            {
                throw new ZkTecoProtocolException($"ZK CONNECT beklenmeyen yanit: {reply.Command}.",
                    isTransient: false, ZkTecoErrorCodes.HandshakeInvalidResponse);
            }

            if (_session == 0)
            {
                throw new ZkTecoProtocolException("ZK cihazi oturum kimligi vermedi.",
                    isTransient: false, ZkTecoErrorCodes.HandshakeInvalidResponse);
            }
        }
        catch (ZkTecoProtocolException exception) when (exception.ErrorCode == ReplyTimeoutCode)
        {
            await TearDownAsync(sendExit: false).ConfigureAwait(false);
            throw new ZkTecoProtocolException(
                $"ZK cihazi ({host}:{port}) UDP 4370'te cevap vermedi. IP dogru mu, cihaz ayni agda mi?",
                isTransient: true, ZkTecoErrorCodes.ConnectTimeout, exception);
        }
        catch
        {
            await TearDownAsync(sendExit: false).ConfigureAwait(false);
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => TearDownAsync(sendExit: true);

    public async Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        EnsureSession();
        var version = await ExecuteAsync(ZkCommands.GetVersion, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        var firmware = version.Command == ZkCommands.AckOk ? DecodeString(version.Data) : null;

        var model = await ReadOptionAsync("~DeviceName", cancellationToken).ConfigureAwait(false);
        var serial = await ReadOptionAsync("~SerialNumber", cancellationToken).ConfigureAwait(false);

        return new DeviceInfo(string.IsNullOrWhiteSpace(model) ? "SC403" : model, serial, firmware, SupportedCapabilities);
    }

    public async Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return new DeviceStatus(DeviceConnectionState.Disconnected, DateTimeOffset.UtcNow, "ZK oturumu yok.",
                ZkTecoErrorCodes.Disconnected);
        }

        var sizes = await ReadSizesAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceStatus(DeviceConnectionState.Connected, DateTimeOffset.UtcNow,
            $"Kullanıcı {sizes.Users}/{sizes.UserCapacity}, kayıt {sizes.Records}/{sizes.RecordCapacity}.");
    }

    public async IAsyncEnumerable<CardReadEvent> ReadRealTimeCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureSession();
        if (!_eventsRegistered)
        {
            var reply = await ExecuteAsync(ZkCommands.RegisterEvent, LittleEndian(_eventFlags), cancellationToken)
                .ConfigureAwait(false);
            EnsureAck(reply, "olay kaydi");
            _eventsRegistered = true;
        }

        var reader = _events.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var packet))
            {
                var card = await ResolveEventAsync(packet, cancellationToken).ConfigureAwait(false);
                if (card is not null) yield return card;
            }
        }

        // Kanal kapandi: alici dongu bitti, yani soket oldu. Sessizce bitmek, adaptorun cihazi
        // hala "bagli" sanmasina ve hicbir kartin okunmamasina yol acardi.
        throw new ZkTecoProtocolException("ZK olay akisi kesildi; baglanti koptu.",
            isTransient: true, ZkTecoErrorCodes.Disconnected);
    }

    public Task<DeviceCommandResult?> SetUserInfoAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return WriteUserAsync(user.CardNumber, user.Name, cancellationToken);
    }

    public Task<DeviceCommandResult?> SetCardNumberAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        // Ad alanina cihazin sakladigi SAYISAL bicim yazilir ("0008573921" -> "8573921"). Ham
        // metin eski firmware'in 8 karakterlik alaninda "00085739" diye kirpiliyor ve teknisyen
        // cihaz ekranindaki adi kartla eslestiremiyordu (test yakaladi).
        return WriteUserAsync(cardNumber, ZkTecoCardNumber.Normalize(cardNumber), cancellationToken);
    }

    public async Task<DeviceCommandResult?> DeleteUserInfoAsync(string cardNumber, CancellationToken cancellationToken)
    {
        var card = ParseCard(cardNumber);
        await RefreshUsersAsync(cancellationToken).ConfigureAwait(false);
        var existing = FindByCard(card);
        if (existing is null)
        {
            // Kart cihazda zaten yok: amac (kartin gecememesi) saglanmis durumda.
            return new DeviceCommandResult(true, "Kart cihazda kayıtlı değildi.");
        }

        var reply = await ExecuteAsync(ZkCommands.DeleteUser, LittleEndian(existing.Uid), cancellationToken)
            .ConfigureAwait(false);
        EnsureAck(reply, "kart silme");
        _usersByUid.Remove(existing.Uid);
        await RefreshDataAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceCommandResult(true, $"Kart {cardNumber} cihazdan silindi (uid {existing.Uid}).");
    }

    /// <summary>
    /// Tum kullanicilari tek tek DELETE_USER ile siler (CMD_CLEAR_DATA gecis kayitlarini da
    /// silerdi; eski program o kayitlari okuyor). 444 kullanici ~20 sn surer.
    /// </summary>
    public async Task<int> ClearUsersAsync(CancellationToken cancellationToken)
    {
        await RefreshUsersAsync(cancellationToken).ConfigureAwait(false);
        var uids = _usersByUid.Keys.ToArray();
        foreach (var uid in uids)
        {
            var reply = await ExecuteAsync(ZkCommands.DeleteUser, LittleEndian(uid), cancellationToken).ConfigureAwait(false);
            EnsureAck(reply, "kullanıcı silme");
            _usersByUid.Remove(uid);
        }

        if (uids.Length > 0) await RefreshDataAsync(cancellationToken).ConfigureAwait(false);
        return uids.Length;
    }

    /// <summary>
    /// GUID cihaza hic yazilmadigi icin geri okunamaz; "kayit yok" dondurulur. Bu yolu cagiran
    /// bir tuketici yoktur (yalnizca adaptor icinde tanimlidir).
    /// </summary>
    public Task<DeviceUser?> GetUserInfoAsync(string externalUserId, CancellationToken cancellationToken)
    {
        EnsureSession();
        return Task.FromResult<DeviceUser?>(null);
    }

    public async Task<string?> GetUserIdByCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        var card = ParseCard(cardNumber);
        await RefreshUsersAsync(cancellationToken).ConfigureAwait(false);
        return FindByCard(card)?.Pin;
    }

    /// <summary>
    /// Kapi rolesini darbeler (uretici SDK: ACUnlock; protokol: CMD_UNLOCK). Veri, sure x 100 ms
    /// biriminde 32-bit sayidir (pyzk <c>unlock(time)</c>: <c>pack('I', time*10)</c>, saniye).
    /// </summary>
    public async Task<DeviceCommandResult?> UnlockAsync(TimeSpan pulse, CancellationToken cancellationToken)
    {
        var deciseconds = (uint)Math.Max(1, Math.Round(pulse.TotalMilliseconds / 100));
        var reply = await ExecuteAsync(ZkCommands.Unlock, LittleEndian(deciseconds), cancellationToken)
            .ConfigureAwait(false);
        EnsureAck(reply, "kapı rölesi");
        return new DeviceCommandResult(true, $"Kapı rölesi {deciseconds * 100} ms sürüldü.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await TearDownAsync(sendExit: true).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _commandLock.Dispose();
    }

    // ---- kullanici tablosu ---------------------------------------------------------------

    private async Task<DeviceCommandResult?> WriteUserAsync(string? cardNumber, string name,
        CancellationToken cancellationToken)
    {
        var card = string.IsNullOrWhiteSpace(cardNumber) ? 0u : ParseCard(cardNumber);
        await RefreshUsersAsync(cancellationToken).ConfigureAwait(false);

        var existing = card != 0 ? FindByCard(card) : null;
        var uid = existing?.Uid ?? AllocateUid();
        var pin = uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var record = BuildUserRecord(uid, pin, card, name);

        var reply = await ExecuteAsync(ZkCommands.UserWrite, record, cancellationToken).ConfigureAwait(false);
        // Cihaz reddi ISTISNA olarak yukselir, basarisiz sonuc olarak DEGIL: kart itme dongusu
        // sonucun Succeeded alanina bakmaz, yalnizca istisnaya bakar. Basarisiz sonuc donmek karti
        // "yuklendi" isaretletirdi.
        EnsureAck(reply, "kullanıcı yazma");
        _usersByUid[uid] = new ZkDeviceUser(uid, pin, card, name);
        await RefreshDataAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceCommandResult(true,
            existing is null ? $"Kart cihaza yazıldı (uid {uid})." : $"Kart cihazda güncellendi (uid {uid}).");
    }

    private ushort AllocateUid()
    {
        var next = _usersByUid.Count == 0 ? 1 : _usersByUid.Keys.Max() + 1;
        if (next > ushort.MaxValue)
        {
            throw new ZkTecoProtocolException("ZK cihazinda bos kullanici kimligi kalmadi.",
                isTransient: false, "ZK_MEMORY_FULL");
        }

        return (ushort)next;
    }

    private ZkDeviceUser? FindByCard(uint card) =>
        card == 0 ? null : _usersByUid.Values.FirstOrDefault(user => user.Card == card);

    private ZkDeviceUser? FindByPin(string pin) =>
        _usersByUid.Values.FirstOrDefault(user => string.Equals(user.Pin, pin, StringComparison.Ordinal));

    /// <summary>
    /// Kart numarasini cihazin bekledigi 32-bit sayiya cevirir. 125 kHz kartlar sayisaldir;
    /// rakam disi ya da sigmayan deger KALICI hatadir (kuyrukta tutmak sorunu gizler).
    /// </summary>
    internal static uint ParseCard(string cardNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        var normalized = ZkTecoCardNumber.Normalize(cardNumber);
        if (!uint.TryParse(normalized, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var card) || card == 0)
        {
            throw new ZkTecoProtocolException(
                $"Kart numarasi ZK cihazina yazilamaz: '{cardNumber}' 1-4294967295 arasinda bir sayi olmali.",
                isTransient: false, "ZK_INVALID_CARD");
        }

        return card;
    }

    private byte[] BuildUserRecord(ushort uid, string pin, uint card, string name)
    {
        if (_userRecordSize == LegacyUserRecordSize)
        {
            // pyzk set_user (28): '<HB5s8sIxBHI' uid, privilege, password, name, card, pad, group, timezone, user_id
            var record = new byte[LegacyUserRecordSize];
            BinaryPrimitives.WriteUInt16LittleEndian(record, uid);
            record[2] = 0;
            WriteAscii(record.AsSpan(3, 5), string.Empty);
            WriteAscii(record.AsSpan(8, 8), name);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), card);
            record[21] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), uint.Parse(pin, System.Globalization.CultureInfo.InvariantCulture));
            return record;
        }
        else
        {
            // pyzk set_user (72): '<HB8s24s4sx7sx24s' uid, privilege, password, name, card, pad, group, pad, user_id
            var record = new byte[ExtendedUserRecordSize];
            BinaryPrimitives.WriteUInt16LittleEndian(record, uid);
            record[2] = 0;
            WriteAscii(record.AsSpan(3, 8), string.Empty);
            WriteAscii(record.AsSpan(11, 24), name);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(35), card);
            WriteAscii(record.AsSpan(40, 7), "1");
            WriteAscii(record.AsSpan(48, 24), pin);
            return record;
        }
    }

    internal static IReadOnlyList<ZkDeviceUser> ParseUserRecords(ReadOnlySpan<byte> records, int recordSize)
    {
        var users = new List<ZkDeviceUser>();
        for (var offset = 0; offset + recordSize <= records.Length; offset += recordSize)
        {
            var record = records.Slice(offset, recordSize);
            var uid = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if (recordSize == LegacyUserRecordSize)
            {
                var name = DecodeString(record.Slice(8, 8));
                var card = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
                var pin = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                users.Add(new ZkDeviceUser(uid, pin, card, name));
            }
            else
            {
                var name = DecodeString(record.Slice(11, 24));
                var card = BinaryPrimitives.ReadUInt32LittleEndian(record[35..]);
                var pin = DecodeString(record.Slice(48, 24));
                users.Add(new ZkDeviceUser(uid, pin, card, name));
            }
        }

        return users;
    }

    private async Task RefreshUsersAsync(CancellationToken cancellationToken)
    {
        EnsureSession();
        var sizes = await ReadSizesAsync(cancellationToken).ConfigureAwait(false);
        var payload = await ReadBufferedAsync(ZkCommands.UserTemplateRead, ZkCommands.FunctionUser, cancellationToken)
            .ConfigureAwait(false);

        _usersByUid.Clear();
        if (payload.Length < 4) return;

        var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var records = payload.AsSpan(4);
        var recordSize = _forcedUserRecordSize ?? DetectRecordSize(total, sizes.Users, records.Length);
        _userRecordSize = recordSize;
        foreach (var user in ParseUserRecords(records, recordSize)) _usersByUid[user.Uid] = user;
    }

    /// <summary>pyzk get_users: kayit boyutu = toplam bayt / kullanici sayisi; 28 ya da 72 olmali.</summary>
    internal static int DetectRecordSize(int totalBytes, int userCount, int availableBytes)
    {
        if (userCount > 0 && totalBytes % userCount == 0)
        {
            var size = totalBytes / userCount;
            if (size is LegacyUserRecordSize or ExtendedUserRecordSize) return size;
        }

        if (availableBytes > 0 && availableBytes % ExtendedUserRecordSize == 0 && availableBytes % LegacyUserRecordSize != 0)
            return ExtendedUserRecordSize;
        return LegacyUserRecordSize;
    }

    /// <summary>
    /// pyzk read_with_buffer: DATA_WRRQ ile ister; kucuk tablo tek CMD_DATA olarak gelir, buyuk
    /// tablo PREPARE_DATA + READ_BUFFER parcalari + FREE_DATA ile okunur.
    /// </summary>
    private async Task<byte[]> ReadBufferedAsync(ushort command, byte function, CancellationToken cancellationToken)
    {
        var request = new byte[11];
        request[0] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(request.AsSpan(1), (short)command);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(3), function);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(7), 0);

        return await ExecuteLockedAsync(async token =>
        {
            var first = await SendAndReceiveAsync(ZkCommands.DataWriteReadRequest, request, token).ConfigureAwait(false);
            if (first.Command == ZkCommands.Data) return first.Data;
            if (first.Command == ZkCommands.AckError || first.Command == ZkCommands.AckUnauthorized)
            {
                EnsureAck(first, "kullanıcı tablosu");
            }

            if (first.Command != ZkCommands.PrepareData || first.Data.Length < 5)
            {
                throw new ZkTecoProtocolException($"ZK kullanici tablosu icin beklenmeyen yanit: {first.Command}.",
                    isTransient: false, ZkTecoErrorCodes.InvalidResponse);
            }

            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(first.Data.AsSpan(1));
            var buffer = new byte[size];
            var start = 0;
            while (start < size)
            {
                var chunk = Math.Min(MaxChunk, size - start);
                var chunkRequest = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(chunkRequest, start);
                BinaryPrimitives.WriteInt32LittleEndian(chunkRequest.AsSpan(4), chunk);
                var reply = await SendAndReceiveAsync(ZkCommands.ReadBuffer, chunkRequest, token).ConfigureAwait(false);

                if (reply.Command == ZkCommands.Data)
                {
                    reply.Data.AsSpan(0, Math.Min(reply.Data.Length, chunk)).CopyTo(buffer.AsSpan(start));
                    start += Math.Min(reply.Data.Length, chunk);
                    continue;
                }

                if (reply.Command != ZkCommands.PrepareData)
                {
                    throw new ZkTecoProtocolException($"ZK parca okuma beklenmeyen yanit: {reply.Command}.",
                        isTransient: false, ZkTecoErrorCodes.InvalidResponse);
                }

                var received = 0;
                while (received < chunk)
                {
                    var part = await ReadReplyAsync(token).ConfigureAwait(false);
                    if (part.Command == ZkCommands.AckOk) break;
                    if (part.Command != ZkCommands.Data)
                    {
                        throw new ZkTecoProtocolException($"ZK parca verisi beklenmeyen yanit: {part.Command}.",
                            isTransient: false, ZkTecoErrorCodes.InvalidResponse);
                    }

                    var copy = Math.Min(part.Data.Length, chunk - received);
                    part.Data.AsSpan(0, copy).CopyTo(buffer.AsSpan(start + received));
                    received += copy;
                }

                // Parca tamamlandiginda cihaz ACK_OK gonderir; parca tam dolduysa o ACK hala kuyruktadir.
                if (received >= chunk) await DrainAckAsync(token).ConfigureAwait(false);
                start += chunk;
            }

            await SendAndReceiveAsync(ZkCommands.FreeData, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
            return buffer;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAckAsync(CancellationToken cancellationToken)
    {
        using var brief = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        brief.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            var packet = await _replies.Reader.ReadAsync(brief.Token).ConfigureAwait(false);
            if (packet.Command != ZkCommands.AckOk) _replies.Writer.TryWrite(packet);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // ACK gelmediyse sorun degil; sonraki komut kuyrugu zaten temizler.
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task<(int Users, int UserCapacity, int Records, int RecordCapacity)> ReadSizesAsync(
        CancellationToken cancellationToken)
    {
        var reply = await ExecuteAsync(ZkCommands.GetFreeSizes, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        EnsureAck(reply, "cihaz durumu");
        if (reply.Data.Length < 80)
        {
            throw new ZkTecoProtocolException($"ZK boyut yaniti kisa: {reply.Data.Length} bayt.",
                isTransient: false, ZkTecoErrorCodes.InvalidResponse);
        }

        // pyzk read_sizes: 20 adet int32; kullanici=4, kayit=8, kullanici kapasitesi=15, kayit kapasitesi=17.
        int Field(int index) => BinaryPrimitives.ReadInt32LittleEndian(reply.Data.AsSpan(index * 4));
        return (Field(4), Field(15), Field(8), Field(17));
    }

    private async Task RefreshDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(ZkCommands.RefreshData, ReadOnlyMemory<byte>.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ZkTecoProtocolException exception)
        {
            // Yazma zaten onaylandi; tazeleme reddi bilgi amaclidir.
            _diagnostics?.Invoke($"ZK REFRESHDATA reddedildi: {exception.Message}");
        }
    }

    private async Task<string?> ReadOptionAsync(string name, CancellationToken cancellationToken)
    {
        var reply = await ExecuteAsync(ZkCommands.OptionsRead, Encoding.ASCII.GetBytes(name + "\0"), cancellationToken)
            .ConfigureAwait(false);
        if (reply.Command != ZkCommands.AckOk) return null;
        var text = DecodeString(reply.Data);
        var separator = text.IndexOf('=');
        return separator < 0 ? null : text[(separator + 1)..].Trim();
    }

    // ---- gercek zamanli olaylar -----------------------------------------------------------

    private async Task<CardReadEvent?> ResolveEventAsync(ZkPacket packet, CancellationToken cancellationToken)
    {
        // Olaylar BILGISAYAR saatiyle damgalanir. Sahada cihaz saati 83 dakika gerideydi (2026-09-08);
        // cihaz saatine guvenmek ogun penceresini, gece yarisina yakin gunu bile kaydirabilirdi.
        var receivedAt = _timeProvider.GetLocalNow();

        if (packet.SessionId == ZkCommands.Events.CardNumber)
        {
            if (!TryParseCardNumber(packet.Data, out var cardNumber))
            {
                _diagnostics?.Invoke($"ZK kart olayi cozulemedi: veri={Convert.ToHexString(packet.Data)}");
                return null;
            }

            _lastCardNumberEventAt = receivedAt;
            return new CardReadEvent(cardNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                receivedAt, "SC403");
        }

        if (!TryParseAttendance(packet.Data, out var pin, out var deviceTime))
        {
            _diagnostics?.Invoke($"ZK cozulemeyen olay: kod=0x{packet.SessionId:X4} veri={Convert.ToHexString(packet.Data)}");
            return null;
        }

        ReportClockSkew(deviceTime, receivedAt);

        // Sahada olculdu: kayitli kart okutulunca cihaz once kart olayini (0x0400), ~1,3 sn sonra ayni
        // okutmanin gecis kaydini (0x0001) gonderir. Ikisini de yukseltmek bir okutmayi iki gecis sayar
        // ve ogun hakkini iki kez duserdi. Pencere icindeki gecis kaydi yankidir; kart olayi UDP'de
        // kaybolursa pencere disinda kalan gecis kaydi yine yukselir, okutma kaybolmaz.
        if (receivedAt - _lastCardNumberEventAt < AttendanceEchoWindow)
        {
            _diagnostics?.Invoke($"ZK gecis kaydi (PIN {pin}) kart olayinin yankisi; atlandi.");
            return null;
        }

        var user = FindByPin(pin);
        if (user is null)
        {
            try { await RefreshUsersAsync(cancellationToken).ConfigureAwait(false); }
            catch (ZkTecoProtocolException exception)
            {
                _diagnostics?.Invoke($"ZK kullanici tablosu tazelenemedi: {exception.Message}");
            }

            user = FindByPin(pin);
        }

        if (user is null || user.Card == 0)
        {
            // Cihazda kayitli ama kartsiz/bilinmeyen kullanici: OLAY DUSURULMEZ. PIN, kart numarasi
            // yerine gecer; erisim karari bunu "taninmayan kart" olarak reddeder ve gunluge duser.
            _diagnostics?.Invoke($"ZK olay PIN {pin} bir karta eslenemedi.");
            return new CardReadEvent(pin, receivedAt, "SC403:pin");
        }

        return new CardReadEvent(user.Card.ToString(System.Globalization.CultureInfo.InvariantCulture), receivedAt, "SC403");
    }

    /// <summary>
    /// Kart numarasi olayi (0x0400) verisi. Sahada olculen ornek: "59 D7 7D 00 00" = uint32 LE
    /// 8247129 + bir dolgu bayti. Dort bayttan kisa ya da sifir kart gecersizdir.
    /// </summary>
    internal static bool TryParseCardNumber(ReadOnlySpan<byte> data, out uint cardNumber)
    {
        cardNumber = 0;
        if (data.Length < 4) return false;
        cardNumber = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return cardNumber != 0;
    }

    private void ReportClockSkew(DateTimeOffset deviceTime, DateTimeOffset receivedAt)
    {
        if (_clockSkewReported || deviceTime == default) return;
        var skew = deviceTime - receivedAt;
        if (skew.Duration() < ClockSkewTolerance) return;
        _clockSkewReported = true;
        _diagnostics?.Invoke(
            $"ZK cihaz saati bilgisayardan {skew.TotalMinutes:F0} dk sapmis (cihaz: {deviceTime:dd.MM.yyyy HH:mm:ss}); olaylar bilgisayar saatiyle damgalanir.");
    }

    /// <summary>
    /// pyzk live_capture: gecis kaydi olayinin veri uzunluguna gore yerlesimi. Kullanici alani
    /// 10/14 baytta 16-bit, 12 baytta 32-bit sayi, 32+ baytta 24 karakterlik metindir; zaman
    /// alti bayttir (yil-2000, ay, gun, saat, dakika, saniye).
    /// </summary>
    internal static bool TryParseAttendance(ReadOnlySpan<byte> data, out string pin, out DateTimeOffset timestamp)
    {
        pin = string.Empty;
        timestamp = default;
        ReadOnlySpan<byte> time;
        switch (data.Length)
        {
            case 10:
            case 14:
                pin = BinaryPrimitives.ReadUInt16LittleEndian(data).ToString(System.Globalization.CultureInfo.InvariantCulture);
                time = data.Slice(4, 6);
                break;
            case 12:
                pin = BinaryPrimitives.ReadUInt32LittleEndian(data).ToString(System.Globalization.CultureInfo.InvariantCulture);
                time = data.Slice(6, 6);
                break;
            case 32:
            case 36:
            case 37:
            case >= 52:
                pin = DecodeString(data[..24]);
                time = data.Slice(26, 6);
                break;
            default:
                return false;
        }

        if (string.IsNullOrWhiteSpace(pin)) return false;
        timestamp = DecodeTime(time);
        return true;
    }

    private static DateTimeOffset DecodeTime(ReadOnlySpan<byte> time)
    {
        try
        {
            var local = new DateTime(2000 + time[0], time[1], time[2], time[3], time[4], time[5], DateTimeKind.Local);
            return new DateTimeOffset(local);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Cihaz saati bozuksa olay yine yukselir (damga zaten bilgisayar saatidir); sapma
            // hesaplanamaz, bu yuzden "bilinmiyor" (default) doner.
            return default;
        }
    }

    // ---- komut alisverisi ---------------------------------------------------------------

    internal const string ReplyTimeoutCode = "ZK_REPLY_TIMEOUT";

    private Task<ZkPacket> ExecuteAsync(ushort command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
        bool requireSession = true)
    {
        if (requireSession) EnsureSession();
        return ExecuteLockedAsync(token => SendAndReceiveAsync(command, data, token), cancellationToken);
    }

    private async Task<T> ExecuteLockedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<ZkPacket> SendAndReceiveAsync(ushort command, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        // Onceki komuttan artakalan (gec gelen) bir yanit, bu komutun yaniti sanilmamali.
        while (_replies.Reader.TryRead(out _)) { }

        var packet = ZkPacket.Build(command, _session, _replyId, data.Span);
        await _transport.SendAsync(packet, cancellationToken).ConfigureAwait(false);
        var reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        _replyId = reply.ReplyId;   // pyzk: cihazin dondurdugu reply_id bir sonrakinin temelidir
        return reply;
    }

    private async Task<ZkPacket> ReadReplyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_replyTimeout);
        try
        {
            return await _replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // UDP'de kaybolan datagram: GECICI hata, adaptor yeniden dener.
            throw new ZkTecoProtocolException($"ZK cihazi {_replyTimeout.TotalSeconds:0.#} sn icinde yanit vermedi.",
                isTransient: true, ReplyTimeoutCode);
        }
        catch (ChannelClosedException exception)
        {
            _session = 0;
            throw new ZkTecoProtocolException("ZK baglantisi koptu.", isTransient: true,
                ZkTecoErrorCodes.Disconnected, exception);
        }
    }

    private void EnsureAck(ZkPacket reply, string operation)
    {
        switch (reply.Command)
        {
            case ZkCommands.AckOk:
            case ZkCommands.AckData:
                return;
            case ZkCommands.AckUnauthorized:
                _session = 0;
                throw new ZkTecoProtocolException($"ZK {operation}: oturum gecersiz (yeniden baglanti gerekir).",
                    isTransient: true, ZkTecoErrorCodes.Disconnected);
            case ZkCommands.AckError:
                throw new ZkTecoProtocolException($"ZK cihazi {operation} komutunu reddetti (ACK_ERROR).",
                    isTransient: false, "ZK_COMMAND_REJECTED");
            default:
                throw new ZkTecoProtocolException($"ZK {operation}: beklenmeyen yanit {reply.Command}.",
                    isTransient: false, ZkTecoErrorCodes.InvalidResponse);
        }
    }

    private void EnsureSession()
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new ZkTecoProtocolException("ZK oturumu yok; once baglanin.", isTransient: true,
                ZkTecoErrorCodes.Disconnected);
        }
    }

    // ---- alici dongu ve yasam dongusu ------------------------------------------------------

    private void StartPump()
    {
        _replies = Channel.CreateUnbounded<ZkPacket>(new UnboundedChannelOptions { SingleReader = true });
        _events = Channel.CreateUnbounded<ZkPacket>(new UnboundedChannelOptions { SingleReader = true });
        _pumpCancellation = new CancellationTokenSource();
        _pump = PumpAsync(_pumpCancellation.Token);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var replies = _replies;
        var events = _events;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] datagram;
                try
                {
                    datagram = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ZkTecoProtocolException) { break; }

                ZkPacket packet;
                try { packet = ZkPacket.Parse(datagram); }
                catch (ZkTecoProtocolException)
                {
                    _diagnostics?.Invoke($"ZK kisa datagram atlandi: {Convert.ToHexString(datagram)}");
                    continue;
                }

                if (packet.IsEvent) events.Writer.TryWrite(packet);
                else replies.Writer.TryWrite(packet);
            }
        }
        finally
        {
            // Dongu bitince bekleyen okuyucular uyandirilir: yanit bekleyen komut "koptu" der,
            // olay akisi kesildigini bildirir.
            replies.Writer.TryComplete();
            events.Writer.TryComplete();
        }
    }

    private async Task TearDownAsync(bool sendExit)
    {
        if (sendExit && _session != 0 && _transport.IsOpen)
        {
            try
            {
                using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ExecuteLockedAsync(token => SendAndReceiveAsync(ZkCommands.Exit, ReadOnlyMemory<byte>.Empty, token), brief.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ZkTecoProtocolException or OperationCanceledException or ObjectDisposedException)
            {
                // Kapanis nezaketi; cihaz cevap vermese de soket kapatilir.
            }
        }

        _session = 0;
        _eventsRegistered = false;
        var cancellation = Interlocked.Exchange(ref _pumpCancellation, null);
        cancellation?.Cancel();
        await _transport.CloseAsync().ConfigureAwait(false);
        var pump = Interlocked.Exchange(ref _pump, null);
        if (pump is not null)
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        }

        cancellation?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] LittleEndian(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static void WriteAscii(Span<byte> field, string value)
    {
        field.Clear();
        var bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }

    internal static string DecodeString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return Encoding.ASCII.GetString(bytes).Trim();
    }
}

/// <summary>Cihazin kullanici tablosundaki bir satir.</summary>
/// <param name="Uid">Cihazin 16-bit ic kimligi; silme bununla yapilir.</param>
/// <param name="Pin">Cihazin kullanici numarasi; gercek zamanli olaylarda bu gelir. Burada uid ile ayni tutulur.</param>
/// <param name="Card">Kart numarasi (32-bit); 0 = kartsiz.</param>
/// <param name="Name">Cihaz ekraninda gorunen ad; kart numarasi yazilir ki teknisyen eslestirebilsin.</param>
public sealed record ZkDeviceUser(ushort Uid, string Pin, uint Card, string Name);
