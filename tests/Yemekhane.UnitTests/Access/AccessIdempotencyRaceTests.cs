using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Realtime;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Access;

/// <summary>
/// Ayni OperationId ile es zamanli gelen ALLOW isteklerinde kaybeden dalin davranisini dogrular.
/// Yaris kosullari nadiren tetiklendigi icin kazananin durumu onceden kurulur ve
/// kaybeden dal deterministik olarak calistirilir.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class AccessIdempotencyRaceTests
{
    [Fact]
    public async Task ConcurrentSameOperationIdAllReturnAllow()
    {
        // Turnike ayni OperationId ile tekrar denedidiginde, ilk istek henuz commit etmemis olabilir.
        // Bu durumda tum dallar ayni yaniti (ALLOW) almalidir; hak yalnizca bir kez tuketilir.
        for (var loop = 0; loop < 12; loop++)
        {
            await using var database = await RaceDatabase.CreateAsync(quantity: 1);
            var operationId = Guid.NewGuid();
            var decisions = await database.RaceAsync(8, operationId);

            Assert.All(decisions, x => Assert.Equal("ALLOW", x.Decision));
            // Tekrar yanitinda da ogrenci adi dolu olmalidir; turnike ekraninda bos isim gorunmemeli.
            Assert.All(decisions, x => Assert.Equal("Race Student", x.StudentName));
            await using var verification = database.CreateContext();
            Assert.Equal(1, await verification.MealUsages.CountAsync());
            Assert.Equal(1, await verification.MealEntitlements.Select(x => x.ConsumedQuantity).SingleAsync());
        }
    }

    [Fact]
    public async Task ReplayOfCommittedOperationIdReturnsAllowNotDeny()
    {
        await using var database = await RaceDatabase.CreateAsync(quantity: 1);
        var operationId = Guid.NewGuid();

        var first = await database.CheckAsync(operationId);
        Assert.Equal("ALLOW", first.Decision);

        // Ayni OperationId ile tekrar gelen istek (turnike yeniden denemesi) ayni yaniti almalidir.
        var replay = await database.CheckAsync(operationId);
        Assert.Equal("ALLOW", replay.Decision);
        Assert.Equal("Geçiş onaylandı", replay.Reason);

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.MealUsages.CountAsync());
        Assert.Equal(1, await verification.MealEntitlements.Select(x => x.ConsumedQuantity).SingleAsync());
    }

    [Fact]
    public async Task LoserOfOperationIdRaceDoesNotDoubleConsumeEntitlement()
    {
        await using var database = await RaceDatabase.CreateAsync(quantity: 2);
        var operationId = Guid.NewGuid();

        // Kazanan dal: hakki tuketip ALLOW kaydini yazar.
        await using (var winner = database.CreateContext())
        {
            var repository = new EfAccessDecisionRepository(winner);
            var accepted = await repository.TryConsumeAndLogAsync(database.EntitlementId,
                database.Request(operationId), database.Allowed(operationId), default);
            Assert.True(accepted);
        }

        // Kaybeden dal: on kontrolu kazanan commit etmeden gecmis gibi ayni cagriyi yapar.
        await using (var loser = database.CreateContext())
        {
            var repository = new EfAccessDecisionRepository(loser);
            var accepted = await repository.TryConsumeAndLogAsync(database.EntitlementId,
                database.Request(operationId), database.Allowed(operationId), default);
            Assert.True(accepted);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.AccessLogs.CountAsync());
        Assert.Equal(1, await verification.MealUsages.CountAsync());
        Assert.Equal(1, await verification.MealEntitlements.Select(x => x.ConsumedQuantity).SingleAsync());
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

    private sealed class RaceDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly DbContextOptions<YemekhaneDbContext> options;

        private RaceDatabase(string directory, DbContextOptions<YemekhaneDbContext> options)
        { this.directory = directory; this.options = options; }

        public Guid StudentId { get; private init; }
        public Guid MealTypeId { get; private init; }
        public Guid DeviceId { get; private init; }
        public Guid EntitlementId { get; private init; }
        public DateTimeOffset Timestamp { get; } = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

        public static async Task<RaceDatabase> CreateAsync(int quantity)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Yemekhane.IdempotencyRace", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "race.db"), Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, ForeignKeys = true, DefaultTimeout = 5
            }.ToString();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options;
            await using var setup = new YemekhaneDbContext(options);
            await setup.Database.MigrateAsync();
            var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Race", LastName = "Student" };
            var meal = new MealType { Name = "Öğle" };
            var device = new Device { Name = "Turnstile", DeviceType = "SF300", ConnectionType = "Ethernet",
                Direction = "Entry", ConnectionStatus = "Connected" };
            var entitlement = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
                EntitlementDate = new DateOnly(2026, 9, 14), Quantity = quantity, Status = "Active" };
            setup.AddRange(student, meal, device, entitlement);
            setup.Add(new StudentCard { StudentId = student.Id, CardNumber = "race-card", ValidFrom = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
            return new RaceDatabase(directory, options)
            { StudentId = student.Id, MealTypeId = meal.Id, DeviceId = device.Id, EntitlementId = entitlement.Id };
        }

        public YemekhaneDbContext CreateContext() => new(options);

        public AccessCheckRequest Request(Guid operationId) =>
            new("race-card", DeviceId, MealTypeId, Timestamp, OperationId: operationId);

        public AccessDecision Allowed(Guid operationId) =>
            new("ALLOW", "Geçiş onaylandı", StudentId, "Race Student", DeviceId, MealTypeId, Timestamp, operationId);

        public async Task<IReadOnlyList<AccessDecision>> RaceAsync(int count, Guid operationId)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, count).Select(async _ =>
            {
                await gate.Task;
                return await CheckAsync(operationId);
            }).ToArray();
            gate.SetResult();
            return await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task<AccessDecision> CheckAsync(Guid operationId)
        {
            await using var context = CreateContext();
            var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
                new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), new NullRealtimePublisher());
            return await service.CheckAccessAsync(Request(operationId));
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
