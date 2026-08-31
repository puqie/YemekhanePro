using System.Runtime.CompilerServices;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.UnitTests.Devices;

public sealed class DeviceContractTests
{
    [Fact]
    public async Task CardStreamHonorsCancellationWithoutBlockingCaller()
    {
        await using ICardReader reader = new ContractReader();
        using var cancellation = new CancellationTokenSource();
        await reader.ConnectAsync(cancellation.Token);
        await using var enumerator = reader.ReadCardsAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    private sealed class ContractReader : ICardReader
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Contract reader";
        public DeviceEndpoint Endpoint { get; } = new("Simulator");
        public DeviceConnectionState ConnectionState { get; private set; }
        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectionState = DeviceConnectionState.Connected;
            return Task.FromResult(new DeviceInfo("Test", null, null, new HashSet<DeviceCapability> { DeviceCapability.ReadCard }));
        }
        public Task DisconnectAsync(CancellationToken cancellationToken) { ConnectionState = DeviceConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new DeviceStatus(ConnectionState, DateTimeOffset.UtcNow));
        public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public ValueTask DisposeAsync() { ConnectionState = DeviceConnectionState.Disconnected; return ValueTask.CompletedTask; }
    }
}
