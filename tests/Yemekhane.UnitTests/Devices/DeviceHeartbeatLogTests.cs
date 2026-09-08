using Yemekhane.Api.Devices;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Saglik yoklamasi her 30 sn'de "Bagli -> Bagli" yayinlar. Bunu olay olarak yazmak cihaz gunlugunu
/// "Kullanici 444/30000, kayit 29389/1500" satirlariyla dolduruyordu (sahada gunde ~2900 satir).
/// Yalnizca durum degisimi ya da hata gunluge girer.
/// </summary>
public sealed class DeviceHeartbeatLogTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 8, 8, 15, 3, TimeSpan.FromHours(3));

    private static DeviceStateChange Change(DeviceConnectionState previous, DeviceConnectionState current, Exception? exception = null) =>
        new(Guid.NewGuid(), "Yemekhane Turnikesi", previous, current, At,
            new DeviceStatus(current, At, "Kullanıcı 444/30000, kayıt 29389/1500."), exception);

    [Fact]
    public void ConnectedStayingConnectedWithoutErrorIsAHeartbeat()
    {
        Assert.True(DeviceRuntimePersistenceService.IsHeartbeat(
            Change(DeviceConnectionState.Connected, DeviceConnectionState.Connected)));
    }

    [Fact]
    public void TransitionsFaultsAndErrorsAreNotHeartbeats()
    {
        Assert.False(DeviceRuntimePersistenceService.IsHeartbeat(
            Change(DeviceConnectionState.Disconnected, DeviceConnectionState.Connected)));
        Assert.False(DeviceRuntimePersistenceService.IsHeartbeat(
            Change(DeviceConnectionState.Connected, DeviceConnectionState.Faulted)));
        Assert.False(DeviceRuntimePersistenceService.IsHeartbeat(
            Change(DeviceConnectionState.Connected, DeviceConnectionState.Connected, new TimeoutException("UDP yanıt yok"))));
        // Yeniden baglanma dongusu de kaydedilir: teknisyen kac kez denendigini gormeli.
        Assert.False(DeviceRuntimePersistenceService.IsHeartbeat(
            Change(DeviceConnectionState.Reconnecting, DeviceConnectionState.Reconnecting)));
    }
}
