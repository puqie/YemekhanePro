using System.Net.Sockets;

namespace Yemekhane.Devices.Sf300;

/// <summary>
/// SF300 icin gercek TCP kanali. Cihaz tek bir kalici baglanti uzerinden hem yanit hem de
/// kendiliginden kart olayi gonderdiginden soket acik tutulur ve NoDelay ile kucuk cerceveler
/// bekletilmeden iletilir (Nagle algoritmasi turnike yanitina gecikme eklerdi).
/// </summary>
public sealed class Sf300TcpTransport : ISf300Transport
{
    private readonly TimeSpan _connectTimeout;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _disposed;

    public Sf300TcpTransport(TimeSpan? connectTimeout = null) =>
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);

    public bool IsConnected
    {
        get
        {
            var socket = _client?.Client;
            if (Volatile.Read(ref _disposed) != 0 || socket is null || !socket.Connected) return false;
            try
            {
                // Poll+Available: karsi taraf baglantiyi kapattiginda Connected hala true kalabilir.
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsConnected) return;

        await CloseAsync().ConfigureAwait(false);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new Sf300ProtocolException($"SF300 cihazina baglanilamadi ({host}:{port}): zaman asimi.",
                isTransient: true, "SF300_CONNECT_TIMEOUT");
        }
        catch (SocketException exception)
        {
            client.Dispose();
            throw new Sf300ProtocolException($"SF300 cihazina baglanilamadi ({host}:{port}): {exception.Message}",
                isTransient: true, "SF300_CONNECT_FAILED", exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => CloseAsync();

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new Sf300ProtocolException("SF300 baglantisi kapali.",
            isTransient: true, "SF300_DISCONNECTED");
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            throw new Sf300ProtocolException("SF300 cihazina yazilamadi.", isTransient: true,
                "SF300_WRITE_FAILED", exception);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new Sf300ProtocolException("SF300 baglantisi kapali.",
            isTransient: true, "SF300_DISCONNECTED");
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // Okuma dongusu bunu baglanti kopmasi olarak degerlendirir.
            return 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var client = Interlocked.Exchange(ref _client, null);
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
    }
}
