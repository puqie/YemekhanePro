using Yemekhane.Application.Common;
using Yemekhane.Application.Income;

namespace Yemekhane.Application.Balances;

/// <summary>Defter satiri turleri (StudentBalanceEntry.Kind). Ekranda EnumTextConverter "BalanceKind" sozlugu cevirir.</summary>
public static class StudentBalanceEntryKinds
{
    public const string TopUp = "TopUp";
    public const string Deduction = "Deduction";
    public const string Refund = "Refund";
    public const string Adjustment = "Adjustment";
}

/// <summary>Defter satirini doguran kaydin turu (StudentBalanceEntry.ReferenceType).</summary>
public static class StudentBalanceReferenceTypes
{
    public const string IncomeTransaction = "IncomeTransaction";
    public const string AccessLog = "AccessLog";
}

/// <summary>Bakiye yuklemelerinin kaydedildigi gelir turu; yoksa ilk yuklemede olusturulur.</summary>
public static class StudentBalanceIncomeType
{
    public const string Name = "Bakiye Yükleme";
}

/// <summary>Gecis kararinin bakiye nedeni kodlari (AccessLog.Reason); Turkcesi EnumTextConverter "Reason" sozlugunde.</summary>
public static class BalanceAccessReasons
{
    public const string BalanceUsed = "BalanceUsed";
    public const string InsufficientBalance = "InsufficientBalance";
}

public sealed record StudentBalanceEntryDetails(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Kind,
    decimal Amount,
    string? Note,
    string? ReferenceType,
    Guid? ReferenceId,
    DateOnly? ExpiresOn,
    Guid? CreatedBy);

/// <summary>
/// Balance: tum satirlarin toplami. Available: AsOf gunu harcanabilir kisim (bitis tarihi
/// gecmis yuklemelerin harcanmamis kalani dusulmus). Expired: o yanmis kisim.
/// </summary>
public sealed record StudentBalanceSummary(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    decimal Balance,
    decimal Available,
    decimal Expired,
    DateOnly AsOf,
    PagedResult<StudentBalanceEntryDetails> Entries);

/// <summary>StudentId ya da StudentNo'dan biri zorunlu. Amount ₺ cinsinden (en fazla iki ondalik).</summary>
public sealed record BalanceTopUpRequest(
    Guid? StudentId,
    string? StudentNo,
    decimal Amount,
    string? Note = null,
    DateOnly? ExpiresOn = null,
    Guid? OperationId = null,
    DateTimeOffset? TransactionAt = null);

/// <summary>Dogrulanmis, ogrencisi cozulmus yukleme komutu (depo katmanina gider).</summary>
public sealed record BalanceTopUpCommand(
    Guid OperationId,
    Guid StudentId,
    long AmountCents,
    string? Note,
    DateOnly? ExpiresOn,
    DateTimeOffset TransactionAt);

public sealed record BalanceTopUpResult(
    IncomeTransactionDetails Transaction,
    StudentBalanceEntryDetails Entry,
    decimal Balance,
    decimal Available);

public interface IStudentBalanceRepository
{
    Task<Guid?> FindStudentIdAsync(Guid? studentId, string? studentNo, CancellationToken cancellationToken);
    Task<StudentBalanceSummary?> GetAsync(Guid studentId, DateOnly asOf, int page, int pageSize, CancellationToken cancellationToken);
    Task<BalanceTopUpResult> TopUpAsync(BalanceTopUpCommand command, Guid actorId, CancellationToken cancellationToken);
}
