using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Notifications;
using Yemekhane.Application.Realtime;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Notifications;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Notifications;

public sealed class NotificationServiceTests
{
    [Fact]
    public async Task PersistsBeforePublishAndCoalescesDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var publisher = new VerifyingPublisher(fixture.Db);
        var service = new NotificationService(new EfNotificationRepository(fixture.Db), publisher, TimeProvider.System);
        var request = new CreateNotification(NotificationSeverities.Error, "DeviceError", "Hata", "Bağlantı kesildi",
            AudiencePermission: "devices.read", DeduplicationKey: "device:1:error");

        var first = await service.CreateAsync(request);
        var second = await service.CreateAsync(request);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, await fixture.Db.Notifications.CountAsync());
        Assert.True(publisher.WasPersistedAtPublish);
        Assert.Equal(2, publisher.Events.Count);
    }

    [Fact]
    public async Task InvalidRequestDoesNotPersistOrPublish()
    {
        await using var fixture = await Fixture.CreateAsync();
        var publisher = new VerifyingPublisher(fixture.Db);
        var service = new NotificationService(new EfNotificationRepository(fixture.Db), publisher, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateNotification("Fatal", "Test", "Başlık", "Mesaj")));

        Assert.Empty(publisher.Events);
        Assert.Empty(await fixture.Db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task AudienceAndReadReceiptsAreUserSpecific()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new EfNotificationRepository(fixture.Db);
        await repository.CreateOrCoalesceAsync(new CreateNotification(NotificationSeverities.Info, "Public", "Genel", "Mesaj"), DateTimeOffset.UtcNow);
        await repository.CreateOrCoalesceAsync(new CreateNotification(NotificationSeverities.Warning, "Device", "Cihaz", "Mesaj",
            AudiencePermission: "devices.read"), DateTimeOffset.UtcNow);
        await repository.CreateOrCoalesceAsync(new CreateNotification(NotificationSeverities.Info, "Private", "Özel", "Mesaj",
            AudienceUserId: fixture.User1), DateTimeOffset.UtcNow);

        var user1 = await repository.ListAsync(fixture.User1, new HashSet<string> { "devices.read" }, 20, null);
        var user2 = await repository.ListAsync(fixture.User2, new HashSet<string>(), 20, null);
        Assert.Equal(3, user1.Items.Count);
        Assert.Single(user2.Items);

        Assert.Equal(3, await repository.MarkAllReadAsync(fixture.User1, new HashSet<string> { "devices.read" }, DateTimeOffset.UtcNow));
        Assert.Equal(0, await repository.UnreadCountAsync(fixture.User1, new HashSet<string> { "devices.read" }));
        Assert.Equal(1, await repository.UnreadCountAsync(fixture.User2, new HashSet<string>()));
    }

    private sealed class VerifyingPublisher(YemekhaneDbContext db) : IRealtimeEventPublisher
    {
        public List<NotificationEvent> Events { get; } = [];
        public bool WasPersistedAtPublish { get; private set; }
        public ValueTask PublishAsync(NotificationEvent value, CancellationToken cancellationToken = default)
        { WasPersistedAtPublish = db.Notifications.AsNoTracking().Any(x => x.Id == value.NotificationId); Events.Add(value); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(AccessDecisionCommittedEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(TurnstileResultEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(DeviceStatusChangedEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Fixture(SqliteConnection connection, YemekhaneDbContext db) : IAsyncDisposable
    {
        public YemekhaneDbContext Db { get; } = db;
        public Guid User1 { get; } = Guid.NewGuid();
        public Guid User2 { get; } = Guid.NewGuid();
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            db.Users.AddRange(User(fixture.User1, "one"), User(fixture.User2, "two")); await db.SaveChangesAsync();
            return fixture;
        }
        private static User User(Guid id, string name) => new() { Id = id, Username = name, NormalizedUsername = name.ToUpperInvariant(), PasswordHash = "hash" };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
