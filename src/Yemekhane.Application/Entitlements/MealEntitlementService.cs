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
            request.IncludeSaturday, request.IncludeSunday, cancellationToken);
        var state = await repository.PreviewAsync(students, request.MealTypeId, dates, cancellationToken);
        // Bedel SUNUCUDA hesaplanir: ekran kendi carpimini yapiyordu ve o rakam hicbir yere
        // gitmiyordu. Ayni deger hem onizlemede gosterilir hem kasaya yazilir.
        var unit = await repository.MealPriceAsync(request.MealTypeId, cancellationToken);
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
            request.Grant.IncludeSaturday, request.Grant.IncludeSunday, cancellationToken);
        var state = await repository.PreviewAsync(students, request.Grant.MealTypeId, dates, cancellationToken);
        var expected = Token(request.Grant, students, dates, state.StateHash);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.PreviewToken)))
            throw new EntityConflictException("Önizlemeden sonra hedef veya hakediş verisi değişti. Yeniden önizleyin.");
        var result = await repository.UpsertBulkAsync(students, request.Grant.MealTypeId, dates, request.Grant.Quantity,
            RequiredSource(request.Grant.Source), state.StateHash, cancellationToken);

        // Ucretlendirme ve SMS hakedis YAZILDIKTAN SONRA calisir: buradaki bir hata
        // tanimlanmis hakki geri almamalidir (IncomeService'teki ayni kural).
        if (billing is null || (!request.Grant.ChargeToCash && !request.Grant.NotifyParents)) return result;
        var unit = await repository.MealPriceAsync(request.Grant.MealTypeId, cancellationToken);
        var perStudent = unit * dates.Count * request.Grant.Quantity;
        if (perStudent <= 0) return result;
        // Kasaya yazma kapaliysa tutar 0 gonderilir: servis para yazmaz, yalnizca SMS kuyruklar.
        var charge = await billing.ChargeAsync(new EntitlementChargeRequest(
            request.Grant.OperationId ?? Guid.NewGuid(), students, request.Grant.MealTypeId,
            request.Grant.ChargeToCash ? perStudent : 0m,
            request.Grant.StartsOn, request.Grant.EndsOn, dates.Count, request.Grant.NotifyParents),
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
        // Iade, iptalden ONCE okunur: iptal sonrasi satirlarin tarihi ve ogrencisi hala
        // durur ama "Active" olmadiklari icin eslestirme zorlasir.
        var result = await CancelBulkAsync(request, cancellationToken);
        if (billing is not null && result.CancelledCount > 0)
            await billing.RefundAsync(ids, actorId, cancellationToken);
        return result;
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
        bool includeSaturday, bool includeSunday, CancellationToken cancellationToken)
    {
        if (endsOn < startsOn) throw new RequestValidationException("Bitiş tarihi başlangıç tarihinden önce olamaz.");
        if (quantity is < 1 or > 10) throw new RequestValidationException("Günlük öğün hakkı 1-10 arasında olmalıdır.");
        if ((endsOn.DayNumber - startsOn.DayNumber) >= 366) throw new RequestValidationException("Tek işlemde en fazla 366 günlük aralık seçilebilir.");
        var dates = new List<DateOnly>();
        foreach (var date in Enumerable.Range(0, endsOn.DayNumber - startsOn.DayNumber + 1).Select(startsOn.AddDays))
        {
            if (date.DayOfWeek == DayOfWeek.Saturday && !includeSaturday) continue;
            if (date.DayOfWeek == DayOfWeek.Sunday && !includeSunday) continue;
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { dates.Add(date); continue; }
            if (await businessDayService.IsBusinessDayAsync(date, new CalendarScope("AllSchool"), cancellationToken)) dates.Add(date);
        }
        if (dates.Count == 0) throw new RequestValidationException("Seçilen aralıkta uygulanabilir gün bulunamadı.");
        return dates;
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
