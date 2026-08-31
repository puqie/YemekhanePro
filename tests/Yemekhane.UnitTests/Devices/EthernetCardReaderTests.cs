using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.CardReaders;

namespace Yemekhane.UnitTests.Devices;

public sealed class EthernetCardReaderTests
{
    private static readonly DeviceEndpoint Endpoint = new("Ethernet", IpAddress: "reader.local", IpPort: 4370);

    [Fact]
    public async Task ConnectAsyncSetsConnectedOnlyAfterTransportConnects()
    {
        var transport = new FakeTcpTransport();
        await using var reader = CreateReader(transport);

        var info = await reader.ConnectAsync(CancellationToken.None);

        Assert.True(transport.IsConnected);
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
        Assert.Contains(DeviceCapability.ReadCard, info.Capabilities);
    }

    [Fact]
    public async Task ConnectAsyncFailsWhenTransportDoesNotActuallyConnect()
    {
        var transport = new FakeTcpTransport { RemainDisconnectedOnConnect = true };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("TCP_CONNECT_FAILED", exception.ErrorCode);
        Assert.NotEqual(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    [Fact]
    public async Task ConnectAsyncReportsTimeout()
    {
        var transport = new FakeTcpTransport { BlockConnect = true };
        await using var reader = CreateReader(transport, TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("TCP_CONNECT_TIMEOUT", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.NetworkUnreachable)]
    public async Task ConnectAsyncWrapsDnsAndUnreachableNetworkFailures(SocketError socketError)
    {
        var transport = new FakeTcpTransport
        {
            ConnectException = new SocketException((int)socketError)
        };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("TCP_CONNECT_FAILED", exception.ErrorCode);
        Assert.Contains("reader.local:4370", exception.Message, StringComparison.Ordinal);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ConnectAsyncWrapsAuthenticationFailure()
    {
        var transport = new FakeTcpTransport
        {
            ConnectException = new System.Security.Authentication.AuthenticationException("auth failed")
        };
        await using var reader = CreateReader(transport);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Equal("TCP_CONNECT_FAILED", exception.ErrorCode);
        Assert.IsType<System.Security.Authentication.AuthenticationException>(exception.InnerException);
    }

    [Fact]
    public async Task ConnectAsyncPropagatesCallerCancellation()
    {
        var transport = new FakeTcpTransport { BlockConnect = true };
        await using var reader = CreateReader(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ConnectAsync(cancellation.Token));
        Assert.NotEqual(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    [Fact]
    public async Task ReadCardsAsyncParsesTerminatedFramesAndIgnoresInvalidFrames()
    {
        var transport = new FakeTcpTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        transport.Queue("\r\ninvalid card\n123");
        transport.Queue("45\rABC-_9\n");
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("12345", enumerator.Current.CardNumber);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("ABC-_9", enumerator.Current.CardNumber);
        Assert.Equal("Test Ethernet reader", enumerator.Current.ReaderSource);
    }

    [Fact]
    public async Task ReadCardsAsyncPropagatesCallerCancellation()
    {
        var transport = new FakeTcpTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = reader.ReadCardsAsync(cancellation.Token).GetAsyncEnumerator();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task ReadCardsAsyncReportsTimeoutWithoutFakingDisconnect()
    {
        var transport = new FakeTcpTransport();
        await using var reader = CreateReader(transport, readTimeout: TimeSpan.FromMilliseconds(20));
        await reader.ConnectAsync(CancellationToken.None);
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("TCP_READ_TIMEOUT", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    [Fact]
    public async Task ReadCardsAsyncReportsRemoteDisconnect()
    {
        var transport = new FakeTcpTransport();
        await using var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);
        transport.CompleteReads();
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("TCP_DISCONNECTED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }

    [Fact]
    public async Task DisconnectAndDisposeAreIdempotent()
    {
        var transport = new FakeTcpTransport();
        var reader = CreateReader(transport);
        await reader.ConnectAsync(CancellationToken.None);

        await reader.DisconnectAsync(CancellationToken.None);
        await reader.DisconnectAsync(CancellationToken.None);
        await reader.DisposeAsync();
        await reader.DisposeAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
        Assert.False(transport.IsConnected);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("COM", "reader.local", 4370)]
    [InlineData("Ethernet", "bad host!", 4370)]
    [InlineData("Ethernet", "reader.local", 0)]
    [InlineData("Ethernet", "reader.local", 65536)]
    public void ConstructorRejectsInvalidEndpoint(string connectionType, string host, int port)
    {
        var endpoint = new DeviceEndpoint(connectionType, IpAddress: host, IpPort: port);

        Assert.ThrowsAny<ArgumentException>(() =>
            new EthernetCardReader(Guid.NewGuid(), "Reader", endpoint));
    }

    [Fact]
    public async Task RealTransportConnectsAndReadsFromLoopbackListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = new DeviceEndpoint("Ethernet", IpAddress: IPAddress.Loopback.ToString(), IpPort: port);
        await using var reader = new EthernetCardReader(Guid.NewGuid(), "Loopback reader", endpoint,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        var connectTask = reader.ConnectAsync(CancellationToken.None);
        using var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await connectTask;
        await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("LOOP-123\r\n"));
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("LOOP-123", enumerator.Current.CardNumber);
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    [Fact]
    public async Task RealTransportDetectsLoopbackRemoteClose()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = new DeviceEndpoint("Ethernet", IpAddress: "127.0.0.1", IpPort: port);
        await using var reader = new EthernetCardReader(Guid.NewGuid(), "Loopback reader", endpoint,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        var connectTask = reader.ConnectAsync(CancellationToken.None);
        var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await connectTask;
        server.Dispose();
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("TCP_DISCONNECTED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }

    private static EthernetCardReader CreateReader(FakeTcpTransport transport,
        TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null) =>
        new(Guid.NewGuid(), "Test Ethernet reader", Endpoint, transport, connectTimeout, readTimeout);

    private sealed class FakeTcpTransport : ITcpTransport
    {
        private readonly Channel<byte[]> _reads = Channel.CreateUnbounded<byte[]>();

        public bool IsConnected { get; private set; }

        public bool BlockConnect { get; init; }

        public bool RemainDisconnectedOnConnect { get; init; }
        public Exception? ConnectException { get; init; }

        public int DisposeCount { get; private set; }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (ConnectException is not null) throw ConnectException;
            if (BlockConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = !RemainDisconnectedOnConnect;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var data = await _reads.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer);
            return data.Length;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public void Queue(string value) => _reads.Writer.TryWrite(Encoding.ASCII.GetBytes(value));

        public void CompleteReads()
        {
            IsConnected = false;
            _reads.Writer.TryWrite([]);
        }
    }
}
