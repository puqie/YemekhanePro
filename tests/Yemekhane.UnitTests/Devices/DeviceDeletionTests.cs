using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Devices.Management;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kalici cihaz silme. Once yalnizca "pasiflestir" vardi; yanlis eklenen cihaz listede kaliyordu.
/// Gecis kayitlari denetim gecmisidir: onlari tasiyan cihaz silinmez, pasiflestirilir.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class DeviceDeletionTests
{
    [Fact]
    public async Task DeletingADeviceRemovesItsEventsAndCardStatesAndWritesAnAuditEntry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var device = await fixture.CreateDeviceAsync("Yanlış Cihaz");
        var otherDevice = await fixture.CreateDeviceAsync("Kalan Cihaz");
        await fixture.SeedEventAndCardStateAsync(device.Id);
        await fixture.SeedEventAndCardStateAsync(otherDevice.Id);

        await fixture.Service.DeleteAsync(device.Id, CancellationToken.None);

        Assert.Null(await fixture.Db.Devices.FindAsync(device.Id));
        Assert.Equal(0, await fixture.Db.DeviceEvents.CountAsync(x => x.DeviceId == device.Id));
        Assert.Equal(0, await fixture.Db.DeviceCardStates.CountAsync(x => x.DeviceId == device.Id));
        // Diger cihazin kayitlari dokunulmadan kalir.
        Assert.NotNull(await fixture.Db.Devices.FindAsync(otherDevice.Id));
        Assert.Equal(1, await fixture.Db.DeviceEvents.CountAsync(x => x.DeviceId == otherDevice.Id));
        Assert.Equal(1, await fixture.Db.DeviceCardStates.CountAsync(x => x.DeviceId == otherDevice.Id));
        Assert.Contains(await fixture.Db.AuditLogs.ToListAsync(), x => x.Action == "Device.Delete" && x.EntityId == device.Id.ToString());
    }

    [Fact]
    public async Task DeviceWithAccessHistoryIsRefusedAndKept()
    {
        await using var fixture = await Fixture.CreateAsync();
        var device = await fixture.CreateDeviceAsync("Yemekhane Girişi");
        fixture.Db.AccessLogs.Add(new AccessLog
        {
            Timestamp = DateTimeOffset.UtcNow, DeviceId = device.Id, CardNumber = "8247129", Decision = "ALLOW",
            Reason = "Geçiş onaylandı", Direction = "Entry", ReaderSource = "SC403", OperationId = Guid.NewGuid()
        });
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => fixture.Service.DeleteAsync(device.Id, CancellationToken.None));

        Assert.Contains("geçiş kaydı", error.Message, StringComparison.Ordinal);
        Assert.Contains("pasifleştirin", error.Message, StringComparison.Ordinal);
        Assert.NotNull(await fixture.Db.Devices.FindAsync(device.Id));
        Assert.DoesNotContain(await fixture.Db.AuditLogs.ToListAsync(), x => x.Action == "Device.Delete");
    }

    [Fact]
    public async Task DeletedDeviceIsUnregisteredFromTheRuntime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var device = await fixture.CreateDeviceAsync("Silinecek");
        Assert.True(fixture.Manager.TryGetDevice(device.Id, out _));

        await fixture.Service.DeleteAsync(device.Id, CancellationToken.None);

        Assert.False(fixture.Manager.TryGetDevice(device.Id, out _));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection connection = null!;
        public YemekhaneDbContext Db { get; private set; } = null!;
        public DeviceManager Manager { get; } = new();
        public DeviceAdministrationService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.connection = new SqliteConnection($"Data Source=file:device-delete-{Guid.NewGuid():N}?mode=memory&cache=shared");
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(fixture.connection).Options;
            fixture.Db = new YemekhaneDbContext(options);
            await fixture.Db.Database.MigrateAsync();
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
            fixture.Service = new DeviceAdministrationService(fixture.Db, fixture.Manager,
                new DeviceAdapterFactory(isDevelopment: true),
                new AuditService(new EfAuditRepository(fixture.Db, clock), new SystemAuditContext()),
                clock, new StubEnvironment());
            return fixture;
        }

        public async Task<DeviceDto> CreateDeviceAsync(string name)
        {
            var address = Random.Shared.Next(2, 250);
            return await Service.CreateAsync(new DeviceWriteRequest(name, "SC403", "Ethernet",
                $"10.9.{address}.{Random.Shared.Next(2, 250)}", 4370, null, null, IsActive: true, AutoConnect: false,
                HasTurnstile: true, Location: null, Direction: "Entry"), CancellationToken.None);
        }

        public async Task SeedEventAndCardStateAsync(Guid deviceId)
        {
            var student = new Student { StudentNo = Guid.NewGuid().ToString("N")[..8], FirstName = "Ali", LastName = "Kaya" };
            var card = new StudentCard { StudentId = student.Id, CardNumber = Guid.NewGuid().ToString("N")[..10], ValidFrom = DateTimeOffset.UtcNow };
            Db.AddRange(student, card,
                new DeviceEvent { DeviceId = deviceId, Timestamp = DateTimeOffset.UtcNow, EventType = "Connected", Severity = "Information", Message = "Bağlandı" },
                new DeviceCardState { DeviceId = deviceId, CardId = card.Id, StudentId = student.Id, CardNumber = card.CardNumber, Status = "Pending" });
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
