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
public sealed record StudentTuitionSummary(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    string? ClassName,
    bool Inherited,
    TuitionPlanDetails? Plan);

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
}
