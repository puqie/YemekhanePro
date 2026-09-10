using System.Security.Cryptography;
using System.Text;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Entitlements;

public sealed class MealEntitlementService(
    IMealEntitlementRepository repository,
    BusinessDayService businessDayService,
    IEntitlementBillingService? billing = null)
{
    public async Task<BulkEntitlementResult> GrantBulkAsync(BulkEntitlementRequest request, CancellationToken cancellationToken = default)
    {
        var students = request.StudentIds.Distinct().ToArray();
        var dates = await ValidateAndGetDatesAsync(request.StartsOn, request.EndsOn, request.Quantity,
            request.IncludeSaturday, request.IncludeSunday, cancellationToken);
        if (students.Length == 0) throw new RequestValidationException("En az bir öğrenci seçilmelidir.");
        return await repository.UpsertBulkAsync(students, request.MealTypeId, dates, request.Quantity,
            RequiredSource(request.Source), null, cancellationToken);
    }

    public async Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default)
    {
        var students = await repository.ResolveTargetAsync(request.Target, cancellationToken);
        if (students.Count == 0) throw new RequestValidationException("Hedefte aktif öğrenci bulunamadı.");
        var dates = await ValidateAndGetDatesAsync(request.StartsOn, request.EndsOn, request.Quantity,
            request.IncludeSaturday, request.IncludeSunday, cancellationToken, request.DayCount);
        var state = await repository.PreviewAsync(students, request.MealTypeId, dates, cancellationToken);
        // Bedel SUNUCUDA hesaplanir: ekran kendi carpimini yapiyordu ve o rakam hicbir yere
        // gitmiyordu. Ayni deger hem onizlemede gosterilir hem kasaya yazilir.
        // Fiyat TANIMSIZSA onizlemede 0 gosterilir; engel ancak "Kasaya isle" secili
        // uygulama adiminda cikar (asagida). Onizleme kullaniciya durumu gostersin diye
        // reddedilmez.
        var unit = await repository.MealPriceAsync(request.MealTypeId, cancellationToken) ?? 0m;
        var perStudent = unit * dates.Count * request.Quantity;
        return new EntitlementPreview(students.Count, dates.Count, checked(students.Count * dates.Count),
            state.CreatedCount, state.UpdatedCount, Token(request, students, dates, state.StateHash),
            perStudent, perStudent * students.Count);
    }

    /// <param name="actorId">Tahsilati kimin yazdigi; kasa denetimi bunu ister.</param>
    public async Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, Guid actorId = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewToken)) throw new RequestValidationException("Önizleme anahtarı zorunludur.");
        var students = await repository.ResolveTargetAsync(request.Grant.Target, cancellationToken);
        if (students.Count == 0) throw new RequestValidationException("Hedefte aktif öğrenci bulunamadı.");
        var dates = await ValidateAndGetDatesAsync(request.Grant.StartsOn, request.Grant.EndsOn, request.Grant.Quantity,
            request.Grant.IncludeSaturday, request.Grant.IncludeSunday, cancellationToken, request.Grant.DayCount);
        var state = await repository.PreviewAsync(students, request.Grant.MealTypeId, dates, cancellationToken);
        var expected = Token(request.Grant, students, dates, state.StateHash);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.PreviewToken)))
            throw new EntityConflictException("Önizlemeden sonra hedef veya hakediş verisi değişti. Yeniden önizleyin.");
        var result = await repository.UpsertBulkAsync(students, request.Grant.MealTypeId, dates, request.Grant.Quantity,
            RequiredSource(request.Grant.Source), state.StateHash, cancellationToken);

        // Ucretlendirme ve SMS hakedis YAZILDIKTAN SONRA calisir: buradaki bir hata
        // tanimlanmis hakki geri almamalidir (IncomeService'teki ayni kural).
        if (billing is null || (!request.Grant.ChargeToCash && !request.Grant.NotifyParents)) return result;
        var price = await repository.MealPriceAsync(request.Grant.MealTypeId, cancellationToken);
        // Ucreti TANIMSIZ ogun kasaya islenemez: sessizce 0 TL yazmak 250.000 TL'lik bir
        // tahsilati hicbir uyari vermeden kaybediyordu. Ucretsiz dagitim icin ogune
        // ACIKCA 0 ₺ tanimlanir ya da "Kasaya isle" kapatilir.
        if (price is null && request.Grant.ChargeToCash)
            throw new RequestValidationException(
                "Bu öğünün ücreti tanımlı değil; kasaya işlenemez. Tanımlar ekranından öğün ücretini girin (ücretsiz ise 0 yazın) ya da \"Kasaya işle\" seçeneğini kapatın.");
        var unit = price ?? 0m;
        var perStudent = unit * dates.Count * request.Grant.Quantity;
        if (perStudent <= 0) return result;
        // Ucret YENI yaratilan gunler uzerinden hesaplanir, aralik uzunlugu uzerinden
        // DEGIL: ayni hakedis ikinci kez verildiginde hakedis satiri guncellenir ve
        // ogrencinin yeni borcu yoktur. Once burada dates.Count kullaniliyordu ve tekrar
        // uygulanan her hakedis kasaya ikinci kez tam tutar yaziyordu.
        var amountOverrides = result.CreatedPerStudent?
            .ToDictionary(pair => pair.Key, pair => unit * pair.Value * request.Grant.Quantity);
        var chargedStudents = amountOverrides is null
            ? students
            : students.Where(x => amountOverrides.GetValueOrDefault(x) > 0).ToArray();
        if (chargedStudents.Count == 0 && !request.Grant.NotifyParents) return result;
        // Kasaya yazma kapaliysa tutar 0 gonderilir: servis para yazmaz, yalnizca SMS kuyruklar.
        var charge = await billing.ChargeAsync(new EntitlementChargeRequest(
            request.Grant.OperationId ?? Guid.NewGuid(),
            request.Grant.ChargeToCash ? chargedStudents : students, request.Grant.MealTypeId,
            request.Grant.ChargeToCash ? perStudent : 0m,
            // Gercek aralik SUNUCUNUN hesapladigi gun listesinden alinir; istekteki
            // EndsOn gun sayisi kullanildiginda yer tutucudur (baslangicla ayni) ve
            // kismi iade tarih araligini bu alanlardan okudugu icin yanlis olurdu.
            dates[0], dates[^1], dates.Count, request.Grant.NotifyParents,
            request.Grant.ChargeToCash ? amountOverrides : null),
            actorId, cancellationToken);
        return result with
        {
            ChargedStudents = charge.ChargedStudents,
            ChargedTotal = charge.Total,
            NotifiedParents = charge.NotifiedParents
        };
    }

    public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 250) throw new RequestValidationException("Sayfalama değerleri geçersiz.");
        if (query.StartsOn.HasValue && query.EndsOn < query.StartsOn) throw new RequestValidationException("Tarih aralığı geçersiz.");
        return repository.SearchAsync(query, cancellationToken);
    }

    /// <summary>
    /// Haklari iptal eder ve varsa tahsilatlarini geri alir: hakedis iptal edilip para
    /// kasada kalirsa kasa ile hak birbirini tutmaz.
    /// </summary>
    public async Task<CancelEntitlementsResult> CancelBulkWithRefundAsync(CancelEntitlementsRequest request,
        Guid actorId = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.EntitlementIds.Distinct().ToArray();
        if (ids.Length == 0 || request.ExpectedAffectedCount != ids.Length)
            throw new RequestValidationException("İptal edilecek kayıt sayısı onayla eşleşmiyor.");
        // Iade iptal ile AYNI transaction icinde yazilir: once ikisi ayriydi ve iade
        // adimi cokerse hak iptal edilmis ama para kasada kalmis oluyordu. Daha kotusu,
        // ikinci denemede haklar "Cancelled" oldugu icin iade BIR DAHA
        // TETIKLENEMIYORDU. Simdi iade hata atarsa iptal de geri alinir.
        return await repository.CancelBulkAsync(ids, request.ExpectedAffectedCount,
            billing is null ? null : token => billing.RefundAsync(ids, actorId, token),
            cancellationToken);
    }

    public Task<CancelEntitlementsResult> CancelBulkAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default)
    {
        var ids = request.EntitlementIds.Distinct().ToArray();
        if (ids.Length == 0 || request.ExpectedAffectedCount != ids.Length)
            throw new RequestValidationException("İptal edilecek kayıt sayısı onayla eşleşmiyor.");
        return repository.CancelBulkAsync(ids, request.ExpectedAffectedCount, cancellationToken);
    }

    public Task<IReadOnlyList<EntitlementDetails>> ListAsync(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken = default) =>
        repository.ListAsync(studentId, startsOn, endsOn, cancellationToken);
    public Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken = default) => repository.TryConsumeAsync(entitlementId, cancellationToken);
    public Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken = default) => repository.CancelAsync(entitlementId, cancellationToken);

    private async Task<IReadOnlyList<DateOnly>> ValidateAndGetDatesAsync(DateOnly startsOn, DateOnly endsOn, int quantity,
        bool includeSaturday, bool includeSunday, CancellationToken cancellationToken, int? dayCount = null)
    {
        if (quantity is < 1 or > 10) throw new RequestValidationException("Günlük öğün hakkı 1-10 arasında olmalıdır.");
        if (dayCount.HasValue) return await CollectDaysAsync(startsOn, dayCount.Value, includeSaturday, includeSunday, cancellationToken);

        if (endsOn < startsOn) throw new RequestValidationException("Bitiş tarihi başlangıç tarihinden önce olamaz.");
        if ((endsOn.DayNumber - startsOn.DayNumber) >= 366) throw new RequestValidationException("Tek işlemde en fazla 366 günlük aralık seçilebilir.");
        var dates = new List<DateOnly>();
        foreach (var date in Enumerable.Range(0, endsOn.DayNumber - startsOn.DayNumber + 1).Select(startsOn.AddDays))
            if (await IsGrantableAsync(date, includeSaturday, includeSunday, cancellationToken)) dates.Add(date);
        if (dates.Count == 0) throw new RequestValidationException("Seçilen aralıkta uygulanabilir gün bulunamadı.");
        return dates;
    }

    /// <summary>
    /// Istenen sayida yemek gunu bulunana kadar ILERLER: hafta sonu ve tatil gunleri
    /// atlanir, aralik gerektigi kadar uzar. "20 gun" her zaman 20 hak demektir --
    /// tatile denk gelen istekte hak sayisi dusmez.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly>> CollectDaysAsync(DateOnly startsOn, int dayCount,
        bool includeSaturday, bool includeSunday, CancellationToken cancellationToken)
    {
        if (dayCount < 1) throw new RequestValidationException("Gün sayısı en az 1 olmalıdır.");
        if (dayCount > 366) throw new RequestValidationException("Tek işlemde en fazla 366 gün tanımlanabilir.");
        var dates = new List<DateOnly>(dayCount);
        // Guvenlik siniri: hicbir gun uygun degilse (tum hafta kapali) dongu sonsuza
        // gitmesin. Bes yillik pencere her gercekci istegi karsilar.
        var cursor = startsOn;
        for (var scanned = 0; scanned < 365 * 5 && dates.Count < dayCount; scanned++, cursor = cursor.AddDays(1))
            if (await IsGrantableAsync(cursor, includeSaturday, includeSunday, cancellationToken)) dates.Add(cursor);
        if (dates.Count < dayCount)
            throw new RequestValidationException($"Seçilen günlerle {dayCount} günlük hak oluşturulamıyor. Cumartesi/Pazar seçimini ve tatil takvimini gözden geçirin.");
        return dates;
    }

    /// <summary>Bu gune hak tanimlanabilir mi: hafta sonu secimi ve tatil takvimi birlikte.</summary>
    private async Task<bool> IsGrantableAsync(DateOnly date, bool includeSaturday, bool includeSunday,
        CancellationToken cancellationToken)
    {
        if (date.DayOfWeek == DayOfWeek.Saturday) return includeSaturday;
        if (date.DayOfWeek == DayOfWeek.Sunday) return includeSunday;
        return await businessDayService.IsBusinessDayAsync(date, new CalendarScope("AllSchool"), cancellationToken);
    }

    /// <summary>
    /// "Bu cocugun kac ogun hakki kaldi, yuklemesi ne zaman bitiyor?" -- veli telefondayken
    /// bakilacak ozet. Ogun bazinda tek satir dondurur.
    /// </summary>
    public Task<IReadOnlyList<EntitlementPeriodSummary>> PeriodsAsync(Guid studentId,
        CancellationToken cancellationToken = default) =>
        repository.PeriodsAsync(studentId, SchoolToday(), cancellationToken);

    /// <summary>
    /// Hakedisi bitmek uzere olan ogrenciler; veli aramadan once yenilemeyi gorebilmek icin.
    /// Suresi coktan gecmis olanlar da listeye girer.
    /// </summary>
    public Task<IReadOnlyList<EntitlementPeriodSummary>> ExpiringAsync(ExpiringEntitlementQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.WithinDays is < 0 or > 365)
            throw new RequestValidationException("Gün eşiği 0-365 arasında olmalıdır.");
        return repository.ExpiringAsync(query, SchoolToday(), cancellationToken);
    }

    /// <summary>Okul saatiyle bugun: hak sayimi sunucunun saat diliminden bagimsiz olmalidir.</summary>
    private static DateOnly SchoolToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Istanbul).DateTime);

    private static readonly TimeZoneInfo Istanbul = FindIstanbulTimeZone();

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }

    private static string RequiredSource(string source) => string.IsNullOrWhiteSpace(source) ? "Manual" : source.Trim();
    private static string Token(EntitlementGrantRequest request, IReadOnlyCollection<Guid> students,
        IReadOnlyCollection<DateOnly> dates, string stateHash)
    {
        var value = string.Join('|', request.MealTypeId, request.Quantity, RequiredSource(request.Source),
            string.Join(',', students.Order()), string.Join(',', dates.Order()), stateHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
