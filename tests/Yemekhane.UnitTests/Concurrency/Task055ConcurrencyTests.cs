using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Audit;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Notifications;
using Yemekhane.Application.Realtime;
using Yemekhane.Application.Sync;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.BulkOperations;
using Yemekhane.Infrastructure.Cards;
using Yemekhane.Infrastructure.Notifications;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;
using Yemekhane.Infrastructure.Sms;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Concurrency;

/// <summary>
/// Eszamanlilik testleri paylasilan bellek-ici SQLite veritabanlarini yogun sekilde acip kapatir.
/// API fixture'lariyla paralel kostuklarinda son baglanti kapaninca veritabani serbest kaliyor ve
/// diger testler "no such table" hatasi aliyordu; bu yuzden seri kosan koleksiyona alindilar.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class Task055ConcurrencyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task SimultaneousTurnstilesConsumeQuantityOneExactlyOnce(int turnstiles)
    {
        await using var database = await TestDatabase.CreateAsync(1);

        var decisions = await database.RunAccessRaceAsync(turnstiles);

        Assert.Single(decisions, x => x.Decision == "ALLOW");
        Assert.Equal(turnstiles - 1, decisions.Count(x => x.Decision == "DENY"));
        await database.AssertAccessCountsAsync(1, turnstiles);
    }

    [Fact]
    public async Task SimultaneousTurnstilesConsumeExactlyConfiguredQuantity()
    {
        await using var database = await TestDatabase.CreateAsync(17);

        var decisions = await database.RunAccessRaceAsync(100);

        Assert.Equal(17, decisions.Count(x => x.Decision == "ALLOW"));
        Assert.Equal(83, decisions.Count(x => x.Decision == "DENY"));
        await database.AssertAccessCountsAsync(17, 100);
    }

    [Fact]
    public async Task ConcurrentDuplicateOperationIdHasOneConsumptionAndOneLog()
    {
        await using var database = await TestDatabase.CreateAsync(1);
        var operationId = Guid.NewGuid();

        var decisions = await database.RunAccessRaceAsync(10, operationId);

        Assert.All(decisions, x => Assert.Equal("ALLOW", x.Decision));
        await database.AssertAccessCountsAsync(1, 1);
    }

    [Fact]
    public async Task AtomicConsumptionRemainsDeterministicForFiftyRaces()
    {
        for (var loop = 0; loop < 50; loop++)
        {
            await using var database = await TestDatabase.CreateAsync(1);
            var decisions = await database.RunAccessRaceAsync(2);
            Assert.Single(decisions, x => x.Decision == "ALLOW");
            await database.AssertAccessCountsAsync(1, 2);
        }
    }

    [Fact]
    public async Task ConcurrentCardAssignmentsLeaveOneActiveCard()
    {
        await using var database = await TestDatabase.CreateAsync(1, includeCard: false);
        var start = NewGate();
        var attempts = Enumerable.Range(0, 10).Select(async index =>
        {
            await start.Task;
            await using var context = database.CreateContext();
            try
            {
                await new EfCardRepository(context).AssignAsync(database.StudentId, $"card-{index}",
                    database.Timestamp.AddMilliseconds(index), default);
                return true;
            }
            catch (Exception exception) when (exception is DbUpdateException or SqliteException or InvalidOperationException or EntityConflictException)
            {
                return false;
            }
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(results, x => x);
        await using var verification = database.CreateContext();
        Assert.Single(await verification.StudentCards.AsNoTracking()
            .Where(x => x.StudentId == database.StudentId && x.IsActive).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentNotificationsCoalesceToOneRow()
    {
        await using var database = await TestDatabase.CreateAsync(1);
        var start = NewGate();
        var now = database.Timestamp;
        var request = new CreateNotification(NotificationSeverities.Warning, "Concurrency", "Başlık", "Mesaj",
            DeduplicationKey: "same-event", DeduplicationWindow: TimeSpan.FromMinutes(10));
        var calls = Enumerable.Range(0, 20).Select(async _ =>
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new EfNotificationRepository(context).CreateOrCoalesceAsync(request, now);
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));

        await using var verification = database.CreateContext();
        var notification = Assert.Single(await verification.Notifications.AsNoTracking().ToListAsync());
        Assert.Equal(20, notification.Count);
    }

    [Fact]
    public async Task MultipleSyncEnginesClaimEachOperationOnce()
    {
        await using var database = await TestDatabase.CreateAsync(1);
        await using (var setup = database.CreateContext())
        {
            setup.SyncOperations.AddRange(Enumerable.Range(0, 40).Select(index => new SyncOperation
            {
                OperationId = Guid.NewGuid(), EntityName = "Student", EntityId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OperationType = "Update", DeviceId = "pc", Payload = "{}", Timestamp = database.Timestamp,
                SyncStatus = SyncOperationStatuses.Pending
            }));
            await setup.SaveChangesAsync();
        }
        var transport = new CountingTransport();
        var start = NewGate();
        var runs = Enumerable.Range(0, 4).Select(async _ =>
        {
            await start.Task;
            await using var context = database.CreateContext();
            using var engine = new SyncEngine(new EfSyncOperationStore(context), transport,
                new SyncEngineOptions { BatchSize = 40, MaxTransientRetries = 0 });
            return await engine.RunOnceAsync();
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(40, transport.Counts.Count);
        Assert.All(transport.Counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task MultipleSmsWorkersClaimEachMessageOnce()
    {
        await using var database = await TestDatabase.CreateAsync(1);
        var now = database.Timestamp;
        await using (var setup = database.CreateContext())
        {
            setup.SmsLogs.AddRange(Enumerable.Range(0, 100).Select(index => new SmsLog
            {
                Phone = "+905321112233", Message = "message", IdempotencyKey = $"sms-{index}",
                Status = SmsLogStatuses.Pending, CreatedAt = now.AddTicks(index), NextAttemptAt = now
            }));
            await setup.SaveChangesAsync();
        }
        var start = NewGate();
        var workers = Enumerable.Range(0, 5).Select(async _ =>
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new EfSmsLogRepository(context, TimeProvider.System)
                .ClaimBatchAsync(now, TimeSpan.FromMinutes(5), 100, default);
        }).ToArray();

        start.SetResult();
        var claimed = (await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30))).SelectMany(x => x).ToArray();

        Assert.Equal(100, claimed.Length);
        Assert.Equal(100, claimed.Select(x => x.Id).Distinct().Count());
        Assert.All(claimed, x => Assert.False(string.IsNullOrWhiteSpace(x.ClaimToken)));
    }

    [Fact]
    public async Task ConcurrentBulkApplyWithSameTokenHasOneEffect()
    {
        await using var database = await TestDatabase.CreateAsync(5);
        var tokens = new BulkPreviewTokenProtector();
        var request = new BulkCalendarOperationRequest("same-token", new BulkOperationScope("Manual",
            StudentIds: [database.StudentId]), new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14),
            [], database.MealTypeId, "CancelEntitlements", "Delete", null, "race");
        BulkOperationPreview preview;
        await using (var previewContext = database.CreateContext())
            preview = await CreateBulkService(previewContext, tokens).PreviewAsync(request);
        var start = NewGate();
        var calls = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await CreateBulkService(context, tokens).ApplyAsync(new(request, preview.PreviewToken), Guid.NewGuid());
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(results[0].OperationId, results[1].OperationId);
        await using var verification = database.CreateContext();
        Assert.Single(await verification.BulkOperations.AsNoTracking().ToListAsync());
        Assert.Equal("Cancelled", (await verification.MealEntitlements.AsNoTracking().SingleAsync()).Status);
    }

    private static BulkOperationService CreateBulkService(YemekhaneDbContext context, BulkPreviewTokenProtector tokens)
    {
        var audit = new AuditService(new EfAuditRepository(context, TimeProvider.System), new SystemAuditContext());
        return new BulkOperationService(new EfBulkOperationRepository(context, audit, TimeProvider.System),
            new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), tokens, TimeProvider.System);
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CountingTransport : ISyncTransport
    {
        public ConcurrentDictionary<Guid, int> Counts { get; } = new();
        public Task<SyncTransportResult> SendAsync(SyncRequestOperation operation, CancellationToken cancellationToken)
        {
            Counts.AddOrUpdate(operation.OperationId, 1, (_, count) => count + 1);
            return Task.FromResult(new SyncTransportResult(SyncTransportOutcome.Success));
        }
    }

    private sealed class OpenCalendar : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class NullRealtimePublisher : IRealtimeEventPublisher
    {
        public ValueTask PublishAsync(AccessDecisionCommittedEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(TurnstileResultEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(DeviceStatusChangedEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(NotificationEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly DbContextOptions<YemekhaneDbContext> options;

        private TestDatabase(string directory, DbContextOptions<YemekhaneDbContext> options)
        {
            this.directory = directory;
            this.options = options;
        }

        public Guid StudentId { get; private init; }
        public Guid MealTypeId { get; private init; }
        public Guid DeviceId { get; private init; }
        public string CardNumber { get; private init; } = "race-card";
        public DateTimeOffset Timestamp { get; } = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

        public static async Task<TestDatabase> CreateAsync(int quantity, bool includeCard = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Yemekhane.Task055", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "concurrency.db"), Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, ForeignKeys = true, DefaultTimeout = 2
            }.ToString();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options;
            await using var setup = new YemekhaneDbContext(options);
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000;");
            var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Race", LastName = "Student" };
            var meal = new MealType { Name = "Öğle" };
            var device = new Device { Name = "Turnstile", DeviceType = "SF300", ConnectionType = "Ethernet",
                Direction = "Entry", ConnectionStatus = "Connected" };
            var entitlement = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
                EntitlementDate = new DateOnly(2026, 9, 14), Quantity = quantity, Status = "Active" };
            setup.AddRange(student, meal, device, entitlement);
            if (includeCard)
                setup.Add(new StudentCard { StudentId = student.Id, CardNumber = "race-card", ValidFrom = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
            return new TestDatabase(directory, options) { StudentId = student.Id, MealTypeId = meal.Id, DeviceId = device.Id };
        }

        public YemekhaneDbContext CreateContext() => new(options);

        public async Task<IReadOnlyList<AccessDecision>> RunAccessRaceAsync(int count, Guid? operationId = null)
        {
            var start = NewGate();
            var calls = Enumerable.Range(0, count).Select(async index =>
            {
                await start.Task;
                await using var context = CreateContext();
                var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
                    new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), new NullRealtimePublisher());
                return await service.CheckAccessAsync(new AccessCheckRequest(CardNumber, DeviceId, MealTypeId,
                    Timestamp.AddTicks(index), OperationId: operationId));
            }).ToArray();
            start.SetResult();
            return await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task AssertAccessCountsAsync(int allowed, int logs)
        {
            await using var verification = CreateContext();
            Assert.Equal(allowed, await verification.MealUsages.CountAsync());
            Assert.Equal(logs, await verification.AccessLogs.CountAsync());
            Assert.Equal(allowed, await verification.MealEntitlements.Select(x => x.ConsumedQuantity).SingleAsync());
            Assert.Equal(allowed, await verification.AccessLogs.CountAsync(x => x.Decision == "ALLOW"));
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
