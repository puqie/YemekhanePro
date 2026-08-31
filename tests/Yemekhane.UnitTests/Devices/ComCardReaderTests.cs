using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.CardReaders;

namespace Yemekhane.UnitTests.Devices;

public sealed class ComCardReaderTests
{
    private static readonly DeviceEndpoint Endpoint = new("COM", "COM_TEST", 9600);

    [Fact]
    public async Task ConnectAsyncSetsConnectedOnlyAfterTransportOpens()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport);

        var info = await reader.ConnectAsync(CancellationToken.None);

        Assert.True(transport.IsOpen);
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
        Assert.Contains(DeviceCapability.ReadCard, info.Capabilities);
    }

    [Fact]
    public async Task ReadCardsAsyncIgnoresEmptyAndInvalidLines()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        transport.Queue("\r\nbad card\n12345\r\nABC-_9\n");
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("12345", enumerator.Current.CardNumber);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("ABC-_9", enumerator.Current.CardNumber);
    }

    [Fact]
    public async Task ReadCardsAsyncPropagatesCallerCancellation()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = reader.ReadCardsAsync(cancellation.Token).GetAsyncEnumerator();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task ConnectAsyncWrapsOpenFailureAndDoesNotReportConnected()
    {
        var transport = new FakeSerialTransport { OpenException = new IOException("Port unavailable") };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("COM_OPEN_FAILED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Faulted, reader.ConnectionState);
    }

    [Fact]
    public async Task AccessDeniedOnPortIncludesPortAndStableErrorCode()
    {
        var transport = new FakeSerialTransport { OpenException = new UnauthorizedAccessException("denied") };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("COM_OPEN_FAILED", exception.ErrorCode);
        Assert.Contains(Endpoint.ComPort!, exception.Message, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
    }

    [Fact]
    public async Task ConnectAsyncFailsWhenTransportDoesNotActuallyOpen()
    {
        var transport = new FakeSerialTransport { RemainClosedOnOpen = true };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("COM_OPEN_FAILED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Faulted, reader.ConnectionState);
    }

    [Fact]
    public async Task ReadCardsAsyncReportsTimeout()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport, TimeSpan.FromMilliseconds(20));
        await reader.ConnectAsync(CancellationToken.None);
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("COM_READ_TIMEOUT", exception.ErrorCode);
    }

    [Fact]
    public async Task ReadCardsAsyncReportsDisconnect()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        transport.CompleteReads();
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("COM_DISCONNECTED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }

    [Fact]
    public async Task UnplugDuringReadReportsDisconnectInsteadOfLeakingTransportException()
    {
        var transport = new FakeSerialTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        transport.ReadException = new IOException("device removed");
        transport.CloseBeforeReadFailure = true;
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("COM_DISCONNECTED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }


    [Fact]
    public async Task WedgedSerialPortFailsWithinConnectTimeoutInsteadOfHangingForever()
    {
        var transport = new FakeSerialTransport { HangOnOpen = true };
        await using var reader = CreateReader(transport, connectTimeout: TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<DeviceConnectionException>(() => reader.ConnectAsync(default));

        Assert.Equal("COM_CONNECT_TIMEOUT", error.ErrorCode);
        Assert.Equal(DeviceConnectionState.Faulted, reader.ConnectionState);
    }

    [Fact]
    public async Task DisposeClosesAndDisposesPortExactlyOnceAfterFailure()
    {
        var transport = new FakeSerialTransport { OpenException = new UnauthorizedAccessException() };
        var reader = CreateReader(transport);
        await Assert.ThrowsAsync<DeviceConnectionException>(() => reader.ConnectAsync(default));

        await reader.DisposeAsync();
        await reader.DisposeAsync();

        Assert.False(transport.IsOpen);
        Assert.Equal(1, transport.DisposeCount);
    }

    private static ComCardReader CreateReader(FakeSerialTransport transport, TimeSpan? timeout = null,
        TimeSpan? connectTimeout = null) =>
        new(Guid.NewGuid(), "Test reader", Endpoint, transport, timeout, connectTimeout);

    private sealed class FakeSerialTransport : ISerialTransport
    {
        private readonly Channel<byte[]> _reads = Channel.CreateUnbounded<byte[]>();

        public bool IsOpen { get; private set; }

        public Exception? OpenException { get; init; }

        public bool RemainClosedOnOpen { get; init; }

        /// <summary>Takılı kalan bir seri portu taklit eder: açma çağrısı hiç tamamlanmaz.</summary>
        public bool HangOnOpen { get; init; }
        public Exception? ReadException { get; set; }
        public bool CloseBeforeReadFailure { get; set; }
        public int DisposeCount { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenException is not null)
            {
                throw OpenException;
            }

            if (HangOnOpen)
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            IsOpen = !RemainClosedOnOpen;
            return Task.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (ReadException is not null)
            {
                if (CloseBeforeReadFailure) IsOpen = false;
                throw ReadException;
            }

            var data = await _reads.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer);
            return data.Length;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsOpen = false;
            return ValueTask.CompletedTask;
        }

        public void Queue(string value) => _reads.Writer.TryWrite(System.Text.Encoding.ASCII.GetBytes(value));

        public void CompleteReads() => _reads.Writer.TryWrite([]);
    }
}
