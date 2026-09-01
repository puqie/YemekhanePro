using System.Text;
using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Sf300;

/// <summary>
/// SF300 protokolunun STX/ETX cerceveli uygulamasi.
///
/// Cihaz tek bir TCP baglantisi uzerinden hem istek/yanit hem de kendiliginden kart olaylari gonderir.
/// Bu yuzden tek bir okuma dongusu tum cerceveleri toplar ve turune gore ayirir: kart olaylari akisa,
/// diger yanitlar bekleyen komuta gider. Her komutun kendi okumasini yapmasi, arada gelen bir kart
/// olayinin yanit sanilmasina yol acardi.
/// </summary>
public sealed class Sf300Protocol : ISf300Protocol
{
    private static readonly IReadOnlySet<DeviceCapability> DefaultCapabilities = new HashSet<DeviceCapability>
    {
        DeviceCapability.DeviceInfo, DeviceCapability.Status, DeviceCapability.ReadCard,
        DeviceCapability.ReadUser, DeviceCapability.SendCard, DeviceCapability.SendUser,
        DeviceCapability.SyncCard, DeviceCapability.SyncUser, DeviceCapability.DeleteCard,
        DeviceCapability.GrantAccess, DeviceCapability.DenyAccess
    };

    private readonly ISf300Transport _transport;
    private readonly TimeSpan _responseTimeout;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Channel<CardReadEvent> _cardEvents = Channel.CreateBounded<CardReadEvent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stopping = new();
    private Channel<Sf300Response>? _responses;
    private Task? _readLoop;
    private int _disposed;

    public Sf300Protocol(ISf300Transport transport, TimeSpan? responseTimeout = null)
    {
        _transport = transport;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
    }

    public bool IsConnected => _transport.IsConnected;

