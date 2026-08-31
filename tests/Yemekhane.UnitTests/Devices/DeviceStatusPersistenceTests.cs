using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Audit;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Yönetici cihazı yeniden yapılandırdığında bilerek "Disconnected" yazar. Arka planda uçuşta olan
/// bayat bir donanım sonucu bu kararı ezmemelidir; <see cref="DeviceAdministrationService.UpdateAsync"/>
/// durum damgasını ilerleterek bunu engeller.
/// </summary>
public sealed class DeviceStatusPersistenceTests
{
    [Fact]
    public async Task UpdateAdvancesStatusStampSoStaleHardwareResultCannotWin()
    {
        await using var connection = new SqliteConnection($"Data Source=file:device-race-{Guid.NewGuid():N}?mode=memory&cache=shared");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.MigrateAsync();
        var device = new Device
        {
            Name = "Turnike-1", DeviceType = "EthernetReader", ConnectionType = "Ethernet",
            Direction = "Entry", ConnectionStatus = "Connected", IsActive = true,
            IpAddress = "10.0.0.10", IpPort = 4370,
            LastStatusAt = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)
        };
        db.Add(device);
        await db.SaveChangesAsync();

        // Bayat durum yazimi bu ani tasiyor: yonetici duzenlemesinden ONCE olculdu.
        var staleStatusAt = new DateTimeOffset(2026, 9, 14, 12, 0, 5, TimeSpan.Zero);
        var service = CreateService(db, staleStatusAt.AddSeconds(1));

        await service.UpdateAsync(device.Id, new DeviceWriteRequest("Turnike-1", "EthernetReader", "Ethernet",
            "10.0.0.99", 4370, null, null, IsActive: true, AutoConnect: false, HasTurnstile: false,
            Location: null, Direction: "Entry"), default);

        var stored = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == device.Id);
        Assert.Equal("Disconnected", stored.ConnectionStatus);
        Assert.NotNull(stored.LastStatusAt);
        Assert.True(stored.LastStatusAt > staleStatusAt,
            "UpdateAsync durum damgasını ilerletmezse uçuştaki bayat sonuç bu kararı ezer.");
    }

    private static DeviceAdministrationService CreateService(YemekhaneDbContext db, DateTimeOffset now)
    {
        var timeProvider = new FixedClock(now);
        var manager = new DeviceManager();
        return new DeviceAdministrationService(db, manager, new DeviceAdapterFactory(isDevelopment: true),
            new AuditService(new EfAuditRepository(db, timeProvider), new SystemAuditContext()),
            timeProvider, new StubEnvironment());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
