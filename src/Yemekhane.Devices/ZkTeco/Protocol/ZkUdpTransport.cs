using System.Net.Sockets;

namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// ZK protokolu icin UDP tasima.
///
/// <para>
/// NEDEN UDP: ZEM tabanli eski firmware (ornegin ZEM500, Linux 2.4.20) 4370 portunu YALNIZCA UDP
/// olarak dinler; TCP 4370 kapalidir. Uretici SDK'si (zkemkeeper.dll) ve pyzk <c>force_udp</c>
/// ayni yolu kullanir. Sahada TCP sondasi "kapali" dondugu icin cihaz bozuk sanildi -- yanlis
/// katman sinanmisti. Bu sinif o dersin urunudur.
/// </para>
/// </summary>
public sealed class ZkUdpTransport : IZkTransport
{
    /// <summary>
    /// Windows'a ozgu: karsi port dinlemiyorsa gelen ICMP "port unreachable", bagli UDP soketinde
    /// bir SONRAKI ReceiveAsync'i ConnectionReset ile dusurur. Bu, zaman asimindan ayirt edilemeyen
    /// ve yeniden denemeyi bozan bir hata gibi gorunur; SIO_UDP_CONNRESET ile kapatilir.
    /// </summary>
    private const int SioUdpConnReset = -1744830452;

    private UdpClient? _client;
    private int _disposed;

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _client is not null;

    public Task OpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not null) return Task.CompletedTask;

        var client = new UdpClient();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try { client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
                catch (SocketException) { /* Eski Windows surumlerinde desteklenmeyebilir; zararsiz. */ }
            }

            // UDP'de Connect ag trafigi uretmez; yalnizca hedefi sabitler ve DNS cozer.
            client.Connect(host, port);
            _client = client;
            return Task.CompletedTask;
        }
        catch (SocketException exception)
        {
            client.Dispose();
            throw new ZkTecoProtocolException($"ZK cihazina UDP soketi acilamadi ({host}:{port}): {exception.Message}",
                isTransient: true, ZkTecoErrorCodes.ConnectFailed, exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task CloseAsync()
    {
        Interlocked.Exchange(ref _client, null)?.Dispose();
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new ZkTecoProtocolException("ZK UDP soketi kapali.",
            isTransient: true, ZkTecoErrorCodes.Disconnected);
        try
        {
            await client.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            throw new ZkTecoProtocolException("ZK cihazina yazilamadi.", isTransient: true,
                "ZK_WRITE_FAILED", exception);
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var client = _client ?? throw new ZkTecoProtocolException("ZK UDP soketi kapali.",
            isTransient: true, ZkTecoErrorCodes.Disconnected);
        try
        {
            var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (SocketException exception)
        {
            // ConnectionReset burada "karsi tarafta dinleyen yok" demektir (ICMP port unreachable).
            throw new ZkTecoProtocolException($"ZK cihazindan okunamadi: {exception.SocketErrorCode}.",
                isTransient: true, ZkTecoErrorCodes.Disconnected, exception);
        }
        catch (ObjectDisposedException exception)
        {
            throw new ZkTecoProtocolException("ZK UDP soketi kapatildi.", isTransient: true,
                ZkTecoErrorCodes.Disconnected, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync().ConfigureAwait(false);
    }
}
