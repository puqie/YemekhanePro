namespace Yemekhane.Devices.CardReaders;

internal interface ITcpTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}