    public async Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(endpoint.IpAddress) || endpoint.IpPort is null)
            throw new Sf300ProtocolException("SF300 icin IP adresi ve port zorunludur.", errorCode: "SF300_ENDPOINT_INVALID");

        await _transport.ConnectAsync(endpoint.IpAddress, endpoint.IpPort.Value, cancellationToken).ConfigureAwait(false);
        _responses = Channel.CreateUnbounded<Sf300Response>();
        _readLoop = Task.Run(() => ReadLoopAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var text = await SendAsync(Sf300Command.Handshake, string.Empty, cancellationToken).ConfigureAwait(false);
        var parts = text.Split('|');
        if (string.IsNullOrWhiteSpace(parts[0]))
            throw new Sf300ProtocolException("SF300 cihaz bilgisi yaniti cozumlenemedi.", errorCode: "SF300_INVALID_RESPONSE");

        return new DeviceInfo(parts[0], parts.Length > 1 ? Empty(parts[1]) : null,
            parts.Length > 2 ? Empty(parts[2]) : null, DefaultCapabilities);
    }

    public async Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var text = await SendAsync(Sf300Command.Status, string.Empty, cancellationToken).ConfigureAwait(false);
        var state = text.StartsWith("READY", StringComparison.OrdinalIgnoreCase)
            ? DeviceConnectionState.Connected
            : DeviceConnectionState.Faulted;
        return new DeviceStatus(state, DateTimeOffset.UtcNow, text);
    }

    public IAsyncEnumerable<CardReadEvent> ReadCardsAsync(CancellationToken cancellationToken) =>
        _cardEvents.Reader.ReadAllAsync(cancellationToken);

    public Task<DeviceCommandResult?> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
        CommandAsync(Sf300Command.GrantAccess, direction.ToString(), cancellationToken);

    public Task<DeviceCommandResult?> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
        CommandAsync(Sf300Command.DenyAccess, direction.ToString(), cancellationToken);

    public Task<DeviceCommandResult?> SendUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
        CommandAsync(Sf300Command.SendUser,
            $"{user.ExternalId}|{user.Name}|{user.CardNumber}|{user.Pid}", cancellationToken);

    public Task<DeviceCommandResult?> SendCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken) =>
        CommandAsync(Sf300Command.SendCard, $"{cardNumber}|{externalUserId}", cancellationToken);

    public Task<DeviceCommandResult?> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
        SendUserAsync(user, cancellationToken);

    public Task<DeviceCommandResult?> SyncCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken) =>
        SendCardAsync(cardNumber, externalUserId, cancellationToken);

    public Task<DeviceCommandResult?> DeleteCardAsync(string cardNumber, CancellationToken cancellationToken) =>
        CommandAsync(Sf300Command.DeleteCard, cardNumber, cancellationToken);

    public async Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken)
    {
        var text = await SendAsync(Sf300Command.ReadUser, externalUserId, cancellationToken).ConfigureAwait(false);
        if (text.Length == 0) return null;
        var parts = text.Split('|');
        return new DeviceUser(parts[0], parts.Length > 1 ? parts[1] : string.Empty,
            parts.Length > 2 ? Empty(parts[2]) : null, null, parts.Length > 3 ? Empty(parts[3]) : null);
    }

    public async Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        var text = await SendAsync(Sf300Command.ReadCard, cardNumber, cancellationToken).ConfigureAwait(false);
        return text.Length == 0 ? null : text;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _cardEvents.Writer.TryComplete();
        await _transport.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _commandLock.Dispose();
    }

    private async Task<DeviceCommandResult?> CommandAsync(Sf300Command command, string payload,
        CancellationToken cancellationToken)
    {
        var text = await SendAsync(command, payload, cancellationToken).ConfigureAwait(false);
        return new DeviceCommandResult(true, text.Length > 0 ? text : "OK");
    }

    /// <summary>
    /// Tek bir komutu gonderir ve yanitin metnini dondurur. Komutlar seri calisir:
    /// SF300 tek kanal uzerinden yanit verdiginden es zamanli iki komut yanitlarini karistirirdi.
    /// </summary>
    private async Task<string> SendAsync(Sf300Command command, string payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var responses = _responses ?? throw new Sf300ProtocolException("SF300 baglantisi kurulmadi.",
            errorCode: "SF300_DISCONNECTED");

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Onceki komuttan artakalan yanit varsa temizlenir; aksi halde bu komut yanlis yaniti okur.
            while (responses.Reader.TryRead(out _)) { }

            await _transport.WriteAsync(Sf300Frame.Encode(command, Encoding.ASCII.GetBytes(payload)), cancellationToken)
                .ConfigureAwait(false);

            // Cihaz hic yanit vermezse cagiran sonsuza kadar beklememelidir.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            timeout.CancelAfter(_responseTimeout);
            Sf300Response response;
            try
            {
                response = await responses.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new Sf300ProtocolException("SF300 yanit vermedi.", isTransient: true, "SF300_TIMEOUT");
            }
            catch (ChannelClosedException exception)
            {
                throw new Sf300ProtocolException("SF300 baglantisi kapandi.", isTransient: true,
                    "SF300_DISCONNECTED", exception);
            }

            if (response.Error is { } error)
                throw new Sf300ProtocolException(error, isTransient: true, "SF300_FRAME_ERROR");
            if (response.Frame!.Command == Sf300Command.Nak)
                throw NakToException(Text(response.Frame));
            // Cihazin yanit vermesi komutu onayladigi anlamina gelmez: beklenen yanit kodu
            // gelmediyse kanal senkronizasyonu bozulmustur (gec gelen bir onceki yanit, kendiliginden
            // gonderilen bir cerceve...). Boyle bir cerceveyi basari saymak, karti hic yazmamis bir
            // turnikeyi "yuklendi" olarak isaretler. Gecici sayilir ki mevcut yeniden deneme calissin.
            if (response.Frame.Command != ExpectedResponse(command))
            {
                throw new Sf300ProtocolException(
                    $"SF300 {command} komutuna beklenmeyen yanit dondurdu: {response.Frame.Command}.",
                    isTransient: true, "SF300_UNEXPECTED_RESPONSE");
            }

            return Text(response.Frame);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Bir komutun gecerli sayilan yanit kodu. Sorgu komutlari kendi kodlariyla yanitlanir
    /// (veriyi tasirlar), digerleri ACK ile onaylanir.
    /// </summary>
    private static Sf300Command ExpectedResponse(Sf300Command command) => command switch
    {
        Sf300Command.Handshake => Sf300Command.Handshake,
        Sf300Command.Status => Sf300Command.Status,
        Sf300Command.ReadCard => Sf300Command.ReadCard,
        Sf300Command.ReadUser => Sf300Command.ReadUser,
        _ => Sf300Command.Ack
    };

    /// <summary>
    /// NAK kodunu gecici/kalici olarak siniflandirir. Yanlis siniflandirma pahalidir:
    /// kalici bir hatayi gecici saymak sonsuz yeniden denemeye, tersi ise gecici bir
    /// mesguliyette kart yuklemesinin kalici basarisiz sayilmasina yol acar.
    /// </summary>
    private static Sf300ProtocolException NakToException(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var transient = normalized is "BUSY" or "TIMEOUT" or "RETRY" or "QUEUE_FULL" or "TEMP_ERROR";
        return new Sf300ProtocolException($"SF300 komutu reddetti: {normalized}", transient,
            $"SF300_{(normalized.Length == 0 ? "NAK" : normalized)}");
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var pending = new List<byte>(512);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                pending.AddRange(buffer.AsSpan(0, read));
                DrainFrames(pending);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Baglanti koptu; bekleyen komut kanal kapanisiyla hata alir.
        }
        finally
        {
            _cardEvents.Writer.TryComplete();
            _responses?.Writer.TryComplete();
        }
    }

    private void DrainFrames(List<byte> pending)
    {
        while (true)
        {
            var start = pending.IndexOf(Sf300Frame.Stx);
            if (start < 0) { pending.Clear(); return; }
            if (start > 0) pending.RemoveRange(0, start);
            if (pending.Count < 5) return;

            var total = pending[1] + 3;
            if (total < 5) { pending.RemoveRange(0, 1); continue; }
            if (pending.Count < total) return;

            var candidate = pending.GetRange(0, total).ToArray();
            pending.RemoveRange(0, total);

            if (!Sf300Frame.TryDecode(candidate, out var frame, out var decodeError) || frame is null)
            {
                // Bozuk cerceve sessizce atilamaz: bekleyen komut zaman asimina kadar asili kalir ve
                // cagiran "cihaz yanit vermedi" sanir. Hata dogrudan bekleyen komuta iletilir.
                _responses?.Writer.TryWrite(new Sf300Response(null, decodeError ?? "SF300 cerceve cozumlenemedi."));
                continue;
            }

            if (frame.Command == Sf300Command.CardEvent)
            {
                var parts = Encoding.ASCII.GetString(frame.Payload.Span).Split('|');
                if (parts.Length > 0 && parts[0].Length > 0)
                    _cardEvents.Writer.TryWrite(new CardReadEvent(parts[0], DateTimeOffset.UtcNow, "SF300"));
                continue;
            }

            _responses?.Writer.TryWrite(new Sf300Response(frame, null));
        }
    }

    private static string Text(Sf300Frame frame) => Encoding.ASCII.GetString(frame.Payload.Span).Trim();
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Okuma dongusunden bekleyen komuta tasinan yanit veya cerceve hatasi.</summary>
    private sealed record Sf300Response(Sf300Frame? Frame, string? Error);
}
