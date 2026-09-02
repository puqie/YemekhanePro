using Yemekhane.Application.Common;

namespace Yemekhane.Application.Balances;

/// <summary>
/// On odemeli bakiye: sorgulama ve yukleme. Yukleme tek transaction'da hem "Bakiye Yükleme"
/// gelir islemi hem defter satiri yazar (depo katmani); iptal mevcut gelir iptal akisindan
/// gecer ve orada iade satiri uretilir.
/// </summary>
public sealed class StudentBalanceService(IStudentBalanceRepository repository, TimeProvider timeProvider)
{
    public const decimal MaxTopUpAmount = 100_000m;
    private static readonly TimeZoneInfo IstanbulTimeZone = FindIstanbulTimeZone();

    public async Task<StudentBalanceSummary> GetAsync(Guid studentId, int page = 1, int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 olmalıdır.");
        return await repository.GetAsync(studentId, Today(), page, pageSize, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
    }

    public async Task<BalanceTopUpResult> TopUpAsync(BalanceTopUpRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new RequestValidationException("Tutar sıfırdan büyük ve en fazla iki ondalık basamaklı olmalıdır.");
        if (request.Amount > MaxTopUpAmount)
            throw new RequestValidationException($"Tek seferde en fazla {MaxTopUpAmount:N0} ₺ yüklenebilir.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500) throw new RequestValidationException("Açıklama en fazla 500 karakter olmalıdır.");
        var studentNo = string.IsNullOrWhiteSpace(request.StudentNo) ? null : request.StudentNo.Trim();
        if (request.StudentId is null && studentNo is null)
            throw new RequestValidationException("Öğrenci kimliği veya öğrenci numarası zorunludur.");
        // Bitis tarihi bugunden once olamaz: yuklendigi an yanmis bir bakiye kullanicinin
        // niyet ettigi sey olamaz (buyuk olasilikla yanlis yil/ay secilmistir).
        if (request.ExpiresOn is { } expires && expires < Today())
            throw new RequestValidationException("Bitiş tarihi bugünden önce olamaz.");

        var studentId = await repository.FindStudentIdAsync(request.StudentId, studentNo, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
        var command = new BalanceTopUpCommand(request.OperationId ?? Guid.NewGuid(), studentId,
            ToCents(request.Amount), note, request.ExpiresOn, request.TransactionAt ?? timeProvider.GetUtcNow());
        return await repository.TopUpAsync(command, actorId, cancellationToken);
    }

    public static long ToCents(decimal lira) => (long)decimal.Round(lira * 100m, 0, MidpointRounding.AwayFromZero);
    public static decimal ToLira(long cents) => cents / 100m;

    public static DateOnly IstanbulDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, IstanbulTimeZone).DateTime);

    private DateOnly Today() => IstanbulDate(timeProvider.GetUtcNow());

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
