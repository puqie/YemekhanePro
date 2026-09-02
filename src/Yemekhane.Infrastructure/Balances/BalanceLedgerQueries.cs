using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Balances;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Balances;

/// <summary>
/// Bir ogrencinin defterini yukleyip toplamlari hesaplar. Gecis karari, kasa iptali ve
/// turnike telafisi ayni hesabi kullanir; ucu ayri yazilsaydi "kullanilabilir bakiye"
/// tanimlari birbirinden kayardi.
/// </summary>
internal static class BalanceLedgerQueries
{
    public static async Task<LedgerTotals> TotalsAsync(YemekhaneDbContext dbContext, Guid studentId, DateOnly asOf,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.StudentBalanceEntries.AsNoTracking()
            .Where(x => x.StudentId == studentId)
            .Select(x => new { x.Id, x.AmountCents, x.Kind, x.OccurredAt, x.ExpiresOn, x.ReferenceId })
            .ToListAsync(cancellationToken);
        return BalanceLedger.Compute(rows.Select(x => new LedgerLine(x.Id, x.AmountCents, x.Kind, x.OccurredAt,
            StudentBalanceService.IstanbulDate(x.OccurredAt), x.ExpiresOn, x.ReferenceId)), asOf);
    }

    public static LedgerLine ToLine(Domain.Entities.StudentBalanceEntry x) => new(x.Id, x.AmountCents, x.Kind, x.OccurredAt,
        StudentBalanceService.IstanbulDate(x.OccurredAt), x.ExpiresOn, x.ReferenceId);
}
