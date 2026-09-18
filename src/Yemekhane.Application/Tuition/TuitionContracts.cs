using Yemekhane.Application.Common;

namespace Yemekhane.Application.Tuition;

/// <summary>Plan kapsami: sinif varsayilani mi, ogrenciye ozel mi.</summary>
public static class TuitionScopes
{
    public const string Class = "Class";
    public const string Student = "Student";
}

/// <param name="Kind">Installment | Monthly | Daily.</param>
/// <param name="ClassId">Sinif plani icin dolu; ogrenci planinda null.</param>
/// <param name="StudentId">Ogrenci plani icin dolu; sinif planinda null.</param>
/// <param name="Amount">Installment: donem toplami. Monthly: aylik. Daily: gunluk (₺).</param>
public sealed record SaveTuitionPlanRequest(
    string Kind,
    string Period,
    decimal Amount,
    Guid? ClassId = null,
    Guid? StudentId = null,
    decimal DownPayment = 0,
    int InstallmentCount = 0,
    int DueDayOfMonth = 1,
    DateOnly? StartsOn = null,
    string? Note = null,
    bool IsActive = true);

public sealed record TuitionInstallmentDetails(
    Guid Id,
    Guid PlanId,
    Guid StudentId,
    int Sequence,
    DateOnly DueOn,
    decimal Amount,
    decimal Paid,
    decimal Remaining,
    string Status,
    string StatusLabel,
    bool IsCancelled,
    string? Note);

public sealed record TuitionPlanDetails(
    Guid Id,
    string Scope,
    Guid? ClassId,
    string? ClassName,
    Guid? StudentId,
    string? StudentName,
    string? StudentNo,
    string Kind,
    string KindLabel,
    string Period,
    decimal Amount,
    decimal DownPayment,
    int InstallmentCount,
    int DueDayOfMonth,
    DateOnly StartsOn,
    string? Note,
    bool IsActive,
    decimal TotalDue,
    decimal TotalPaid,
    decimal Outstanding,
    decimal OverdueAmount,
    IReadOnlyList<TuitionInstallmentDetails> Installments);

/// <summary>Ogrencinin gecerli plani: kendi plani varsa o, yoksa sinifinin plani.</summary>
/// <param name="Inherited">Plan sinifindan mi geliyor (ogrenciye ozel degil).</param>
/// <param name="Payments">Bu ogrencinin taksitlerine sayilmis tahsilatlar, en yeni ustte.</param>
public sealed record StudentTuitionSummary(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    string? ClassName,
    bool Inherited,
    TuitionPlanDetails? Plan,
    IReadOnlyList<TuitionPaymentDetails>? Payments = null);

/// <summary>Bir kasa tahsilatinin taksite sayilan parcasi; ayni tahsilat birden cok taksite bolunebilir.</summary>
public sealed record TuitionPaymentDetails(
    Guid Id,
    Guid IncomeTransactionId,
    int Sequence,
    DateTimeOffset TransactionAt,
    decimal Amount,
    string IncomeTypeName,
    string? Description);

/// <summary>
/// SAHA: "Ogrencinin uzerine tikladigim zaman simdiye kadar KAC KEZ odeme yapmis gormem
/// gerekiyor -- benden ziyade PATRONUN gormesi gerekiyor." Ogrenci detayinda, sekmeye
/// girmeden gorunen tek satirlik odeme ozeti. HER ogrenci icin calisir: anasinifinda plan
/// varsa taksit ilerlemesi de dolar, ilkokulda plan yoksa yalnizca sayim/tutar gorunur.
/// </summary>
/// <param name="PaymentCount">Iptal EDILMEMIS, ogrenciye bagli kasa tahsilati sayisi.</param>
/// <param name="TuitionPaymentCount">
/// Bunlardan kacinin taksite sayildigi (ayri yazilir: "5 tahsilat · 3'u taksit").
/// </param>
/// <param name="VoidedCount">Iptal edilen tahsilat sayisi; 0 degilse ekranda ayrica belirtilir.</param>
/// <param name="TotalPaid">Iptal edilmemis tahsilatlarin toplami.</param>
/// <param name="LastPaidAt">En son tahsilatin zamani; hic yoksa null.</param>
/// <param name="HasPlan">Gecerli bir ucret plani var mi (kendi plani ya da sinifinin plani).</param>
public sealed record StudentPaymentSummary(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    int PaymentCount,
    int TuitionPaymentCount,
    int VoidedCount,
    decimal TotalPaid,
    DateTimeOffset? LastPaidAt,
    bool HasPlan,
    int InstallmentCount,
    int PaidInstallments,
    decimal Outstanding,
    DateOnly? NextDueOn);

