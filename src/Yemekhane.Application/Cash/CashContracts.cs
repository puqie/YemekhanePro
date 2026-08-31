namespace Yemekhane.Application.Cash;

public enum CashSummaryPeriod
{
    Daily,
    Weekly,
    IsoWeek,
    Monthly,
    Custom
}

public sealed record CashTypeBreakdown(
    Guid IncomeTypeId,
    string IncomeTypeName,
    decimal Amount,
    int TransactionCount);

public sealed record CashSummary(
    CashSummaryPeriod Period,
    DateOnly From,
    DateOnly To,
    DateTimeOffset UtcFrom,
    DateTimeOffset UtcToExclusive,
    decimal TotalAmount,
    int TransactionCount,
    decimal VoidedAmount,
    int VoidedCount,
    IReadOnlyList<CashTypeBreakdown> ByIncomeType);

public sealed record CashAggregate(
    decimal TotalAmount,
    int TransactionCount,
    decimal VoidedAmount,
    int VoidedCount,
    IReadOnlyList<CashTypeBreakdown> ByIncomeType);

public interface ICashRepository
{
    Task<CashAggregate> AggregateAsync(
        DateTimeOffset utcFrom,
        DateTimeOffset utcToExclusive,
        CancellationToken cancellationToken);
}
