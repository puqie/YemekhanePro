using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Devices;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kart-cihaz senkronizasyon durumu. Cok turnikeli bir okulda bir kartin her cihazdaki durumu
/// ayri tutulur: tek bir "yuklendi" bayragi, bir turnikede eksik kalan karti gizlerdi ve
/// ogrenci o kapidan gecemezken sistem her seyin yolunda oldugunu gosterirdi.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class DeviceCardSyncTests
{
    [Fact]
    public async Task PendingCardIsQueuedForEveryActiveTurnstile()
    {
        await using var context = await CreateContextAsync();
        var (student, card) = await AddStudentWithCardAsync(context);
        var first = await AddDeviceAsync(context, "Ana Giriş");
        var second = await AddDeviceAsync(context, "Yemekhane");
        var service = new DeviceCardSyncService(context, TimeProvider.System);

        await service.QueueCardAsync(card.Id, default);

        var states = await context.Set<DeviceCardState>().ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Equal(DeviceCardSyncStatus.Pending, state.Status));
        Assert.Contains(states, state => state.DeviceId == first.Id);
        Assert.Contains(states, state => state.DeviceId == second.Id);
        Assert.All(states, state => Assert.Equal(student.Id, state.StudentId));
    }

    [Fact]
    public async Task SuccessfulPushMarksOnlyThatDeviceAsLoaded()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        var first = await AddDeviceAsync(context, "Ana Giriş");
        var second = await AddDeviceAsync(context, "Arka Kapı");
        var service = new DeviceCardSyncService(context, TimeProvider.System);
        await service.QueueCardAsync(card.Id, default);

        await service.MarkLoadedAsync(first.Id, card.Id, default);

        var states = await context.Set<DeviceCardState>().ToListAsync();
        Assert.Equal(DeviceCardSyncStatus.Loaded, states.Single(x => x.DeviceId == first.Id).Status);
        Assert.Equal(DeviceCardSyncStatus.Pending, states.Single(x => x.DeviceId == second.Id).Status);
        Assert.NotNull(states.Single(x => x.DeviceId == first.Id).LastSyncedAt);
    }

    [Fact]
    public async Task TransientFailureKeepsCardPendingSoItIsRetried()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        var device = await AddDeviceAsync(context, "Ana Giriş");
        var service = new DeviceCardSyncService(context, TimeProvider.System);
        await service.QueueCardAsync(card.Id, default);

        await service.MarkFailedAsync(device.Id, card.Id, "SF300_BUSY", isPermanent: false, default);

        var state = await context.Set<DeviceCardState>().SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.Pending, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal("SF300_BUSY", state.LastError);
        Assert.Contains(await service.GetPendingAsync(device.Id, 10, default), x => x.CardNumber == card.CardNumber);
    }

    [Fact]
    public async Task PermanentFailureStopsRetryingAndStaysVisible()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        var device = await AddDeviceAsync(context, "Ana Giriş");
        var service = new DeviceCardSyncService(context, TimeProvider.System);
        await service.QueueCardAsync(card.Id, default);

        await service.MarkFailedAsync(device.Id, card.Id, "SF300_INVALID_CARD", isPermanent: true, default);

        var state = await context.Set<DeviceCardState>().SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.Failed, state.Status);
        // Kalici hata yeniden denenmez ama operatore gorunur kalmalidir.
        Assert.Empty(await service.GetPendingAsync(device.Id, 10, default));
        var report = await service.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Failed, report.Single().Status);
    }

    [Fact]
    public async Task DeactivatedCardIsQueuedForRemovalFromEveryDevice()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        var device = await AddDeviceAsync(context, "Ana Giriş");
        var service = new DeviceCardSyncService(context, TimeProvider.System);
        await service.QueueCardAsync(card.Id, default);
        await service.MarkLoadedAsync(device.Id, card.Id, default);

        await service.QueueRemovalAsync(card.Id, default);

        var state = await context.Set<DeviceCardState>().SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.PendingRemoval, state.Status);
        Assert.Contains(await service.GetPendingAsync(device.Id, 10, default),
            x => x.CardNumber == card.CardNumber && x.IsRemoval);
    }

    [Fact]
    public async Task QueueingTheSameCardTwiceDoesNotCreateDuplicateRows()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        await AddDeviceAsync(context, "Ana Giriş");
        var service = new DeviceCardSyncService(context, TimeProvider.System);

        await service.QueueCardAsync(card.Id, default);
        await service.QueueCardAsync(card.Id, default);

        Assert.Equal(1, await context.Set<DeviceCardState>().CountAsync());
    }

    [Fact]
    public async Task ReloadAfterSuccessResetsAttemptCountAndError()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        var device = await AddDeviceAsync(context, "Ana Giriş");
        var service = new DeviceCardSyncService(context, TimeProvider.System);
        await service.QueueCardAsync(card.Id, default);
        await service.MarkFailedAsync(device.Id, card.Id, "SF300_BUSY", isPermanent: false, default);

        await service.MarkLoadedAsync(device.Id, card.Id, default);

        var state = await context.Set<DeviceCardState>().SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.Loaded, state.Status);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task InactiveDevicesAreNotQueued()
    {
        await using var context = await CreateContextAsync();
        var (_, card) = await AddStudentWithCardAsync(context);
        await AddDeviceAsync(context, "Kapalı Kapı", isActive: false);
        var service = new DeviceCardSyncService(context, TimeProvider.System);

        await service.QueueCardAsync(card.Id, default);

        Assert.Equal(0, await context.Set<DeviceCardState>().CountAsync());
    }

    private static async Task<YemekhaneDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection($"Data Source=cards-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        var context = new YemekhaneDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }

    private static async Task<(Student Student, StudentCard Card)> AddStudentWithCardAsync(YemekhaneDbContext context)
    {
        var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Ayşe", LastName = "Yılmaz" };
        var card = new StudentCard { StudentId = student.Id, CardNumber = "0012345678", ValidFrom = DateTimeOffset.UtcNow };
        context.AddRange(student, card);
        await context.SaveChangesAsync();
        return (student, card);
    }

    private static int nextDeviceAddress;

    private static async Task<Device> AddDeviceAsync(YemekhaneDbContext context, string name, bool isActive = true)
    {
        // Cihazlar IP:port cifti uzerinde benzersizdir; her cihaza ayri adres verilir.
        var address = Interlocked.Increment(ref nextDeviceAddress);
        var device = new Device
        {
            Name = name, DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry",
            ConnectionStatus = "Connected", IsActive = isActive,
            IpAddress = $"10.0.{address / 250}.{address % 250 + 1}", IpPort = 4370
        };
        context.Add(device);
        await context.SaveChangesAsync();
        return device;
    }
}
