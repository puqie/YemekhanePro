using Yemekhane.Application.Common;

namespace Yemekhane.Application.Cash;

public sealed class CashService(ICashRepository repository, TimeProvider timeProvider)
{
    private static readonly TimeZoneInfo IstanbulTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public Task<CashSummary> GetDailyAsync(DateOnly? date = null,
        CancellationToken cancellationToken = default) =>
        GetSummaryAsync(CashSummaryPeriod.Daily, date, null, null, cancellationToken);

    public async Task<CashSummary> GetSummaryAsync(
        CashSummaryPeriod period,
        DateOnly? date = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), IstanbulTimeZone).DateTime);
        var (firstDay, lastDay) = ResolveRange(period, date ?? today, from, to);
        var utcFrom = ToUtcBoundary(firstDay);
        var utcToExclusive = ToUtcBoundary(lastDay.AddDays(1));
        var aggregate = await repository.AggregateAsync(utcFrom, utcToExclusive, cancellationToken);

        return new CashSummary(period, firstDay, lastDay, utcFrom, utcToExclusive,
            aggregate.TotalAmount, aggregate.TransactionCount, aggregate.VoidedAmount,
            aggregate.VoidedCount, aggregate.ByIncomeType);
    }

    private static (DateOnly From, DateOnly To) ResolveRange(
        CashSummaryPeriod period, DateOnly date, DateOnly? from, DateOnly? to)
    {
        if (period == CashSummaryPeriod.Custom)
        {
            if (!from.HasValue || !to.HasValue)
                throw new RequestValidationException("Özel tarih aralığı için başlangıç ve bitiş tarihleri zorunludur.");
            if (from > to)
                throw new RequestValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
            return (from.Value, to.Value);
        }

        return period switch
        {
            CashSummaryPeriod.Daily => (date, date),
            CashSummaryPeriod.Weekly or CashSummaryPeriod.IsoWeek => Week(date),
            CashSummaryPeriod.Monthly => (new DateOnly(date.Year, date.Month, 1),
                new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))),
            _ => throw new RequestValidationException("Geçersiz kasa özet dönemi.")
        };
    }

    private static (DateOnly From, DateOnly To) Week(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        var monday = date.AddDays(-daysSinceMonday);
        return (monday, monday.AddDays(6));
    }

    private static DateTimeOffset ToUtcBoundary(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, IstanbulTimeZone), TimeSpan.Zero);
    }
}
