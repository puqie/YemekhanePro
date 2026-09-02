using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Balances;
using Yemekhane.Infrastructure.Income;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Balances;

/// <summary>
/// Bakiye yukleme: gelir islemi + defter satiri tek transaction'da; gelir turu otomatik;
/// iptal iade yazar ve eksi bakiyeyi uyarir; ayni OperationId ikinci kez yuklemez.
/// </summary>
public sealed class StudentBalanceServiceTests
{
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task YuklemeGelirIslemiVeDefterSatiriniBirlikteYazar()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, " 5001 ", 500m, " Eylül bakiyesi "), ActorId);

        Assert.Equal(500m, result.Balance);
        Assert.Equal(500m, result.Available);
        Assert.Equal(StudentBalanceIncomeType.Name, result.Transaction.IncomeTypeName);
        Assert.Equal(500m, result.Transaction.Amount);
        Assert.Equal(student.Id, result.Transaction.StudentId);
        Assert.Equal("Eylül bakiyesi", result.Entry.Note);
        Assert.Equal(StudentBalanceEntryKinds.TopUp, result.Entry.Kind);
        Assert.Equal(result.Transaction.Id, result.Entry.ReferenceId);

        var income = Assert.Single(await fixture.Context.Set<IncomeTransaction>().ToListAsync());
        var entry = Assert.Single(await fixture.Context.StudentBalanceEntries.ToListAsync());
        Assert.Equal(income.Id, entry.ReferenceId);
        Assert.Equal(50_000, entry.AmountCents);
        Assert.Equal(ActorId, entry.CreatedBy);
        var type = Assert.Single(await fixture.Context.Set<IncomeType>().ToListAsync());
        Assert.Equal(StudentBalanceIncomeType.Name, type.Name);
        Assert.True(type.IsActive);
        Assert.Contains(await fixture.Context.Set<AuditLog>().ToListAsync(), x => x.Action == "BalanceTopUp");
    }

    [Fact]
    public async Task PasifBakiyeYuklemeTuruYenidenAcilirYenisiOlusmaz()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddStudent("5001");
        fixture.Context.Add(new IncomeType { Name = "bakiye yükleme", IsActive = false });
        await fixture.Context.SaveChangesAsync();

        await fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, "5001", 10m), ActorId);

        var type = Assert.Single(await fixture.Context.Set<IncomeType>().ToListAsync());
        Assert.True(type.IsActive);
    }

    [Fact]
    public async Task AyniOperationIdIkinciKezYuklemez()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();
        var operationId = Guid.NewGuid();

        var first = await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 200m, OperationId: operationId), ActorId);
        var replay = await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 200m, OperationId: operationId), ActorId);

        Assert.Equal(first.Transaction.Id, replay.Transaction.Id);
        Assert.Equal(200m, replay.Balance);
        Assert.Single(await fixture.Context.StudentBalanceEntries.ToListAsync());
        Assert.Single(await fixture.Context.Set<IncomeTransaction>().ToListAsync());
    }

    [Fact]
    public async Task IptalIadeYazarVeHarcanmisBakiyedeEksiyeDusmeUyarisiDoner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();
        var topUp = await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 100m), ActorId);
        // Turnike 75 ₺ dusmus olsun.
        fixture.Context.Add(new StudentBalanceEntry { StudentId = student.Id, AmountCents = -7_500, Kind = StudentBalanceEntryKinds.Deduction,
            ReferenceType = StudentBalanceReferenceTypes.AccessLog, ReferenceId = Guid.NewGuid(), OccurredAt = Now.AddHours(1) });
        await fixture.Context.SaveChangesAsync();

        var voided = await fixture.Income.VoidAsync(topUp.Transaction.Id, "Yanlış öğrenci", ActorId);

        Assert.True(voided.IsVoided);
        Assert.NotNull(voided.Warning);
        Assert.Contains("EKSİYE", voided.Warning);
        var summary = await fixture.Service.GetAsync(student.Id);
        Assert.Equal(-75m, summary.Balance);
        Assert.Equal(-75m, summary.Available);
        var refund = Assert.Single(summary.Entries.Items, x => x.Kind == StudentBalanceEntryKinds.Refund);
        Assert.Equal(-100m, refund.Amount);
        Assert.Equal(topUp.Transaction.Id, refund.ReferenceId);
        Assert.Equal(3, summary.Entries.TotalCount);
    }

    [Fact]
    public async Task HarcanmamisYuklemeninIptaliUyariVermez()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();
        var topUp = await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 100m), ActorId);

        var voided = await fixture.Income.VoidAsync(topUp.Transaction.Id, "Mükerrer", ActorId);

        Assert.Null(voided.Warning);
        Assert.Equal(0m, (await fixture.Service.GetAsync(student.Id)).Balance);
    }

    [Fact]
    public async Task SadeGelirIptaliDefteriEtkilemez()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        var type = new IncomeType { Name = "Servis Ücreti" };
        fixture.Context.Add(type);
        await fixture.Context.SaveChangesAsync();
        var income = await fixture.Income.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student.Id, null, Now, type.Id, 40m), ActorId);

        var voided = await fixture.Income.VoidAsync(income.Id, "Hatalı", ActorId);

        Assert.Null(voided.Warning);
        Assert.Empty(await fixture.Context.StudentBalanceEntries.ToListAsync());
    }

    [Fact]
    public async Task OzetSayfaliVeSonHareketOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();
        await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 100m, TransactionAt: Now), ActorId);
        await fixture.Service.TopUpAsync(new BalanceTopUpRequest(student.Id, null, 50m, TransactionAt: Now.AddMinutes(5), ExpiresOn: new DateOnly(2026, 9, 30)), ActorId);

        var page = await fixture.Service.GetAsync(student.Id, page: 1, pageSize: 1);

        Assert.Equal(150m, page.Balance);
        Assert.Equal(150m, page.Available);
        Assert.Equal("5001", page.StudentNo);
        Assert.Equal(2, page.Entries.TotalCount);
        var latest = Assert.Single(page.Entries.Items);
        Assert.Equal(50m, latest.Amount);
        Assert.Equal(new DateOnly(2026, 9, 30), latest.ExpiresOn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(12.345)]
    [InlineData(100_001)]
    public async Task GecersizTutarReddedilir(double amount)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, "5001", (decimal)amount), ActorId));
        Assert.Empty(await fixture.Context.StudentBalanceEntries.ToListAsync());
    }

    [Fact]
    public async Task GecmisBitisTarihiVeBilinmeyenOgrenciReddedilir()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddStudent("5001");
        await fixture.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, "5001", 10m, ExpiresOn: new DateOnly(2026, 9, 1)), ActorId));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, null, 10m), ActorId));
        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            fixture.Service.TopUpAsync(new BalanceTopUpRequest(null, "9999", 10m), ActorId));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.GetAsync(Guid.NewGuid()));
        Assert.Empty(await fixture.Context.Set<IncomeTransaction>().ToListAsync());
    }

    private sealed class Fixture(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        private static readonly FixedTimeProvider Clock = new(Now);
        public YemekhaneDbContext Context { get; } = context;
        public StudentBalanceService Service { get; } = new(new EfStudentBalanceRepository(context, Clock), Clock);
        public IncomeService Income { get; } = new(new EfIncomeRepository(context, Clock));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            return new Fixture(connection, context);
        }

        public Student AddStudent(string no)
        {
            var student = new Student { StudentNo = no, FirstName = "Ada", LastName = "Akgün" };
            Context.Add(student);
            return student;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
