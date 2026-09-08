using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Maintenance;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Maintenance;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Maintenance;

/// <summary>
/// Yil sonu sifirlamasi: isletim verisi gider, tanimlar kalir, once yedek alinir. Veli 1-2 yil
/// onceki odemesini sorabildigi icin tahsilat, bakiye ve veli kayitlari ile ogrenci sicili
/// SILINMEZ; ogrenci pasife alinir. Kart numarasi pasif kayitlar dahil tekil oldugu icin
/// kartlar silinir (mezunun karti yeni ogrenciye verilebilsin). Gercek SQLite ile kosar cunku
/// silme sirasi yabanci anahtarlara bagli; bellek-ici sahte baglam bu hatayi goremezdi.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class YearEndResetServiceTests
{
    [Fact]
    public async Task PreviewCountsOperationalTablesAndStudentsToDeactivate()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSchoolYearAsync();

        var preview = await fixture.Service.PreviewAsync(CancellationToken.None);

        var items = preview.Items.ToDictionary(item => item.Key, item => item);
        Assert.Equal(1, items["students"].Count);
        Assert.Equal(YearEndResetActions.Deactivate, items["students"].Action);
        Assert.Equal(1, items["cards"].Count);
        Assert.Equal(1, items["entitlements"].Count);
        Assert.Equal(1, items["meal-usages"].Count);
        Assert.Equal(1, items["access-logs"].Count);
        Assert.Equal(1, items["turnstile-events"].Count);
        Assert.Equal(1, items["device-card-states"].Count);
        Assert.Equal(1, items["leaves"].Count);
        // Korunanlar onizlemede YOKTUR: veli, tahsilat, bakiye.
        Assert.DoesNotContain("parents", items.Keys);
        Assert.DoesNotContain("income", items.Keys);
        Assert.DoesNotContain("balance-entries", items.Keys);
        Assert.All(items.Values.Where(x => x.Key != "students"), x => Assert.Equal(YearEndResetActions.Delete, x.Action));
        Assert.Equal(8, preview.Total);
        Assert.Equal(7, preview.DeletedTotal);
        Assert.Equal(1, preview.DeactivatedTotal);
    }

    [Fact]
    public async Task WrongConfirmationDeletesNothingAndTakesNoBackup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSchoolYearAsync();

        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => fixture.Service.ResetAsync("sıfırla", CancellationToken.None));

        Assert.Contains("SIFIRLA", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Backup.Calls);
        Assert.Equal(1, await fixture.Db.Students.CountAsync(x => x.IsActive));
        Assert.Equal(1, await fixture.Db.AccessLogs.CountAsync());
    }

    [Fact]
    public async Task ResetDeletesOperationalDataKeepsMoneyAndRosterAndAudits()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSchoolYearAsync();

        var result = await fixture.Service.ResetAsync(" SIFIRLA ", CancellationToken.None);

        Assert.Equal(1, fixture.Backup.Calls);
        Assert.Equal("yedek-2026-09-08.zip", result.BackupFileName);
        Assert.Equal(8, result.Total);
        Assert.Equal(7, result.DeletedTotal);
        Assert.Equal(1, result.DeactivatedTotal);

        // Silinen isletim verisi.
        Assert.Equal(0, await fixture.Db.StudentCards.CountAsync());
        Assert.Equal(0, await fixture.Db.MealEntitlements.CountAsync());
        Assert.Equal(0, await fixture.Db.MealUsages.CountAsync());
        Assert.Equal(0, await fixture.Db.AccessLogs.CountAsync());
        Assert.Equal(0, await fixture.Db.TurnstileEvents.CountAsync());
        Assert.Equal(0, await fixture.Db.DeviceCardStates.CountAsync());
        Assert.Equal(0, await fixture.Db.Set<StudentLeave>().CountAsync());

        // Korunan: sicil (pasif), veli, tahsilat, bakiye.
        var student = await fixture.Db.Students.SingleAsync();
        Assert.False(student.IsActive);
        Assert.False(student.IsDeleted);
        Assert.Equal("1001", student.StudentNo);
        Assert.Equal(1, await fixture.Db.Parents.CountAsync());
        Assert.Equal(1, await fixture.Db.Set<IncomeTransaction>().CountAsync());
        Assert.Equal(1, await fixture.Db.StudentBalanceEntries.CountAsync());
        Assert.Equal(150, (await fixture.Db.Set<IncomeTransaction>().SingleAsync()).Amount);

        // Tanimlar.
        Assert.Equal(1, await fixture.Db.Set<MealType>().CountAsync());
        Assert.Equal(1, await fixture.Db.Set<SchoolClass>().CountAsync());
        Assert.Equal(1, await fixture.Db.Devices.CountAsync());
        Assert.Equal(1, await fixture.Db.Set<IncomeType>().CountAsync());

        var audit = Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "YearEndReset").ToListAsync());
        Assert.Contains("7 kayıt silindi", audit.Description, StringComparison.Ordinal);
        Assert.Contains("1 öğrenci pasife alındı", audit.Description, StringComparison.Ordinal);
        Assert.Contains("yedek-2026-09-08.zip", audit.Description, StringComparison.Ordinal);
    }

    /// <summary>Pasif kalan ogrenci yeni yilin listesiyle geri gelir; ikinci sifirlama artik sayim vermez.</summary>
    [Fact]
    public async Task SecondResetAffectsNothingNew()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSchoolYearAsync();
        await fixture.Service.ResetAsync("SIFIRLA", CancellationToken.None);

        var preview = await fixture.Service.PreviewAsync(CancellationToken.None);

        Assert.Equal(0, preview.Total);
    }

    /// <summary>Yedek alinamazsa sifirlama hic baslamaz; veri kaybi yedeksiz olmaz.</summary>
    [Fact]
    public async Task BackupFailureAbortsBeforeAnythingIsDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSchoolYearAsync();
        fixture.Backup.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ResetAsync("SIFIRLA", CancellationToken.None));

        Assert.Equal(1, await fixture.Db.Students.CountAsync(x => x.IsActive));
        Assert.Equal(1, await fixture.Db.MealUsages.CountAsync());
        Assert.DoesNotContain(await fixture.Db.AuditLogs.ToListAsync(), x => x.Action == "YearEndReset");
    }

    private sealed class FakeBackup : IYearEndBackup
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public Task<string> CreateSafetyBackupAsync(CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("Yedek klasörüne yazılamadı.");
            Calls++;
            return Task.FromResult("yedek-2026-09-08.zip");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection connection = null!;
        public YemekhaneDbContext Db { get; private set; } = null!;
        public FakeBackup Backup { get; } = new();
        public EfYearEndResetService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.connection = new SqliteConnection($"Data Source=file:year-end-{Guid.NewGuid():N}?mode=memory&cache=shared");
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(fixture.connection).Options;
            fixture.Db = new YemekhaneDbContext(options);
            await fixture.Db.Database.MigrateAsync();
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
            fixture.Service = new EfYearEndResetService(fixture.Db, fixture.Backup,
                new AuditService(new EfAuditRepository(fixture.Db, clock), new SystemAuditContext()), clock);
            return fixture;
        }

        public async Task SeedSchoolYearAsync()
        {
            var now = new DateTimeOffset(2026, 9, 8, 9, 20, 0, TimeSpan.FromHours(3));
            var today = new DateOnly(2026, 9, 8);
            var meal = new MealType { Name = "Öğle" };
            var schoolClass = new SchoolClass { Name = "5-A" };
            var student = new Student { StudentNo = "1001", FirstName = "Ali", LastName = "Kaya", ClassId = schoolClass.Id };
            var card = new StudentCard { StudentId = student.Id, CardNumber = "8247129", ValidFrom = now };
            var parent = new Parent { StudentId = student.Id, Name = "Ayşe Kaya", NormalizedPhone = "905551112233" };
            var device = new Device
            {
                Name = "Yemekhane Turnikesi", DeviceType = "SC403", ConnectionType = "Ethernet", Direction = "Entry",
                IpAddress = "169.254.198.201", IpPort = 4370, ConnectionStatus = "Disconnected", HasTurnstile = true
            };
            var entitlement = new MealEntitlement
            {
                StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = today, Quantity = 1, ConsumedQuantity = 1, Status = "Active"
            };
            var access = new AccessLog
            {
                Timestamp = now, DeviceId = device.Id, CardId = card.Id, StudentId = student.Id, MealTypeId = meal.Id,
                CardNumber = card.CardNumber, Decision = "ALLOW", Reason = "Geçiş onaylandı", Direction = "Entry",
                ReaderSource = "SC403", OperationId = Guid.NewGuid()
            };
            var usage = new MealUsage
            {
                EntitlementId = entitlement.Id, StudentId = student.Id, MealTypeId = meal.Id, AccessLogId = access.Id, UsedAt = now
            };
            var turnstile = new TurnstileEvent { DeviceId = device.Id, AccessLogId = access.Id, Timestamp = now, Command = "GRANT", Result = "SUCCEEDED" };
            var cardState = new DeviceCardState
            {
                DeviceId = device.Id, CardId = card.Id, StudentId = student.Id, CardNumber = card.CardNumber, Status = "Loaded"
            };
            var leave = new StudentLeave { StudentId = student.Id, StartsOn = today, EndsOn = today, LeaveType = "Sick", EntitlementBehavior = "Cancel" };
            var incomeType = new IncomeType { Name = "Yemek Ücreti" };
            var income = new IncomeTransaction
            {
                OperationId = Guid.NewGuid(), StudentId = student.Id, IncomeTypeId = incomeType.Id, TransactionAt = now,
                Amount = 150, CreatedBy = Guid.NewGuid()
            };
            var balance = new StudentBalanceEntry
            {
                StudentId = student.Id, AmountCents = 15000, Kind = "TopUp", ReferenceType = "IncomeTransaction",
                ReferenceId = income.Id, OccurredAt = now
            };

            Db.AddRange(meal, schoolClass, student, card, parent, device, entitlement, access, usage, turnstile,
                cardState, leave, incomeType, income, balance);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
