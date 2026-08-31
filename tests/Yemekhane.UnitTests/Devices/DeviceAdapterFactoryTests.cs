using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Sf300;
using Yemekhane.Devices.Simulators;

namespace Yemekhane.UnitTests.Devices;

public sealed class DeviceAdapterFactoryTests
{
    [Fact]
    public async Task Sf300WithoutProtocolReportsNotConfiguredAndNeverConnects()
    {
        var factory = new DeviceAdapterFactory(isDevelopment: false);
        await using var adapter = factory.Create(new DeviceAdapterConfiguration(Guid.NewGuid(), "Kapı SF300",
            "SF300", "Ethernet", null, null, "192.168.1.20", 4370, true));

        var error = await Assert.ThrowsAsync<DeviceConnectionException>(() => adapter.ConnectAsync(CancellationToken.None));

        Assert.IsType<SF300Adapter>(adapter);
        Assert.Equal("SF300_PROTOCOL_NOT_CONFIGURED", error.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, adapter.ConnectionState);
    }

    [Fact]
    public void SimulatorIsRejectedOutsideDevelopment()
    {
        var factory = new DeviceAdapterFactory(isDevelopment: false);
        var configuration = new DeviceAdapterConfiguration(Guid.NewGuid(), "Test", "Simulator",
            "Simulator", null, null, null, null, false);

        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(configuration));

        Assert.Contains("Development", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorUsesRealSimulatorAdapterInDevelopment()
    {
        var factory = new DeviceAdapterFactory(isDevelopment: true);
        await using var adapter = factory.Create(new DeviceAdapterConfiguration(Guid.NewGuid(), "Test", "Simulator",
            "Simulator", null, null, null, null, false));

        Assert.IsType<SimulatorCardReader>(adapter);
        var info = await adapter.ConnectAsync(CancellationToken.None);
        Assert.Equal(DeviceConnectionState.Connected, adapter.ConnectionState);
        Assert.Equal("Simulator Card Reader", info.Model);
    }
}
