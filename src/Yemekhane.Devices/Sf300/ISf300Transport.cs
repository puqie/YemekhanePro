namespace Yemekhane.Devices.Sf300;

/// <summary>
/// SF300 icin cift yonlu bayt kanali. Gercek cihazda TCP, testlerde bellek-ici bir sahte kullanilir.
/// </summary>
public interface ISf300Transport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
