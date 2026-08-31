using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Api.Infrastructure;
using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;

namespace Yemekhane.UnitTests.Realtime;

public sealed class RealtimeInfrastructureTests
{
    [Fact]
    public async Task RegistrationMapsHubAtStableEndpointAndRegistersDispatcher()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddYemekhaneRealtime();
        await using var app = builder.Build();

        app.MapYemekhaneRealtime();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText);
        Assert.Contains(RealtimeServiceExtensions.HubPath, routes);
        Assert.IsType<RealtimeEventPublisher>(app.Services.GetRequiredService<IRealtimeEventPublisher>());
        Assert.Contains(app.Services.GetServices<IHostedService>(), service => service is DeviceStatusRealtimeBridge);
    }

    [Fact]
    public async Task DeviceManagerStateChangesAreBridgedWithExpectedPayload()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var manager = new DeviceManager();
        var device = new BridgeDevice();
        manager.Register(device, new DeviceRegistrationOptions(AutoConnect: false));
        var bridge = new DeviceStatusRealtimeBridge(manager, publisher,
            NullLogger<DeviceStatusRealtimeBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        await manager.ConnectAsync(device.Id);
        await bridge.StopAsync(CancellationToken.None);

        var realtimeEvent = Assert.Single(publisher.DeviceStatuses,
            value => value.Status == nameof(DeviceConnectionState.Connected));
        Assert.Equal(device.Id, realtimeEvent.DeviceId);
        Assert.Equal(device.Name, realtimeEvent.DeviceName);
        Assert.Equal(nameof(DeviceConnectionState.Connecting), realtimeEvent.PreviousStatus);
        Assert.NotNull(realtimeEvent.CheckedAt);
    }

    [Fact]
    public void ChannelNamesAreStableAndUnique()
    {
        Assert.Equal(4, RealtimeChannels.All.Count);
        Assert.Contains(RealtimeChannels.AccessDecisions, RealtimeChannels.All);
        Assert.Contains(RealtimeChannels.TurnstileResults, RealtimeChannels.All);
        Assert.Contains(RealtimeChannels.DeviceStatuses, RealtimeChannels.All);
        Assert.Contains(RealtimeChannels.Notifications, RealtimeChannels.All);
    }

    private sealed class BridgeDevice : IDevice
    {
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;

        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Bridge test device";
        public DeviceEndpoint Endpoint { get; } = new("Test");
        public DeviceConnectionState ConnectionState => _state;

        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = DeviceConnectionState.Connected;
            return Task.FromResult(new DeviceInfo("Test", null, null,
                new HashSet<DeviceCapability> { DeviceCapability.Status }));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceStatus(_state, DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
