using System.Runtime.CompilerServices;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Sf300;

namespace Yemekhane.UnitTests.Devices;

// These tests exercise the protocol seam and adapter policy; they are not an SF300 simulator.
public sealed class SF300AdapterTests
{
    private static readonly DeviceEndpoint Endpoint =
        new("Ethernet", IpAddress: "sf300.test", IpPort: 12345);

    private static readonly DeviceCapability[] AllCapabilities = Enum.GetValues<DeviceCapability>();

    [Fact]
    public async Task ConnectWithoutDocumentedProtocolReportsNotConfigured()
    {
        await using var adapter = new SF300Adapter(Guid.NewGuid(), "SF300", Endpoint);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal("SF300_PROTOCOL_NOT_CONFIGURED", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, adapter.ConnectionState);
    }

    [Fact]
    public async Task StatusWithoutDocumentedProtocolReportsNotConfigured()
    {
        await using var adapter = new SF300Adapter(Guid.NewGuid(), "SF300", Endpoint);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.GetStatusAsync(CancellationToken.None));

        Assert.Equal("SF300_PROTOCOL_NOT_CONFIGURED", exception.ErrorCode);
    }

    [Fact]
    public async Task TcpConnectionAloneDoesNotCountAsConnectedWithoutValidHandshake()
    {
        var protocol = new FakeSf300Protocol { DeviceInfo = null };
        await using var adapter = CreateAdapter(protocol);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal("SF300_HANDSHAKE_INVALID_RESPONSE", exception.ErrorCode);
        Assert.Equal(1, protocol.ConnectCalls);
        Assert.Equal(1, protocol.DeviceInfoCalls);
        Assert.Equal(1, protocol.DisconnectCalls);
        Assert.NotEqual(DeviceConnectionState.Connected, adapter.ConnectionState);
    }

    [Fact]
    public async Task ConnectPublishesOnlyHandshakeCapabilities()
    {
        var capabilities = new[] { DeviceCapability.DeviceInfo, DeviceCapability.Status, DeviceCapability.GrantAccess };
        var protocol = new FakeSf300Protocol { DeviceInfo = CreateInfo(capabilities) };
        await using var adapter = CreateAdapter(protocol);

        var info = await adapter.ConnectAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Connected, adapter.ConnectionState);
        Assert.Equal(capabilities.OrderBy(value => value), adapter.Capabilities.OrderBy(value => value));
        Assert.Equal(capabilities.OrderBy(value => value), info.Capabilities.OrderBy(value => value));
    }

    [Fact]
    public async Task HandshakeTimeoutClosesConnectionAndNeverSetsConnected()
    {
        var protocol = new FakeSf300Protocol { BlockDeviceInfo = true };
        await using var adapter = CreateAdapter(protocol, TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal("SF300_TIMEOUT", exception.ErrorCode);
        Assert.Equal(1, protocol.DisconnectCalls);
        Assert.NotEqual(DeviceConnectionState.Connected, adapter.ConnectionState);
    }

    [Fact]
    public async Task RetryCountIsBoundedAndRequiresTransientProtocolFailure()
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            TransientStatusFailures = 2
        };
        await using var adapter = CreateAdapter(protocol, maxRetryCount: 2);
        await adapter.ConnectAsync(CancellationToken.None);

        var status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Connected, status.State);
        Assert.Equal(3, protocol.StatusCalls);
    }

    [Theory]
    [InlineData("SF300_MALFORMED_RESPONSE")]
    [InlineData("SF300_CHECKSUM_INVALID")]
    [InlineData("SF300_AUTH_FAILED")]
    public async Task ProtocolValidationFailuresPreserveActionableCodeAndAreNotRetried(string errorCode)
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            StatusFailure = new Sf300ProtocolException("Geçersiz cihaz yanıtı", errorCode: errorCode)
        };
        await using var adapter = CreateAdapter(protocol, maxRetryCount: 3);
        await adapter.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.GetStatusAsync(CancellationToken.None));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(1, protocol.StatusCalls);
    }

    [Fact]
    public async Task AmbiguousCommandTimeoutIsNeverRetried()
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            BlockGrantAccess = true
        };
        await using var adapter = CreateAdapter(protocol, TimeSpan.FromMilliseconds(20), maxRetryCount: 3);
        await adapter.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None));

        Assert.Equal("SF300_TIMEOUT", exception.ErrorCode);
        Assert.Equal(1, protocol.GrantAccessCalls);
    }

    [Fact]
    public async Task UnsupportedCapabilityStopsCommandBeforeProtocolCall()
    {
        var protocol = new FakeSf300Protocol { DeviceInfo = CreateInfo([DeviceCapability.Status]) };
        await using var adapter = CreateAdapter(protocol);
        await adapter.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<DeviceCapabilityException>(
            () => adapter.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None));

        Assert.Equal(0, protocol.GrantAccessCalls);
    }

    [Fact]
    public async Task CommandsDelegateThroughProtocolContract()
    {
        var protocol = new FakeSf300Protocol { DeviceInfo = CreateInfo(AllCapabilities) };
        await using var adapter = CreateAdapter(protocol);
        await adapter.ConnectAsync(CancellationToken.None);
        var user = new DeviceUser("U-1", "Test User", "C-1", null, null);

        Assert.True((await adapter.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None)).Succeeded);
        Assert.True((await adapter.DenyAccessAsync(TurnstileDirection.Exit, CancellationToken.None)).Succeeded);
        Assert.True((await adapter.SendUserAsync(user, CancellationToken.None)).Succeeded);
        Assert.True((await adapter.SendCardAsync("C-1", "U-1", CancellationToken.None)).Succeeded);
        Assert.True((await adapter.SyncUserAsync(user, CancellationToken.None)).Succeeded);
        Assert.True((await adapter.SyncCardAsync("C-1", "U-1", CancellationToken.None)).Succeeded);
        Assert.Equal(user, await adapter.ReadUserAsync("U-1", CancellationToken.None));
        Assert.Equal("U-1", await adapter.ReadCardAsync("C-1", CancellationToken.None));
        Assert.Equal(8, protocol.CommandCalls);
    }

    [Fact]
    public async Task InvalidCommandResponseIsRejected()
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            ReturnNullCommandResult = true
        };
        await using var adapter = CreateAdapter(protocol);
        await adapter.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None));

        Assert.Equal("SF300_INVALID_RESPONSE", exception.ErrorCode);
    }

    [Fact]
    public async Task CardStreamTimeoutIsEnforcedByAdapter()
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            BlockCardStream = true
        };
        await using var adapter = CreateAdapter(protocol, TimeSpan.FromMilliseconds(20));
        await adapter.ConnectAsync(CancellationToken.None);
        await using var enumerator = adapter.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal("SF300_TIMEOUT", exception.ErrorCode);
    }

    [Fact]
    public async Task CardStreamTimeoutRemainsBoundedWhenProtocolIgnoresCancellation()
    {
        var protocol = new FakeSf300Protocol
        {
            DeviceInfo = CreateInfo(AllCapabilities),
            IgnoreCardStreamCancellation = true
        };
        await using var adapter = CreateAdapter(protocol, TimeSpan.FromMilliseconds(20));
        await adapter.ConnectAsync(CancellationToken.None);
        await using var enumerator = adapter.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(async () =>
            await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));

        Assert.Equal("SF300_TIMEOUT", exception.ErrorCode);
    }

    [Fact]
    public async Task FailedHandshakeCleanupIsBoundedWhenProtocolDisconnectHangs()
    {
        var protocol = new FakeSf300Protocol { DeviceInfo = null, BlockDisconnect = true };
        await using var adapter = CreateAdapter(protocol, TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            adapter.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250)));

        Assert.Equal("SF300_HANDSHAKE_INVALID_RESPONSE", exception.ErrorCode);
    }

    [Fact]
    public async Task DisconnectClearsCapabilitiesAndState()
    {
        var protocol = new FakeSf300Protocol { DeviceInfo = CreateInfo(AllCapabilities) };
        await using var adapter = CreateAdapter(protocol);
        await adapter.ConnectAsync(CancellationToken.None);

        await adapter.DisconnectAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, adapter.ConnectionState);
        Assert.Empty(adapter.Capabilities);
        Assert.False(protocol.IsConnected);
    }

    private static SF300Adapter CreateAdapter(FakeSf300Protocol protocol, TimeSpan? timeout = null,
        int maxRetryCount = 0) =>
        new(Guid.NewGuid(), "Test SF300", Endpoint, protocol, timeout, maxRetryCount);

    private static DeviceInfo CreateInfo(IEnumerable<DeviceCapability> capabilities) =>
        new("SF300", "TEST-SERIAL", "TEST-FIRMWARE", capabilities.ToHashSet());

    private sealed class FakeSf300Protocol : ISf300Protocol
    {
        private static readonly DeviceCommandResult Success = new(true, "Accepted");

        public bool IsConnected { get; private set; }
        public DeviceInfo? DeviceInfo { get; init; } = CreateInfo(AllCapabilities);
        public bool BlockDeviceInfo { get; init; }
        public bool BlockCardStream { get; init; }
        public bool IgnoreCardStreamCancellation { get; init; }
        public bool BlockDisconnect { get; init; }
        public bool ReturnNullCommandResult { get; init; }
        public int TransientStatusFailures { get; set; }
        public Sf300ProtocolException? StatusFailure { get; init; }
        public bool BlockGrantAccess { get; init; }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int DeviceInfoCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int GrantAccessCalls { get; private set; }
        public int CommandCalls { get; private set; }

        public Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCalls++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            if (BlockDisconnect)
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
        }

        public async Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken)
        {
            DeviceInfoCalls++;
            if (BlockDeviceInfo)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return DeviceInfo;
        }

        public Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCalls++;
            if (StatusFailure is not null) throw StatusFailure;
            if (TransientStatusFailures-- > 0)
            {
                throw new Sf300ProtocolException("Temporary test failure", isTransient: true);
            }

            return Task.FromResult<DeviceStatus?>(
                new DeviceStatus(DeviceConnectionState.Connected, DateTimeOffset.UtcNow, "Connected"));
        }

        public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (BlockCardStream)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (IgnoreCardStreamCancellation)
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            yield return new CardReadEvent("C-1", DateTimeOffset.UtcNow, "Fake protocol seam");
        }

        public async Task<DeviceCommandResult?> GrantAccessAsync(TurnstileDirection direction,
            CancellationToken cancellationToken)
        {
            GrantAccessCalls++;
            if (BlockGrantAccess) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await Result(cancellationToken);
        }

        public Task<DeviceCommandResult?> DenyAccessAsync(TurnstileDirection direction,
            CancellationToken cancellationToken) => Result(cancellationToken);

        public Task<DeviceCommandResult?> SendUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
            Result(cancellationToken);

        public Task<DeviceCommandResult?> SendCardAsync(string cardNumber, string externalUserId,
            CancellationToken cancellationToken) => Result(cancellationToken);

        public Task<DeviceCommandResult?> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
            Result(cancellationToken);

        public Task<DeviceCommandResult?> SyncCardAsync(string cardNumber, string externalUserId,
            CancellationToken cancellationToken) => Result(cancellationToken);

        public Task<DeviceCommandResult?> DeleteCardAsync(string cardNumber, CancellationToken cancellationToken) =>
            Result(cancellationToken);

        public Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCalls++;
            return Task.FromResult<DeviceUser?>(new DeviceUser(externalUserId, "Test User", "C-1", null, null));
        }

        public Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCalls++;
            return Task.FromResult<string?>("U-1");
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private Task<DeviceCommandResult?> Result(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCalls++;
            return Task.FromResult(ReturnNullCommandResult ? null : Success);
        }
    }
}
