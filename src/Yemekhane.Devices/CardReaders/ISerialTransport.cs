namespace Yemekhane.Devices.CardReaders;

internal interface ISerialTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
