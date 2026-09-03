using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Realtime;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Concurrency;

/// <summary>
/// ODEMELI (bakiye) yolun es zamanlilik testleri.
///
/// Yemek HAKKI yolu Task055ConcurrencyTests ile kapsanmisti; BAKIYE yolu kapsanmamisti.
/// Aradaki fark onemli: hak dusumu kosullu bir UPDATE ile atomiktir
/// (WHERE ConsumedQuantity &lt; Quantity), bakiye ise defter satiri EKLEYEREK dusulur --
/// yani "once oku, sonra yaz" kalibi. Bu kalip yanlis korunursa ayni para iki kez
/// harcanir ve okul zarar eder; bu testler bunun olmadigini kanitlar.
/// </summary>
public sealed class BalanceRaceTests
{
    /// <summary>
    /// Bakiyesi TAM bir ogune yeten ogrenciye, FARKLI OperationId'lerle es zamanli
    /// okutmalar. Idempotency kalkani burada devrede DEGILDIR (her istek ayri islem);
    /// koruma yalnizca veritabani yazma kilidinden gelir.
    ///
    /// Beklenen: tam 1 ALLOW ve bakiye asla negatife dusmez.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task AyniParaBirdenFazlaOgunIcinHarcanamaz(int swipes)
    {
        await using var db = await BalanceTestDatabase.CreateAsync(balanceCents: 7500, mealPriceCents: 7500);

        var decisions = await db.RunAccessRaceAsync(swipes);

        var allowed = decisions.Count(x => x.Decision == "ALLOW");
        Assert.Equal(1, allowed);
        // Yarisin GERCEKTEN olustugunu kanitlar: hepsi sirayla kossaydi test hicbir sey
        // gostermezdi. Olculdu -- 20 istek ayni anda iceride oluyor.
        Assert.True(db.PeakConcurrent > 1, $"Es zamanlilik olusmadi (zirve={db.PeakConcurrent}).");

        await using var verify = db.CreateContext();
        var totalCents = await verify.Set<StudentBalanceEntry>()
            .Where(x => x.StudentId == db.StudentId).SumAsync(x => x.AmountCents);
        Assert.True(totalCents >= 0, $"Bakiye NEGATIFE dustu: {totalCents} kurus ({swipes} es zamanli okutma).");
        Assert.Equal(0, totalCents);

        // Her ALLOW icin tam bir dusum satiri olmali: fazlasi cift harcama, azi bedava ogun.
        var deductions = await verify.Set<StudentBalanceEntry>()
            .CountAsync(x => x.StudentId == db.StudentId && x.Kind == StudentBalanceEntryKinds.Deduction);
        Assert.Equal(allowed, deductions);
    }

    /// <summary>
    /// Bakiye tam N ogune yetiyorsa, N'den fazla es zamanli okutma yapilsa bile
    /// yalnizca N tanesi gecmelidir. Tek ogunluk testin genellenmis hali: sinir
    /// degerinde degil, ortada da dogru sayilmali.
    /// </summary>
    [Fact]
    public async Task BakiyeKacOguneYetiyorsaOKadarGecisOlur()
    {
        await using var db = await BalanceTestDatabase.CreateAsync(balanceCents: 22_500, mealPriceCents: 7500);

        var decisions = await db.RunAccessRaceAsync(12);

        Assert.Equal(3, decisions.Count(x => x.Decision == "ALLOW"));

        await using var verify = db.CreateContext();
        var totalCents = await verify.Set<StudentBalanceEntry>()
            .Where(x => x.StudentId == db.StudentId).SumAsync(x => x.AmountCents);
        Assert.Equal(0, totalCents);
    }

