using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Application.Tuition;

/// <summary>
/// Anasinifi ve benzeri ucretli siniflarin ucret plani. Plan sinifa ya da tek ogrenciye
/// tanimlanir; kaydedilirken taksitler onceden uretilir (bkz. <see cref="TuitionSchedule"/>)
/// ki veli "ne zaman ne kadar" listesini gorsun. Tahsilat kasadan girilir ve taksite
/// islenir; para hareketinin kendisi IncomeTransaction olarak durur.
/// </summary>
public sealed class TuitionService(ITuitionRepository repository, TimeProvider timeProvider)
{
    /// <summary>Tek planda azami tutar; yanlislikla fazladan sifir yazilmasini yakalar.</summary>
    public const decimal MaxAmount = 1_000_000m;

    public Task<PagedResult<TuitionPlanDetails>> ListAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Page < 1 || filter.PageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 olmalıdır.");
        return repository.ListAsync(filter, Today(), cancellationToken);
    }

    public async Task<TuitionPlanDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(id, Today(), cancellationToken)
            ?? throw new EntityNotFoundException("Ücret planı bulunamadı.");

    public async Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        await repository.ForStudentAsync(studentId, Today(), cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");

    public Task<TuitionPlanDetails> SaveAsync(SaveTuitionPlanRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var plan = Validate(request, Today());
        return repository.SaveAsync(request, TuitionSchedule.Build(plan), Today(), actorId, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeleteAsync(id, actorId, cancellationToken))
            throw new EntityNotFoundException("Ücret planı bulunamadı.");
    }

    public Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new RequestValidationException("Tutar sıfırdan büyük ve en fazla iki ondalık basamaklı olmalıdır.");
        return repository.ApplyPaymentAsync(request, Today(), actorId, cancellationToken);
    }

    /// <summary>
    /// Istegi dogrular ve kaydedilecek plan nesnesini uretir. Dogrulama servis katmaninda
    /// durur ki hem API hem testler ayni kurali gorsun.
    /// </summary>
    /// <param name="today">
    /// OKUL gunu (Istanbul). Verilmezse sunucunun yerel gunu kullanilir; sunucu UTC ise
    /// ayin 1'i civarinda plan BIR AY kayar ve bu KALICI olarak veritabanina yazilir.
    /// Uretimde her zaman TuitionService.Today() gecilir.
    /// </param>
    public static TuitionPlan Validate(SaveTuitionPlanRequest request, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TuitionPlanKinds.IsKnown(request.Kind))
            throw new RequestValidationException("Ücret türü Installment, Monthly veya Daily olmalıdır.");
        // Plan ya sinifa ya ogrenciye aittir; ikisi birden dolu olursa hangi tutarin
        // gecerli oldugu belirsizlesir ve ogrenci iki kez borclanir.
        if ((request.ClassId is null) == (request.StudentId is null))
            throw new RequestValidationException("Plan ya bir sınıfa ya da bir öğrenciye tanımlanmalıdır.");

        var period = (request.Period ?? string.Empty).Trim();
        if (period.Length is < 2 or > 20)
            throw new RequestValidationException("Dönem 2-20 karakter olmalıdır (örn. 2026-2027).");
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new RequestValidationException("Tutar sıfırdan büyük ve en fazla iki ondalık basamaklı olmalıdır.");
        if (request.Amount > MaxAmount)
            throw new RequestValidationException($"Tutar en fazla {MaxAmount:N0} ₺ olabilir.");

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500) throw new RequestValidationException("Açıklama en fazla 500 karakter olmalıdır.");

        var down = request.DownPayment;
        var count = request.InstallmentCount;
        var dueDay = request.DueDayOfMonth;

        if (request.Kind == TuitionPlanKinds.Daily)
        {
            // Gunluk ucrette taksit uretilmez; kullanici yanlislikla taksit yazdiysa
            // sessizce yok saymak yerine soyluyoruz.
            if (down != 0 || count != 0)
                throw new RequestValidationException("Günlük ücrette peşinat ve taksit sayısı girilmez.");
            down = 0; count = 0; dueDay = TuitionSchedule.MinDueDay;
        }
        else
        {
            if (count is < 1 or > TuitionSchedule.MaxInstallmentCount)
                throw new RequestValidationException($"Taksit sayısı 1 ile {TuitionSchedule.MaxInstallmentCount} arasında olmalıdır.");
            if (dueDay is < TuitionSchedule.MinDueDay or > TuitionSchedule.MaxDueDay)
                throw new RequestValidationException($"Ayın günü {TuitionSchedule.MinDueDay} ile {TuitionSchedule.MaxDueDay} arasında olmalıdır (29-31 her ayda bulunmaz).");
            if (request.Kind == TuitionPlanKinds.Monthly)
            {
                if (down != 0) throw new RequestValidationException("Aylık sabit ücrette peşinat girilmez.");
                down = 0;
            }
            else
            {
                if (down < 0 || decimal.Round(down, 2) != down)
                    throw new RequestValidationException("Peşinat sıfır ya da en fazla iki ondalıklı bir tutar olmalıdır.");
                // Pesinat toplami asarsa taksitlere bolunecek para kalmaz; kullanici
                // buyuk olasilikla toplam ile pesinati karistirmistir.
                if (down >= request.Amount)
                    throw new RequestValidationException("Peşinat toplam ücretten küçük olmalıdır.");
            }
        }

        return new TuitionPlan
        {
            Id = Guid.NewGuid(),
            ClassId = request.ClassId,
            StudentId = request.StudentId,
            Kind = request.Kind,
            Period = period,
            AmountCents = StudentBalanceService.ToCents(request.Amount),
            DownPaymentCents = StudentBalanceService.ToCents(down),
            InstallmentCount = count,
            DueDayOfMonth = dueDay,
            // OKUL gunu kullanilir; DateTime.Today SUNUCUNUN saat dilimidir.
            StartsOn = request.StartsOn ?? FirstOfMonth(today),
            Note = note,
            IsActive = request.IsActive
        };
    }

    private DateOnly Today() => StudentBalanceService.IstanbulDate(timeProvider.GetUtcNow());

    /// <summary>Verilen gunun (yoksa sunucu gununun) ayinin ilk gunu.</summary>
    private static DateOnly FirstOfMonth(DateOnly? today)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Today);
        return new DateOnly(day.Year, day.Month, 1);
    }
}
