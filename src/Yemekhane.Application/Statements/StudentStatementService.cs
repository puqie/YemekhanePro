using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Statements;

/// <summary>
/// Ogrenci ekstresi: veli "1-2 yil onceki odemelerimi gorebilir miyim" diye sordugunda
/// tek belgede odemeler, bakiye hareketleri, taksit/borc durumu ve yemek kullanimi.
/// Yil sonu sifirlamasi mali kayitlari SILMEDIGI icin gecmis yillar da sorgulanabilir.
/// </summary>
public sealed class StudentStatementService(IStudentStatementRepository repository, TimeProvider timeProvider)
{
    /// <summary>Tek ekstrede azami gun; kazara 1900'den bugune sorgu acilmasin.</summary>
    public const int MaxRangeDays = 1100;

    public async Task<StudentStatement> BuildAsync(StudentStatementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From)
            throw new RequestValidationException("Bitiş tarihi başlangıçtan önce olamaz.");
        var days = query.To.DayNumber - query.From.DayNumber + 1;
        if (days > MaxRangeDays)
            throw new RequestValidationException($"Ekstre en fazla {MaxRangeDays} günlük olabilir; tarih aralığını daraltın.");
        return await repository.BuildAsync(query, Today(), cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
    }

    /// <summary>Varsayilan aralik: icinde bulunulan egitim yili (1 Eylul - 31 Agustos).</summary>
    public (DateOnly From, DateOnly To) DefaultRange()
    {
        var today = Today();
        var startYear = today.Month >= 9 ? today.Year : today.Year - 1;
        return (new DateOnly(startYear, 9, 1), new DateOnly(startYear + 1, 8, 31));
    }

    private DateOnly Today() => StudentBalanceService.IstanbulDate(timeProvider.GetUtcNow());
}
