using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Cards;
using Yemekhane.Infrastructure.Income;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Otomatik SMS kurallari: ayar dogrulama, sablon, hak uyarisi adaylari (esik, hakki bitmis,
/// telefonsuz, ayni gun tekrar), zamanlayici karari, gelir/kart kancalari ve gecmis kaynak filtresi.
/// Gercek SQLite (bellek ici) + gercek EF depolari; sahte yalnizca saat ve saglayici.
/// </summary>
public sealed class SmsAutomationTests
{
    // 2026-09-02 Carsamba, Istanbul 10:00 (UTC 07:00).
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 2);

    [Fact]
    public void ValidationRejectsThresholdPhoneAndTemplateProblemsInTurkish()
    {
        var ok = SmsAutomationSettings.Default;
        var threshold = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { EntitlementWarning = ok.EntitlementWarning with { DaysThreshold = 31 } }));
        Assert.Contains("1 ile 30", threshold.Message);
        Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { EntitlementWarning = ok.EntitlementWarning with { DaysThreshold = 0 } }));

        var noPhone = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { IncomeNotice = ok.IncomeNotice with { Enabled = true, AdminPhone = " " } }));
        Assert.Contains("GSM", noPhone.Message);
        var shortPhone = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { IncomeNotice = ok.IncomeNotice with { Enabled = true, AdminPhone = "532123" } }));
        Assert.Contains("10-11", shortPhone.Message);
        var landline = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { CardReplacement = ok.CardReplacement with { AdminPhone = "0212 555 44 33" } }));
        Assert.Contains("mobil", landline.Message);

        var empty = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { CardReplacement = ok.CardReplacement with { Template = "  " } }));
        Assert.Contains("boş olamaz", empty.Message);
        var unknown = Assert.Throws<RequestValidationException>(() => SmsAutomationValidation.Validate(ok with
        { IncomeNotice = ok.IncomeNotice with { Template = "{ad} için {kalan_gun} gün" } }));
        Assert.Contains("{kalan_gun}", unknown.Message);
        Assert.Contains("{tutar}", unknown.Message);

        // Gecerli: telefon girildigi gibi saklanir, sablon kirpilir; kural kapaliyken telefon bos olabilir.
        var valid = SmsAutomationValidation.Validate(ok with
        {
            IncomeNotice = ok.IncomeNotice with { Enabled = true, AdminPhone = " 0532 111 22 33 ", Template = "  {tutar} TL  " },
            CardReplacement = ok.CardReplacement with { AdminPhone = "" }
        });
        Assert.Equal("0532 111 22 33", valid.IncomeNotice.AdminPhone);
        Assert.Equal("{tutar} TL", valid.IncomeNotice.Template);
        Assert.Null(valid.CardReplacement.AdminPhone);
        Assert.Same(SmsAutomationSettings.Default.EntitlementWarning.Template, SmsAutomationValidation.Validate(ok).EntitlementWarning.Template);
    }

    [Fact]
    public void NamedRendererFillsVariablesNullsBecomeEmptyAndUnknownStaysVisible()
    {
        Assert.Equal(["ad", "no", "kalan_gun"], SmsTemplateRenderer.NamedPlaceholders("{ad} ({no}) {kalan_gun} {ad} {{ParentName}}"));
        var text = SmsTemplateRenderer.RenderNamed("Sayın {veli}, {ad} {soyad} ({no}) {kalan_gun} gün. {bilinmeyen} {{ParentName}}",
            new Dictionary<string, string?> { ["veli"] = "Ayşe", ["ad"] = "ADA", ["soyad"] = null, ["no"] = "5016", ["kalan_gun"] = "2" });
        Assert.Equal("Sayın Ayşe, ADA (5016) 2 gün. {bilinmeyen} {{ParentName}}", text);
    }

    [Fact]
    public void SourceIsDerivedFromIdempotencyKeyPrefix()
    {
        Assert.Equal(SmsSources.AutoEntitlement, SmsSources.FromKey("oto:hak:20260902:" + Guid.NewGuid()));
        Assert.Equal(SmsSources.AutoIncome, SmsSources.FromKey("oto:gelir:" + Guid.NewGuid()));
        Assert.Equal(SmsSources.AutoCard, SmsSources.FromKey("oto:kart:" + Guid.NewGuid() + ":yetkili"));
        Assert.Equal(SmsSources.Bulk, SmsSources.FromKey(new string('A', 32) + new string('1', 32)));
        Assert.Equal(SmsSources.Manual, SmsSources.FromKey("elle-gonderim-1"));
        Assert.Equal(SmsSources.Manual, SmsSources.FromKey(new string('Z', 64)));
        Assert.Equal(SmsSources.Manual, new SmsLogDetails(Guid.NewGuid(), null, null, "+905321112233", "m", null, "Pending",
            "abc", 0, null, null, null, null, null, Now).Source);
    }

    [Fact]
    public void ScheduledRunIsDueOnlyWhenEnabledAfterSendTimeAndNotYetToday()
    {
        var settings = SmsAutomationSettings.Default with
        { EntitlementWarning = SmsAutomationSettings.Default.EntitlementWarning with { Enabled = true, SendAt = new TimeOnly(13, 10) } };
        var before = new DateTimeOffset(2026, 9, 2, 10, 9, 0, TimeSpan.Zero);   // Istanbul 13:09
        var at = new DateTimeOffset(2026, 9, 2, 10, 10, 0, TimeSpan.Zero);       // Istanbul 13:10
        var later = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);     // Istanbul 21:00 (API gec acildi)
        Assert.False(SmsAutomationService.IsScheduledRunDue(settings, before, null));
        Assert.True(SmsAutomationService.IsScheduledRunDue(settings, at, null));
        Assert.True(SmsAutomationService.IsScheduledRunDue(settings, later, new DateOnly(2026, 9, 1)));
        Assert.False(SmsAutomationService.IsScheduledRunDue(settings, later, new DateOnly(2026, 9, 2)));
        Assert.False(SmsAutomationService.IsScheduledRunDue(settings with
        { EntitlementWarning = settings.EntitlementWarning with { Enabled = false } }, later, null));
        // Gece yarisi sonrasi Istanbul'da yeni gun, UTC'de hala dun: 2026-09-02 22:30 UTC = 03.09 01:30 Istanbul, saat gecmemis.
        Assert.False(SmsAutomationService.IsScheduledRunDue(settings, new DateTimeOffset(2026, 9, 2, 22, 30, 0, TimeSpan.Zero), new DateOnly(2026, 9, 2)));
    }

    [Fact]
    public async Task StoreRoundTripsSettingsAndLastRunDateThroughSystemSettings()
    {
        await using var fx = await Fixture.CreateAsync();
        Assert.Null(await fx.Store.GetAsync(default));
        Assert.Null(await fx.Store.GetLastRunDateAsync(default));
        var settings = SmsAutomationSettings.Default with
        {
            EntitlementWarning = new EntitlementWarningRule(true, new TimeOnly(9, 45), 5, "Kalan {kalan_gun} gün"),
            IncomeNotice = new IncomeNoticeRule(true, "05321112233", "Gelir {tutar}")
        };
        await fx.Store.SaveAsync(settings, default);
        await fx.Store.SetLastRunDateAsync(Today, default);
        Assert.Equal(settings, await fx.Store.GetAsync(default));
        Assert.Equal(Today, await fx.Store.GetLastRunDateAsync(default));
        var keys = await fx.Db.Set<SystemSetting>().Select(x => x.Key).OrderBy(x => x).ToListAsync();
        Assert.Equal(["sms.automation", "sms.automation.lastRunDate"], keys);
        // Ikinci kayit yeni satir acmaz, gunceller.
        await fx.Store.SaveAsync(settings with { CardReplacement = settings.CardReplacement with { Enabled = true } }, default);
        Assert.Equal(2, await fx.Db.Set<SystemSetting>().CountAsync());
        Assert.True((await fx.Store.GetAsync(default))!.CardReplacement.Enabled);
    }

    [Fact]
    public async Task EntitlementWarningQueuesStudentsAtOrBelowThresholdSkipsNoPhoneAndDedupesPerDay()
    {
        await using var fx = await Fixture.CreateAsync();
        var plenty = await fx.StudentAsync("5001", "ADA", "AKGÜN", phone: "05321000001", remainingDays: 13);
        var two = await fx.StudentAsync("5002", "ALİ", "KAYA", phone: "05321000002", remainingDays: 2);
        var expired = await fx.StudentAsync("5003", "ECE", "DEMİR", phone: "05321000003", remainingDays: 0, pastDays: 3);
        // Hic hakedisi olmamis ogrenci: "hakkin bitiyor" demek anlamsiz, uyari GITMEMELI.
        var never = await fx.StudentAsync("5004", "CAN", "YILDIZ", phone: "05321000004", remainingDays: 0, everHadEntitlement: false);
        var noPhone = await fx.StudentAsync("5005", "EDA", "ÖZ", phone: null, remainingDays: 1);
        var inactive = await fx.StudentAsync("5006", "ONUR", "ŞEN", phone: "05321000006", remainingDays: 1, active: false);
        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { EntitlementWarning = SmsAutomationSettings.Default.EntitlementWarning with { Enabled = true, DaysThreshold = 2 } });

        var result = await fx.Service.RunEntitlementWarningAsync();

        Assert.Equal(Today, result.Date);
        Assert.Equal(3, result.Candidates);          // two, expired, noPhone
        Assert.Equal(2, result.Queued);
        Assert.Equal(1, result.SkippedNoPhone);
        Assert.Equal(0, result.SkippedAlreadySent);
        var logs = await fx.Db.SmsLogs.AsNoTracking().OrderBy(x => x.Phone).ToListAsync();
        Assert.Equal([two, expired], logs.Select(x => x.StudentId).OrderBy(id => id == expired).ToArray());
        Assert.DoesNotContain(logs, x => x.StudentId == plenty || x.StudentId == never || x.StudentId == noPhone || x.StudentId == inactive);
        Assert.All(logs, x => Assert.StartsWith("oto:hak:20260902:", x.IdempotencyKey));
        Assert.All(logs, x => Assert.Equal(SmsLogStatuses.Pending, x.Status));
        var aliMessage = logs.Single(x => x.StudentId == two).Message;
        Assert.Contains("ALİ KAYA (5002)", aliMessage);
        Assert.Contains("2 gün", aliMessage);
        Assert.Contains("03.09.2026", aliMessage);      // son hak tarihi
        Assert.Contains("Sayın KAYA VELİSİ", aliMessage);
        Assert.Equal("+905321000002", logs.Single(x => x.StudentId == two).Phone);
        Assert.Contains("0 gün", logs.Single(x => x.StudentId == expired).Message);

        // Ayni gun ikinci kosu: hicbir sey kuyruklanmaz.
        var again = await fx.Service.RunEntitlementWarningAsync();
        Assert.Equal(0, again.Queued);
        Assert.Equal(2, again.SkippedAlreadySent);
        Assert.Equal(2, await fx.Db.SmsLogs.CountAsync());

        // Ertesi gun: yeniden gider (kalan gun 1'e duser).
        fx.Clock.Advance(TimeSpan.FromDays(1));
        var tomorrow = await fx.Service.RunEntitlementWarningAsync();
        Assert.Equal(2, tomorrow.Queued);
        Assert.Contains("1 gün", (await fx.Db.SmsLogs.AsNoTracking().Where(x => x.StudentId == two && x.IdempotencyKey.StartsWith("oto:hak:20260903:")).SingleAsync()).Message);
    }

    [Fact]
    public async Task ScheduledRunRecordsLastRunDateAndRunsOncePerDay()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.StudentAsync("5002", "ALİ", "KAYA", phone: "05321000002", remainingDays: 1);
        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { EntitlementWarning = SmsAutomationSettings.Default.EntitlementWarning with { Enabled = true, SendAt = new TimeOnly(13, 10) } });

        Assert.False(await fx.Service.RunScheduledAsync());   // 10:00 Istanbul, saat gelmedi
        Assert.Equal(0, await fx.Db.SmsLogs.CountAsync());
        fx.Clock.Advance(TimeSpan.FromHours(3.5));            // 13:30
        Assert.True(await fx.Service.RunScheduledAsync());
        Assert.Equal(1, await fx.Db.SmsLogs.CountAsync());
        Assert.Equal(Today, await fx.Store.GetLastRunDateAsync(default));
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await fx.Service.RunScheduledAsync());   // bugun kosuldu
        Assert.Equal(1, await fx.Db.SmsLogs.CountAsync());
    }

    [Fact]
    public async Task IncomeRecordingQueuesAdminNoticeOnlyWhenEnabled()
    {
        await using var fx = await Fixture.CreateAsync();
        var student = await fx.StudentAsync("5016", "ADA", "AKGÜN", phone: "05321000016", remainingDays: 5);
        var income = new IncomeService(new EfIncomeRepository(fx.Db, fx.Clock), fx.Service);
        var first = await income.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student, null,
            new DateTimeOffset(2026, 9, 2, 8, 15, 0, TimeSpan.Zero), fx.IncomeTypeId, 150.50m, "Eylül"), Guid.NewGuid());
        Assert.Equal(0, await fx.Db.SmsLogs.CountAsync());   // kural kapali

        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { IncomeNotice = new IncomeNoticeRule(true, "0532 999 88 77", SmsAutomationSettings.Default.IncomeNotice.Template) });
        var second = await income.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student, null,
            new DateTimeOffset(2026, 9, 2, 8, 15, 0, TimeSpan.Zero), fx.IncomeTypeId, 1250.75m, "Ekim taksiti"), Guid.NewGuid());

        var log = await fx.Db.SmsLogs.AsNoTracking().SingleAsync();
        Assert.Equal("oto:gelir:" + second.Id.ToString("D"), log.IdempotencyKey);
        Assert.Equal("+905329998877", log.Phone);
        Assert.Equal(student, log.StudentId);
        Assert.Contains("02.09.2026 11:15", log.Message);   // Istanbul saati (UTC 08:15 + 3)
        Assert.Contains("ADA AKGÜN (5016)", log.Message);
        Assert.Contains("1.250,75 TL", log.Message);
        Assert.Contains("Yemek Ücreti", log.Message);
        Assert.EndsWith("Ekim taksiti", log.Message);
        Assert.Equal(SmsSources.AutoIncome, SmsSources.FromKey(log.IdempotencyKey));
        Assert.NotEqual(first.Id, second.Id);

        // Ogrencisiz gelir: SMS yine gider, ad yerine acik ifade.
        var third = await income.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), null, "8350099",
            Now, fx.IncomeTypeId, 10m), Guid.NewGuid());
        var anonymous = await fx.Db.SmsLogs.AsNoTracking().SingleAsync(x => x.IdempotencyKey == "oto:gelir:" + third.Id.ToString("D"));
        Assert.Contains("Öğrenci belirtilmedi (8350099)", anonymous.Message);
    }

    [Fact]
    public async Task IncomeIsStillRecordedWhenSmsQueueFails()
    {
        await using var fx = await Fixture.CreateAsync(smsLogs: new ThrowingSmsLogs());
        var student = await fx.StudentAsync("5016", "ADA", "AKGÜN", phone: "05321000016", remainingDays: 5);
        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { IncomeNotice = new IncomeNoticeRule(true, "05329998877", "x {tutar}") });
        var income = new IncomeService(new EfIncomeRepository(fx.Db, fx.Clock), fx.Service);

        var created = await income.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student, null, Now,
            fx.IncomeTypeId, 99m), Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(1, await fx.Db.Set<IncomeTransaction>().CountAsync());
        Assert.Equal(0, await fx.Db.SmsLogs.CountAsync());
    }

    [Fact]
    public async Task CardAssignAndReplaceQueueParentSmsAndOptionalAdminCopy()
    {
        await using var fx = await Fixture.CreateAsync();
        var student = await fx.StudentAsync("5016", "ADA", "AKGÜN", phone: "05321000016", remainingDays: 5);
        var cards = new CardService(new EfCardRepository(fx.Db), fx.Clock, fx.Service);
        await cards.AssignAsync(student, new AssignCardRequest("8350016"));
        Assert.Equal(0, await fx.Db.SmsLogs.CountAsync());   // kural kapali

        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { CardReplacement = new CardReplacementRule(true, "{ad} {soyad} ({no}) yeni kart {kart_no}, eski {eski_kart_no}", null) });
        var replaced = await cards.ReplaceAsync(student, new ReplaceCardRequest("8350999", "Kayıp"));
        Assert.True(await fx.Db.SmsLogs.AnyAsync(), "kart SMS'i kuyruklanmadi: " + fx.LastTriggerError);
        var log = await fx.Db.SmsLogs.AsNoTracking().SingleAsync();
        Assert.Equal("oto:kart:" + replaced.Id.ToString("D"), log.IdempotencyKey);
        Assert.Equal("+905321000016", log.Phone);
        Assert.Equal("ADA AKGÜN (5016) yeni kart 8350999, eski 8350016", log.Message);

        // Yetkili kopyasi: ikinci kayit, ayri anahtar.
        await fx.Service.SaveAsync(SmsAutomationSettings.Default with
        { CardReplacement = new CardReplacementRule(true, "Kart {kart_no} eski {eski_kart_no}", "05320000000") });
        var third = await cards.ReplaceAsync(student, new ReplaceCardRequest("8351000", "Arızalı"));
        var logs = await fx.Db.SmsLogs.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith("oto:kart:" + third.Id.ToString("D")))
            .OrderBy(x => x.IdempotencyKey).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(["+905321000016", "+905320000000"], logs.Select(x => x.Phone).ToArray());
        Assert.All(logs, x => Assert.Equal("Kart 8351000 eski 8350999", x.Message));
        Assert.EndsWith(":yetkili", logs[1].IdempotencyKey);

        // Velisi olmayan ogrenci: veliye gitmez, yetkiliye gider; islem yine basarili.
        var orphan = await fx.StudentAsync("5099", "VELİSİZ", "ÖĞRENCİ", phone: null, remainingDays: 1);
        var assigned = await cards.AssignAsync(orphan, new AssignCardRequest("8352000"));
        var orphanLogs = await fx.Db.SmsLogs.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith("oto:kart:" + assigned.Id.ToString("D"))).ToListAsync();
        Assert.Single(orphanLogs);
        Assert.Equal("+905320000000", orphanLogs[0].Phone);
        Assert.Equal("Kart 8352000 eski -", orphanLogs[0].Message);
    }

    [Fact]
    public async Task HistoryFilterBySourceSeparatesAutomaticBulkAndManualRows()
    {
        await using var fx = await Fixture.CreateAsync();
        var repo = new EfSmsLogRepository(fx.Db, fx.Clock);
        await repo.EnqueueAsync("+905321000001", "elle", "elle-1", null, null, default);
        await repo.EnqueueAsync("+905321000002", "toplu", new string('A', 40) + new string('9', 24), null, null, default);
        await repo.EnqueueAsync("+905321000003", "hak", "oto:hak:20260902:" + Guid.NewGuid().ToString("D"), null, null, default);
        await repo.EnqueueAsync("+905321000004", "gelir", "oto:gelir:" + Guid.NewGuid().ToString("D"), null, null, default);
        await repo.EnqueueAsync("+905321000005", "kart", "oto:kart:" + Guid.NewGuid().ToString("D") + ":yetkili", null, null, default);
        var service = new SmsService(repo, new EfSmsTemplateRepository(fx.Db));

        async Task<string[]> Messages(string? source) =>
            (await service.ListAsync(new SmsHistoryFilter(Source: source))).Items.Select(x => x.Message).OrderBy(x => x).ToArray();

        Assert.Equal(5, (await Messages(null)).Length);
        Assert.Equal(["elle"], await Messages(SmsSources.Manual));
        Assert.Equal(["toplu"], await Messages(SmsSources.Bulk));
        Assert.Equal(["hak"], await Messages(SmsSources.AutoEntitlement));
        Assert.Equal(["gelir"], await Messages(SmsSources.AutoIncome));
        Assert.Equal(["kart"], await Messages(SmsSources.AutoCard));
        Assert.Equal(SmsSources.AutoCard, (await service.ListAsync(new SmsHistoryFilter(Source: SmsSources.AutoCard))).Items.Single().Source);
        var invalid = await Assert.ThrowsAsync<RequestValidationException>(() => service.ListAsync(new SmsHistoryFilter(Source: "Robot")));
        Assert.Contains("kaynak", invalid.Message);
    }

    private sealed class ThrowingSmsLogs : ISmsLogRepository
    {
        public Task<SmsLogDetails> EnqueueAsync(string phone, string message, string idempotencyKey, Guid? studentId, Guid? templateId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("kuyruk kapalı");
        public Task<PagedResult<SmsLogDetails>> ListAsync(SmsHistoryFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// Kanca hatayi YUTAR (ana islem dusmemeli). Test bunu gormezse "SMS gelmedi" hatasi
    /// sebepsiz kalir; bu logger yutulan istisnayi yakalar ve assert mesajina tasir.
    /// </summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<SmsAutomationService>
    {
        public Exception? Last { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Last = exception;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public YemekhaneDbContext Db { get; }
        public MutableClock Clock { get; } = new(Now);
        public EfSmsAutomationStore Store { get; }
        public SmsAutomationService Service { get; }
        public Guid IncomeTypeId { get; }
        private readonly Guid mealTypeId;
        private readonly CapturingLogger triggerLog = new();
        /// <summary>Kancanin yuttugu son istisna; "SMS gelmedi" hatasinin nedenini gorunur kilar.</summary>
        public string LastTriggerError => triggerLog.Last?.ToString() ?? "(yutulmuş istisna yok)";

        private Fixture(SqliteConnection connection, YemekhaneDbContext db, ISmsLogRepository? smsLogs, Guid incomeTypeId, Guid mealTypeId)
        {
            this.connection = connection; Db = db; IncomeTypeId = incomeTypeId; this.mealTypeId = mealTypeId;
            Store = new EfSmsAutomationStore(db, Clock);
            Service = new SmsAutomationService(Store, new EfSmsAutomationRepository(db), smsLogs ?? new EfSmsLogRepository(db, Clock), Clock, triggerLog);
        }

        public static async Task<Fixture> CreateAsync(ISmsLogRepository? smsLogs = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var incomeType = new IncomeType { Name = "Yemek Ücreti" };
            var meal = new MealType { Name = "Öğle Yemeği" };
            db.AddRange(incomeType, meal);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, smsLogs, incomeType.Id, meal.Id);
        }

        /// <summary>
        /// Ogrenci + (istege bagli) veli + bugunden itibaren <paramref name="remainingDays"/> aktif hak,
        /// <paramref name="pastDays"/> gecmis hak. <paramref name="everHadEntitlement"/> false ise
        /// ogrenciye HIC hakedis yazilmaz: "hakki bitti" yalnizca hakki OLMUS biri icin anlamlidir.
        /// </summary>
        public async Task<Guid> StudentAsync(string no, string first, string last, string? phone, int remainingDays,
            int pastDays = 0, bool active = true, bool everHadEntitlement = true)
        {
            var student = new Student { StudentNo = no, FirstName = first, LastName = last, IsActive = active };
            Db.Add(student);
            if (phone is not null)
                Db.Add(new Parent { StudentId = student.Id, Name = last + " VELİSİ", NormalizedPhone = phone, IsPrimary = true });
            for (var i = 0; i < remainingDays; i++)
                Db.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = mealTypeId, EntitlementDate = Today.AddDays(i), Quantity = 1, Status = "Active", Source = "Manual" });
            for (var i = 1; i <= pastDays; i++)
                Db.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = mealTypeId, EntitlementDate = Today.AddDays(-i), Quantity = 1, ConsumedQuantity = 1, Status = "Active", Source = "Manual" });
            // Iptal edilmis gelecek hak KALAN sayilmaz (ama "hakki olmus" saydirir).
            if (everHadEntitlement)
                Db.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = mealTypeId, EntitlementDate = Today.AddDays(40), Quantity = 1, Status = "Cancelled", Source = "Manual" });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return student.Id;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
