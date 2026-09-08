namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// ZK protokolu icin datagram tasima siniri. Gercek uygulama UDP'dir (<see cref="ZkUdpTransport"/>);
/// testler ayni uygulamayi loopback'teki sahte cihaza karsi kullanir, yani tasima katmani da
/// gercek soketle sinanir.
/// </summary>
public interface IZkTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(string host, int port, CancellationToken cancellationToken);

    Task CloseAsync();

    Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);

    /// <summary>Tek bir datagram bekler. Iptal edilene ya da veri gelene kadar doner.</summary>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}
