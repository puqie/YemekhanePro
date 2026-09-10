using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.DailyTracking;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.DailyTracking;

/// <summary>
/// "Ogrencinin GECMIS gunlerde yemekhaneye girip girmedigini gormek" -- gecen yillara kadar.
///
/// Onceden gecis gecmisi YALNIZCA BUGUNU getiriyordu: servis araligi her istekte bugune
/// sabitliyordu, tarih suzgeci diye bir sey yoktu. "Gecen yil 12 Eylul'de yemek yedi mi"
/// sorusu cevaplanamiyordu.
///
/// SQLite tuzagi: DateTimeOffset metin olarak saklanir; karsilastirma JulianDay uzerinden
/// yapilmazsa ya cevrilemez ya da sessizce YANLIS sonuc doner.
/// </summary>
public sealed class AccessHistoryRangeTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(3)));
    private Guid studentId;

    public AccessHistoryRangeTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var student = new Student { StudentNo = "5001", FirstName = "Ceylin", LastName = "Yılmaz", SearchName = "ceylin yilmaz" };
        var device = new Device
        {
            Name = "Turnike", DeviceType = "SF300", ConnectionType = "Ethernet",
            Direction = "Entry", ConnectionStatus = "Connected"
        };
        db.AddRange(student, device);
        db.SaveChanges();
        studentId = student.Id;
        deviceId = device.Id;
    }

    private Guid deviceId;

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private DailyTrackingService Service() => new(new EfDailyTrackingRepository(db), clock);

    /// <summary>Okul saatiyle o gunun ogle vaktine bir gecis yazar.</summary>
    private void Entry(DateOnly date, string decision = "ALLOW")
    {
        db.Add(new AccessLog
        {
            OperationId = Guid.NewGuid(),
            StudentId = studentId,
            DeviceId = deviceId,
            CardNumber = "8350001",
            Timestamp = new DateTimeOffset(date.Year, date.Month, date.Day, 12, 15, 0, TimeSpan.FromHours(3)),
            Decision = decision,
            Reason = decision == "ALLOW" ? "Geçiş onaylandı" : "Hak yok",
            Direction = "Entry",
            ReaderSource = "Test"
        });
        db.SaveChanges();
    }

    private Task<DailyTrackingPage> QueryAsync(DateOnly? from = null, DateOnly? to = null) =>
        Service().GetAsync(new DailyTrackingQuery(StudentId: studentId, FromDate: from, ToDate: to));

    /// <summary>Tarih verilmezse eski davranis: yalnizca BUGUN.</summary>
    [Fact]
    public async Task WithoutADateRangeOnlyTodayIsReturned()
    {
        Entry(new DateOnly(2026, 9, 20));   // bugun
        Entry(new DateOnly(2026, 9, 19));   // dun

        var page = await QueryAsync();

        Assert.Single(page.Items);
    }

    /// <summary>GECEN YILA bakilabilir; kok sikayet buydu.</summary>
    [Fact]
    public async Task LastYearsEntriesAreFound()
    {
        Entry(new DateOnly(2025, 9, 12));

        var page = await QueryAsync(new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 30));

        Assert.Single(page.Items);
        Assert.Equal(new DateOnly(2025, 9, 12), DateOnly.FromDateTime(page.Items[0].Timestamp.ToOffset(TimeSpan.FromHours(3)).DateTime));
    }

    /// <summary>Aralik disindaki gunler GELMEZ.</summary>
    [Fact]
    public async Task EntriesOutsideTheRangeAreExcluded()
    {
        Entry(new DateOnly(2025, 9, 12));
        Entry(new DateOnly(2026, 9, 12));

        var page = await QueryAsync(new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 30));

        Assert.Single(page.Items);
    }

    /// <summary>BITIS GUNU DAHILDIR: "20 Eylul'e kadar" derken o gun de kastedilir.</summary>
    [Fact]
    public async Task TheEndDateIsInclusive()
    {
        Entry(new DateOnly(2026, 9, 20));

        var page = await QueryAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 20));

        Assert.Single(page.Items);
    }

    /// <summary>BASLANGIC GUNU de dahildir.</summary>
    [Fact]
    public async Task TheStartDateIsInclusive()
    {
        Entry(new DateOnly(2026, 9, 1));

        var page = await QueryAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 20));

        Assert.Single(page.Items);
    }

    /// <summary>Tek gun sorgusu: "12 Eylul'de yedi mi" dogrudan cevaplanir.</summary>
    [Fact]
    public async Task ASingleDayCanBeQueried()
    {
        Entry(new DateOnly(2025, 9, 12));
        Entry(new DateOnly(2025, 9, 13));

        var page = await QueryAsync(new DateOnly(2025, 9, 12), new DateOnly(2025, 9, 12));

        Assert.Single(page.Items);
    }

    /// <summary>Yalnizca baslangic verilirse O GUN gosterilir (bitis baslangica esitlenir).</summary>
    [Fact]
    public async Task GivingOnlyTheStartDateShowsThatDay()
    {
        Entry(new DateOnly(2025, 9, 12));
        Entry(new DateOnly(2025, 9, 13));

        var page = await QueryAsync(new DateOnly(2025, 9, 12));

        Assert.Single(page.Items);
    }

    /// <summary>Cok yillik aralik: tum ogretim yillari birden gorulebilir.</summary>
    [Fact]
    public async Task AMultiYearRangeReturnsEveryYear()
    {
        Entry(new DateOnly(2024, 10, 5));
        Entry(new DateOnly(2025, 10, 5));
        Entry(new DateOnly(2026, 9, 5));

        var page = await QueryAsync(new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(3, page.Items.Count);
    }

    /// <summary>Ters aralik REDDEDILIR; sessizce bos liste donmek kullaniciyi yaniltirdi.</summary>
    [Fact]
    public async Task AnInvertedRangeIsRejected()
    {
        var error = await Assert.ThrowsAsync<Yemekhane.Application.Common.RequestValidationException>(
            () => QueryAsync(new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 1)));

        Assert.Contains("Bitiş tarihi", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Reddedilen gecisler de gorunur: "geldi ama hakki yoktu" bilgisi onemlidir.</summary>
    [Fact]
    public async Task DeniedEntriesAreAlsoReturned()
    {
        Entry(new DateOnly(2025, 9, 12), decision: "DENY");

        var page = await QueryAsync(new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 30));

        Assert.Equal("DENY", Assert.Single(page.Items).Decision);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