/// <summary>
/// Anasinifi ekraninin satiri: ogrencinin gecerli planindaki taksit ilerlemesi. Plan yoksa
/// sayimlar sifirdir ve <see cref="HasPlan"/> false doner; ekran "Plan yok" yazar.
/// </summary>
public sealed record KindergartenStudentRow(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    string? ClassName,
    bool HasPlan,
    int InstallmentCount,
    int PaidInstallments,
    int PaymentCount,
    decimal TotalDue,
    decimal TotalPaid,
    decimal Outstanding,
    decimal OverdueAmount,
    int OverdueCount,
    DateOnly? NextDueOn,
    DateTimeOffset? LastPaidAt)
{
    /// <summary>"3/10" gibi; plan yoksa "Plan yok".</summary>
    public string Progress => HasPlan ? $"{PaidInstallments}/{InstallmentCount}" : "Plan yok";
}

/// <param name="UnappliedIncomeCount">
/// Taksite sayilir isaretli turden girilmis ama hicbir taksite islenmemis tahsilat sayisi
/// (bu surumden onceki kayitlar); "taksitlere isle" dugmesi bunu gosterir.
/// </param>
/// <param name="HasTuitionIncomeType">Kasa > Gelir Turleri'nde en az bir aktif tur isaretli mi.</param>
public sealed record KindergartenOverview(
    IReadOnlyList<KindergartenStudentRow> Students,
    int UnappliedIncomeCount,
    bool HasTuitionIncomeType);

/// <summary>Bir tahsilatin taksitlere dagilimi: sira numarasi ve o taksite sayilan tutar.</summary>
public sealed record TuitionAllocationLine(int Sequence, decimal Amount, bool CompletesInstallment);

/// <param name="Unallocated">Taksitlere sigmayan kisim (fazla odeme); 0 ise tamami sayildi.</param>
public sealed record TuitionAllocationResult(Guid StudentId, IReadOnlyList<TuitionAllocationLine> Lines, decimal Unallocated)
{
    public bool Applied => Lines.Count > 0;

    /// <summary>Kasiyerin gordugu tek satirlik ozet: "1. ve 2. taksite sayıldı (2/10 ödendi)".</summary>
    public string Describe(int paidInstallments, int installmentCount)
    {
        if (!Applied) return "Tahsilat taksite sayılmadı.";
        var parts = Lines.Select(x => $"{x.Sequence}.").ToList();
        var which = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " ve " + parts[^1];
        var text = $"{which} taksite sayıldı ({paidInstallments}/{installmentCount} ödendi).";
        return Unallocated > 0 ? text + $" {Unallocated:N2} ₺ fazla ödeme taksitlere sığmadı." : text;
    }
}

/// <summary>"Kasadaki tahsilatlari taksitlere isle" kosusunun ozeti.</summary>
public sealed record TuitionReconcileResult(int Examined, int Applied, int SkippedNoInstallment);

public sealed record TuitionPlanFilter(
    string? Period = null,
    Guid? ClassId = null,
    Guid? StudentId = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 50);

/// <summary>Bir tahsilati taksite sayma istegi; kasa gelir kaydiyla birlikte cagrilir.</summary>
public sealed record ApplyTuitionPaymentRequest(Guid InstallmentId, Guid IncomeTransactionId, decimal Amount);

public interface ITuitionRepository
{
    Task<PagedResult<TuitionPlanDetails>> ListAsync(TuitionPlanFilter filter, DateOnly today, CancellationToken cancellationToken);
    Task<TuitionPlanDetails?> GetAsync(Guid id, DateOnly today, CancellationToken cancellationToken);
    Task<StudentTuitionSummary?> ForStudentAsync(Guid studentId, DateOnly today, CancellationToken cancellationToken);
    Task<TuitionPlanDetails> SaveAsync(SaveTuitionPlanRequest request, IReadOnlyList<PlannedInstallment> installments,
        DateOnly today, Guid actorId, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
    Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, DateOnly today, Guid actorId,
        CancellationToken cancellationToken);
    /// <summary>Aktif anasinifi ogrencileri ve taksit ilerlemeleri; sinif turu <c>Anasinifi</c> olanlar.</summary>
    Task<KindergartenOverview> KindergartenAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>Ogrenci detayindaki odeme ozeti satiri; ogrenci yoksa null.</summary>
    Task<StudentPaymentSummary?> PaymentSummaryAsync(Guid studentId, DateOnly today, CancellationToken cancellationToken);
    /// <summary>
    /// Taksite sayilir turden girilmis ama hic taksite islenmemis tahsilatlari tarih sirasiyla
    /// taksitlere sayar. Yeniden calistirmak guvenlidir: islenmis tahsilat atlanir.
    /// </summary>
    Task<TuitionReconcileResult> ReconcileAsync(DateOnly today, Guid actorId, CancellationToken cancellationToken);
}
