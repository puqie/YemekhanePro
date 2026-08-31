using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kart yasam dongusu cihaz kuyruguna baglidir: yeni kart otomatik olarak yuklenmeyi bekler,
/// pasife alinan kart silinmeyi bekler. Bu kuyruklama SaveChanges sinirinda yapilir; tek tek
/// cagiranlara birakilsaydi ileride eklenen bir yazma yolu bunu unutur ve kart cihaza hic gitmezdi.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class CardLifecycleSyncTests
{
    [Fact]
    public async Task NewCardIsAutomaticallyQueuedForEveryDevice()
    {
        await using var context = await CreateContextAsync();
        var device = await AddDeviceAsync(context, "Ana Giriş");
        var student = AddStudent(context);
        await context.SaveChangesAsync();

        context.StudentCards.Add(new StudentCard
        {
            StudentId = student.Id, CardNumber = "0011223344", ValidFrom = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var state = await context.DeviceCardStates.SingleAsync();
        Assert.Equal(device.Id, state.DeviceId);
        Assert.Equal(DeviceCardSyncStatus.Pending, state.Status);
        Assert.Equal("0011223344", state.CardNumber);
    }

    [Fact]
    public async Task DeactivatingCardQueuesRemoval()
    {
        await using var context = await CreateContextAsync();
        await AddDeviceAsync(context, "Ana Giriş");
        var student = AddStudent(context);
        var card = new StudentCard
        {
            StudentId = student.Id, CardNumber = "0011223344", ValidFrom = DateTimeOffset.UtcNow
        };
        context.StudentCards.Add(card);
        await context.SaveChangesAsync();

        // Kart cihaza yuklenmis kabul edilir; ardindan pasife alinir.
        var loaded = await context.DeviceCardStates.SingleAsync();
        loaded.Status = DeviceCardSyncStatus.Loaded;
        await context.SaveChangesAsync();

        card.IsActive = false;
        await context.SaveChangesAsync();

        var state = await context.DeviceCardStates.SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.PendingRemoval, state.Status);
    }

    [Fact]
    public async Task CardAddedBeforeAnyDeviceExistsIsQueuedWhenDeviceAppears()
    {
        await using var context = await CreateContextAsync();
        var student = AddStudent(context);
        context.StudentCards.Add(new StudentCard
        {
            StudentId = student.Id, CardNumber = "0011223344", ValidFrom = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        Assert.Equal(0, await context.DeviceCardStates.CountAsync());

        await AddDeviceAsync(context, "Sonradan Eklenen");

        // Yeni cihaz mevcut aktif kartlari devralir; aksi halde cihaz bos kalir ve
        // hicbir ogrenci o turnikeden gecemez.
        var state = await context.DeviceCardStates.SingleAsync();
        Assert.Equal(DeviceCardSyncStatus.Pending, state.Status);
    }

    private static async Task<YemekhaneDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection($"Data Source=lifecycle-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        var context = new YemekhaneDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }

    private static Student AddStudent(YemekhaneDbContext context)
    {
        var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Ayşe", LastName = "Yılmaz" };
        context.Students.Add(student);
        return student;
    }

    private static int nextAddress;

    private static async Task<Device> AddDeviceAsync(YemekhaneDbContext context, string name)
    {
        var address = Interlocked.Increment(ref nextAddress);
        var device = new Device
        {
            Name = name, DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry",
            ConnectionStatus = "Connected", IsActive = true,
            IpAddress = $"10.2.{address / 250}.{address % 250 + 1}", IpPort = 4370
        };
        context.Devices.Add(device);
        await context.SaveChangesAsync();
        return device;
    }
}