    /// <summary>
    /// AYNI OperationId ile tekrar (turnike yeniden denemesi, ag hatasi sonrasi retry):
    /// para YALNIZCA BIR KEZ dusulmelidir. Bu, yukaridakinden farkli bir korumadir --
    /// burada idempotency kalkani devrededir.
    /// </summary>
    [Fact]
    public async Task AyniIslemNumarasiylaTekrarParaIkiKezDusmez()
    {
        await using var db = await BalanceTestDatabase.CreateAsync(balanceCents: 7500, mealPriceCents: 7500);
        var operationId = Guid.NewGuid();

        var decisions = await db.RunAccessRaceAsync(8, operationId);

        // Ayni islem: hepsi ayni yaniti almalidir.
        Assert.All(decisions, x => Assert.Equal("ALLOW", x.Decision));

        await using var verify = db.CreateContext();
        var deductions = await verify.Set<StudentBalanceEntry>()
            .CountAsync(x => x.StudentId == db.StudentId && x.Kind == StudentBalanceEntryKinds.Deduction);
        Assert.Equal(1, deductions);
        Assert.Equal(1, await verify.AccessLogs.CountAsync());
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

    private sealed class BalanceTestDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly DbContextOptions<YemekhaneDbContext> options;

        private BalanceTestDatabase(string directory, DbContextOptions<YemekhaneDbContext> options)
        { this.directory = directory; this.options = options; }

        public Guid StudentId { get; private init; }
        public Guid MealTypeId { get; private init; }
        public Guid DeviceId { get; private init; }
        public string CardNumber => "balance-race-card";
        public DateTimeOffset Timestamp { get; } = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

        public static async Task<BalanceTestDatabase> CreateAsync(long balanceCents, long mealPriceCents)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Yemekhane.BalanceRace", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "balance-race.db"), Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, ForeignKeys = true, DefaultTimeout = 5
            }.ToString();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options;
            await using var setup = new YemekhaneDbContext(options);
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync(
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");

            var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Bakiye", LastName = "Yarisi" };
            var meal = new MealType { Name = "Öğle" };
            var device = new Device
            {
                Name = "Turnstile", DeviceType = "SF300", ConnectionType = "Ethernet",
                Direction = "Entry", ConnectionStatus = "Connected"
            };
            setup.AddRange(student, meal, device);
            setup.Add(new StudentCard
            {
                StudentId = student.Id, CardNumber = "balance-race-card", ValidFrom = DateTimeOffset.UtcNow
            });
            // HAKEDIS YOK: odemeli yola dusulmesi icin ogrencinin yemek hakki bulunmamalidir.
            setup.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = mealPriceCents });
            setup.Add(new StudentBalanceEntry
            {
                StudentId = student.Id, AmountCents = balanceCents, Kind = StudentBalanceEntryKinds.TopUp,
                ReferenceType = StudentBalanceReferenceTypes.IncomeTransaction,
                Note = "Test yüklemesi", OccurredAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(3))
            });
            await setup.SaveChangesAsync();
            return new BalanceTestDatabase(directory, options)
            { StudentId = student.Id, MealTypeId = meal.Id, DeviceId = device.Id };
        }

        private int concurrent;
        private int peakConcurrent;
        public int PeakConcurrent => peakConcurrent;

        public YemekhaneDbContext CreateContext() => new(options);

        public async Task<IReadOnlyList<AccessDecision>> RunAccessRaceAsync(int count, Guid? operationId = null)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, count).Select(async index =>
            {
                await start.Task;
                Interlocked.Increment(ref concurrent);
                var peak = Volatile.Read(ref concurrent);
                if (peak > Volatile.Read(ref peakConcurrent)) Volatile.Write(ref peakConcurrent, peak);
                try {
                await using var context = CreateContext();
                var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
                    new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), new NullRealtimePublisher());
                return await service.CheckAccessAsync(new AccessCheckRequest(CardNumber, DeviceId, MealTypeId,
                    Timestamp.AddTicks(index), OperationId: operationId));
                } finally { Interlocked.Decrement(ref concurrent); }
            }).ToArray();
            start.SetResult();
            return await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60));
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
