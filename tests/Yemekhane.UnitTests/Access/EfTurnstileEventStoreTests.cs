using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Access;

public sealed class EfTurnstileEventStoreTests
{
    [Fact]
    public async Task FailedGrantAtomicallyRestoresEntitlementAndPersistsEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new YemekhaneDbContext(
            new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "T-21", FirstName = "Test", LastName = "Öğrenci" };
        var meal = new MealType { Name = "TASK021 Öğünü" };
        var device = new Device
        {
            Name = "TASK021 Turnike", DeviceType = "Fake", ConnectionType = "Fake", Direction = "Entry",
            ConnectionStatus = "Connected", HasTurnstile = true
        };
        var entitlement = new MealEntitlement
        {
            StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = DateOnly.FromDateTime(DateTime.Today),
            Quantity = 1, ConsumedQuantity = 1, Status = "Active"
        };
        var operationId = Guid.NewGuid();
        var accessLog = new AccessLog
        {
            Timestamp = DateTimeOffset.UtcNow, StudentId = student.Id, DeviceId = device.Id,
            MealTypeId = meal.Id, CardNumber = "1234", Decision = "ALLOW", Reason = "Geçiş onaylandı",
            Direction = "Entry", ReaderSource = "Test", OperationId = operationId
        };
        var usage = new MealUsage
        {
            EntitlementId = entitlement.Id, StudentId = student.Id, MealTypeId = meal.Id,
            AccessLogId = accessLog.Id, UsedAt = accessLog.Timestamp
        };
        context.AddRange(student, meal, device, entitlement, accessLog, usage);
        await context.SaveChangesAsync();

        var result = await new EfTurnstileEventStore(context).RecordAsync(
            new TurnstileEventData(device.Id, operationId, DateTimeOffset.UtcNow, "GRANT", "REVIEW_REQUIRED",
                "RELAY_FAILED: Röle yanıt vermedi"), compensateConsumption: true, CancellationToken.None);

        Assert.True(result.ConsumptionCompensated);
        Assert.Equal(0, await context.MealEntitlements.Where(x => x.Id == entitlement.Id)
            .Select(x => x.ConsumedQuantity).SingleAsync());
        Assert.Empty(await context.MealUsages.ToListAsync());
        Assert.Equal("ERROR", await context.AccessLogs.Where(x => x.Id == accessLog.Id)
            .Select(x => x.Decision).SingleAsync());
        Assert.Equal("COMPENSATED_RETRY_REQUIRED",
            await context.TurnstileEvents.Select(x => x.Result).SingleAsync());
    }

    /// <summary>
    /// Başarılı geçişte telafi bayrağı false gelir; hak İADE EDİLMEMELİDİR.
    /// Bu dal daha önce hiç test edilmiyordu, bayrağın tamamen yok sayılması fark edilmezdi.
    /// </summary>
    [Fact]
    public async Task SuccessfulGrantDoesNotRefundEntitlement()
    {
        await using var fixture = await TurnstileFixture.CreateAsync();

        var result = await new EfTurnstileEventStore(fixture.Context).RecordAsync(
            new TurnstileEventData(fixture.Device.Id, fixture.OperationId, DateTimeOffset.UtcNow, "GRANT", "SUCCEEDED"),
            compensateConsumption: false, CancellationToken.None);

        Assert.False(result.ConsumptionCompensated);
        Assert.Equal(1, await fixture.Context.MealEntitlements.Where(x => x.Id == fixture.Entitlement.Id)
            .Select(x => x.ConsumedQuantity).SingleAsync());
        Assert.Single(await fixture.Context.MealUsages.ToListAsync());
        Assert.Equal("ALLOW", await fixture.Context.AccessLogs.Where(x => x.Id == fixture.AccessLog.Id)
            .Select(x => x.Decision).SingleAsync());
        Assert.Equal("SUCCEEDED", await fixture.Context.TurnstileEvents.Select(x => x.Result).SingleAsync());
    }

    /// <summary>
    /// OperationId eşleşmezse telafi bloğu sessizce atlanıyor ve metot yine de başarı dönüyor.
    /// Bu test o davranışı sabitler: hak tüketilmiş kalır ve olay yine de kaydedilir.
    /// </summary>
    [Fact]
    public async Task UnmatchedOperationIdSkipsCompensationButStillRecordsEvent()
    {
        await using var fixture = await TurnstileFixture.CreateAsync();

        var result = await new EfTurnstileEventStore(fixture.Context).RecordAsync(
            new TurnstileEventData(fixture.Device.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, "GRANT",
                "REVIEW_REQUIRED", "RELAY_FAILED"), compensateConsumption: true, CancellationToken.None);

        Assert.False(result.ConsumptionCompensated);
        Assert.Equal(1, await fixture.Context.MealEntitlements.Where(x => x.Id == fixture.Entitlement.Id)
            .Select(x => x.ConsumedQuantity).SingleAsync());
        var stored = Assert.Single(await fixture.Context.TurnstileEvents.ToListAsync());
        Assert.Equal("REVIEW_REQUIRED", stored.Result);
        Assert.Null(stored.AccessLogId);
    }

    private sealed class TurnstileFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TurnstileFixture(SqliteConnection connection, YemekhaneDbContext context, Device device,
            MealEntitlement entitlement, AccessLog accessLog, Guid operationId)
        {
            this.connection = connection;
            Context = context; Device = device; Entitlement = entitlement; AccessLog = accessLog; OperationId = operationId;
        }

        public YemekhaneDbContext Context { get; }
        public Device Device { get; }
        public MealEntitlement Entitlement { get; }
        public AccessLog AccessLog { get; }
        public Guid OperationId { get; }

        public static async Task<TurnstileFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(
                new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            var student = new Student { StudentNo = "T-22", FirstName = "Test", LastName = "Öğrenci" };
            var meal = new MealType { Name = "Öğle" };
            var device = new Device { Name = "Turnike", DeviceType = "Fake", ConnectionType = "Fake",
                Direction = "Entry", ConnectionStatus = "Connected", HasTurnstile = true };
            var entitlement = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
                EntitlementDate = new DateOnly(2026, 9, 14), Quantity = 1, ConsumedQuantity = 1, Status = "Active" };
            var operationId = Guid.NewGuid();
            var accessLog = new AccessLog { Timestamp = DateTimeOffset.UtcNow, StudentId = student.Id,
                DeviceId = device.Id, MealTypeId = meal.Id, CardNumber = "1234", Decision = "ALLOW",
                Reason = "Geçiş onaylandı", Direction = "Entry", ReaderSource = "Test", OperationId = operationId };
            var usage = new MealUsage { EntitlementId = entitlement.Id, StudentId = student.Id,
                MealTypeId = meal.Id, AccessLogId = accessLog.Id, UsedAt = accessLog.Timestamp };
            context.AddRange(student, meal, device, entitlement, accessLog, usage);
            await context.SaveChangesAsync();
            return new TurnstileFixture(connection, context, device, entitlement, accessLog, operationId);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
