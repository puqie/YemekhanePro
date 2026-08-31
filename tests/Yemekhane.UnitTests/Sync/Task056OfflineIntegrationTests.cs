using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yemekhane.Application.Access;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Income;
using Yemekhane.Application.Realtime;
using Yemekhane.Application.Sms;
using Yemekhane.Application.Students;
using Yemekhane.Application.Sync;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Simulators;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;
using Yemekhane.Sync;
using Yemekhane.UnitTests.Api;
using Yemekhane.UnitTests.Persistence;

namespace Yemekhane.UnitTests.Sync;

[Collection(LocalDatabaseTests.CollectionName)]
public sealed class Task056OfflineIntegrationTests
{
    [Fact]
    public async Task LocalApiHealthRemainsAvailableWithoutInternet()
    {
        using var database = new TemporaryDatabase();
        using var api = new FileDatabaseApiFactory(database.ConnectionString);
        _ = api.Server;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            db.Add(new SystemSetting { Key = "Sync.Enabled", Value = "true" });
            db.Add(new SyncOperation
            {
                OperationId = Guid.NewGuid(), EntityName = "Student", EntityId = Guid.NewGuid().ToString("D"),
                OperationType = LocalOutbox.UpdateStudent, Timestamp = DateTimeOffset.UtcNow,
                DeviceId = "offline-test", Payload = "{}", SyncStatus = SyncOperationStatuses.RetryPending,
                LastError = "transport_error: Cloud unreachable"
            });
            await db.SaveChangesAsync();
        }
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content, StringComparison.Ordinal);
        Assert.Contains("\"localApi\":\"Available\"", content, StringComparison.Ordinal);
        Assert.Contains("\"cloud\":\"Offline\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalOfflineFlowPersistsOutboxAndSynchronizesAfterRestart()
    {
        using var database = new TemporaryDatabase();
        var ids = await WriteOfflineScenarioAsync(database.ConnectionString);

        SqliteConnection.ClearAllPools();
        await using (var offlineServices = await OpenAsync(database.ConnectionString))
        {
            await using var scope = offlineServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            var before = await db.SyncOperations.AsNoTracking()
                .OrderBy(x => YemekhaneDbContext.JulianDay(x.Timestamp)).ThenBy(x => x.OperationId).ToListAsync();
            Assert.Contains(before, x => x.OperationType == LocalOutbox.UpdateStudent);
            Assert.Contains(before, x => x.OperationType == LocalOutbox.UpdateCard);
            Assert.Contains(before, x => x.OperationType == LocalOutbox.CreateMealEntitlement);
            Assert.Contains(before, x => x.OperationType == LocalOutbox.CreateAccessLog && x.OperationId == ids.AccessOperationId);
            Assert.Contains(before, x => x.OperationType == LocalOutbox.CreateIncomeTransaction);
            Assert.Contains(before, x => x.OperationType == LocalOutbox.QueueSms);

            var unreachable = new FakeRemoteTransport { IsAvailable = false };
            using var engine = CreateEngine(new EfSyncOperationStore(db), unreachable);
            var result = await engine.RunOnceAsync();

            Assert.Equal(before.Count, result.RetryPending);
            Assert.Equal(before.Count, unreachable.CallCount);
            Assert.All(await db.SyncOperations.AsNoTracking().ToListAsync(),
                operation => Assert.Equal(SyncOperationStatuses.RetryPending, operation.SyncStatus));
        }

        SqliteConnection.ClearAllPools();
        var remote = new FakeRemoteTransport { ConflictOperationId = ids.StudentOperationId };
        await using (var restoredServices = await OpenAsync(database.ConnectionString))
        {
            await using var scope = restoredServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            using var engine = CreateEngine(new EfSyncOperationStore(db), remote);
            var result = await engine.RunOnceAsync();

            Assert.Equal(1, result.Conflicts);
            var conflict = await db.SyncOperations.AsNoTracking()
                .SingleAsync(x => x.OperationId == ids.StudentOperationId);
            Assert.Equal(SyncOperationStatuses.Conflict, conflict.SyncStatus);
            Assert.Contains("remoteVersion", conflict.LastError, StringComparison.Ordinal);
            // Motor SQLite julianday() ile siralar: bu bir kayan noktali gun degeridir ve
            // milisaniye altinda hassasiyet kaybeder. .NET tarafinda tam hassasiyetle
            // dogrulamak, ayni milisaniyeye dusen islemlerde rastgele basarisiz olur.
            // Motorun esitlik bozucusu (OperationId) ile ayni anahtar kullanilmalidir.
            Assert.Equal(
                remote.Sent.OrderBy(x => Math.Round(x.Timestamp.UtcDateTime.ToOADate(), 8)).ThenBy(x => x.OperationId),
                remote.Sent);

            await db.SyncOperations.Where(x => x.OperationId == ids.StudentOperationId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.SyncStatus, SyncOperationStatuses.RetryPending));
            remote.ConflictOperationId = null;
            await engine.RunOnceAsync();
            Assert.All(await db.SyncOperations.AsNoTracking().ToListAsync(),
                operation => Assert.Equal(SyncOperationStatuses.Synced, operation.SyncStatus));

            var replay = remote.Sent[0].OperationId;
            await db.SyncOperations.Where(x => x.OperationId == replay)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.SyncStatus, SyncOperationStatuses.RetryPending));
            var duplicateResult = await engine.RunOnceAsync();
            Assert.Equal(1, duplicateResult.DuplicateAccepted);
            Assert.Equal(1, remote.Applied.Count(x => x == replay));

            Assert.Equal(1, await db.Students.CountAsync(x => x.Id == ids.StudentId));
            Assert.Equal(1, await db.StudentCards.CountAsync(x => x.StudentId == ids.StudentId && x.IsActive));
            Assert.Equal(1, await db.MealEntitlements.CountAsync(x => x.StudentId == ids.StudentId));
            Assert.Equal(1, await db.AccessLogs.CountAsync(x => x.OperationId == ids.AccessOperationId));
            Assert.Equal(1, await db.MealUsages.CountAsync(x => x.StudentId == ids.StudentId));
        }
    }

    private static async Task<ScenarioIds> WriteOfflineScenarioAsync(string connectionString)
    {
        await using var services = await OpenAsync(connectionString);
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<YemekhaneDbContext>();
        var today = IstanbulToday();
        var mealType = new MealType { Name = "Offline Öğünü", IsActive = true };
        var device = new Device
        {
            Name = "OFFLINE-SIM", DeviceType = "Turnstile", ConnectionType = "Simulator",
            Direction = "Entry", ConnectionStatus = "Online", IsActive = true, HasTurnstile = true
        };
        var incomeType = new IncomeType { Name = "Offline Gelir", IsActive = true };
        db.AddRange(mealType, device, incomeType);
        await db.SaveChangesAsync();

        var students = provider.GetRequiredService<IStudentRepository>();
        var studentId = await students.AddAsync(new SaveStudentRequest("OFF-056", "Çevrim", "Dışı"), default);
        await students.UpdateAsync(studentId, new SaveStudentRequest("OFF-056", "Çevrimdışı", "Öğrenci"), default);
        await provider.GetRequiredService<ICardRepository>()
            .AssignAsync(studentId, "CARD-056", DateTimeOffset.UtcNow, default);
        await provider.GetRequiredService<IMealEntitlementRepository>()
            .UpsertBulkAsync([studentId], mealType.Id, [today], 1, "Offline", null, default);

        var simulator = new SimulatorTurnstile(device.Id, device.Name, new DeviceEndpoint("Simulator"));
        await simulator.ConnectAsync(default);
        var registry = new TurnstileRegistry();
        registry.Register(simulator);
        var operationId = Guid.NewGuid();
        var turnstile = new TurnstileService(provider.GetRequiredService<AccessDecisionService>(), registry,
            provider.GetRequiredService<ITurnstileEventStore>(), TimeProvider.System,
            provider.GetRequiredService<IRealtimeEventPublisher>());
        var access = await turnstile.ProcessCardReadAsync(new AccessCheckRequest(
            "CARD-056", device.Id, mealType.Id, IstanbulNoon(today), OperationId: operationId));
        Assert.Equal("ALLOW", access.AccessDecision?.Decision);
        Assert.Equal(HardwareCommandOutcome.Succeeded, access.HardwareOutcome);
        Assert.Single(simulator.CommandHistory);
        await simulator.DisposeAsync();

        var incomeOperationId = Guid.NewGuid();
        await provider.GetRequiredService<IIncomeRepository>().CreateTransactionAsync(
            new CreateIncomeTransactionRequest(incomeOperationId, studentId, "CARD-056",
                DateTimeOffset.UtcNow, incomeType.Id, 25m, "Offline"), Guid.NewGuid(), default);
        await provider.GetRequiredService<IBulkSmsRepository>().EnqueueAsync(
            [new SmsRecipientPreview(studentId, "Çevrimdışı Öğrenci", "Veli", "+905551112233", "Offline mesaj")],
            null, "task-056", default);

        var studentOperationId = await db.SyncOperations.AsNoTracking()
            .Where(x => x.OperationType == LocalOutbox.UpdateStudent)
            .OrderBy(x => YemekhaneDbContext.JulianDay(x.Timestamp)).Select(x => x.OperationId).FirstAsync();
        return new ScenarioIds(studentId, operationId, studentOperationId);
    }

    private static SyncEngine CreateEngine(ISyncOperationStore store, ISyncTransport transport) =>
        new(store, transport, new SyncEngineOptions
        {
            BatchSize = 100,
            MaxTransientRetries = 0,
            InitialRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero
        });

    private static async Task<ServiceProvider> OpenAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddYemekhaneInfrastructure(connectionString);
        services.AddSingleton<IRealtimeEventPublisher, NullRealtimePublisher>();
        services.AddSingleton(new WeekendPolicy());
        services.AddScoped<BusinessDayService>();
        services.AddScoped<AccessDecisionService>();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
        return provider;
    }

    private static DateOnly IstanbulToday()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }

    private static DateTimeOffset IstanbulNoon(DateOnly date) =>
        new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(3));

    private sealed record ScenarioIds(Guid StudentId, Guid AccessOperationId, Guid StudentOperationId);

    private sealed class FakeRemoteTransport : ISyncTransport
    {
        private readonly HashSet<Guid> applied = [];
        public bool IsAvailable { get; init; } = true;
        public Guid? ConflictOperationId { get; set; }
        public int CallCount { get; private set; }
        public List<SyncRequestOperation> Sent { get; } = [];
        public IReadOnlyCollection<Guid> Applied => applied;

        public Task<SyncTransportResult> SendAsync(SyncRequestOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Sent.Add(operation);
            if (!IsAvailable)
                return Task.FromResult(new SyncTransportResult(SyncTransportOutcome.TransientFailure,
                    "transport_error", "Cloud unreachable"));
            if (operation.OperationId == ConflictOperationId)
                return Task.FromResult(new SyncTransportResult(SyncTransportOutcome.Conflict,
                    "version_conflict", "Remote changed", "{\"remoteVersion\":2}"));
            return Task.FromResult(applied.Add(operation.OperationId)
                ? new SyncTransportResult(SyncTransportOutcome.Success)
                : new SyncTransportResult(SyncTransportOutcome.Duplicate));
        }
    }

    private sealed class NullRealtimePublisher : IRealtimeEventPublisher
    {
        public ValueTask PublishAsync(AccessDecisionCommittedEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(TurnstileResultEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(DeviceStatusChangedEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(NotificationEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "Yemekhane.Task056", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            ConnectionString = LocalDatabaseConnection.Resolve(null, DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string ConnectionString { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class FileDatabaseApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["Authentication:Jwt:SigningKey"] = YemekhaneApiFactory.SigningKey,
                    ["Authentication:Jwt:Issuer"] = "yemekhane-test",
                    ["Authentication:Jwt:Audience"] = "yemekhane-test",
                    ["Authentication:Bootstrap:Enabled"] = "false"
                }));
            return base.CreateHost(builder);
        }
    }
}
