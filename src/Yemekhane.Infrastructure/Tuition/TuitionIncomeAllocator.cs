using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Tuition;

/// <summary>
/// Kasa tahsilatini anasinifi taksitlerine sayar / geri alir. Kasa deposu (gelir kaydi ve
/// iptali) ile ucret deposu (toplu mutabakat) ayni kurali paylassin diye buradadir.
///
/// <para>
/// Saha: veli her ay odeme yapiyor, kasiyer "Gelir ekle" ile giriyor ama hicbir yer bu parayi
/// taksite yazmiyordu; okul "kacinci taksiti odedi" sorusuna cevap alamiyordu. Kural: gelir
/// turu "taksite sayilir" isaretliyse ve kayit bir ogrenciye bagliysa tutar, ogrencinin gecerli
/// planindaki (kendi plani, yoksa sinifinin plani) ODENMEMIS taksitlere vade sirasiyla dagitilir.
/// Taksitlere sigmayan kisim (fazla odeme) kasada durur, borca yazilmaz; kasiyere uyari doner.
/// </para>
/// <para>
/// Odenen tutar veritabaninda ARTIRILIR (ExecuteUpdate), okunan degerin uzerine yazilmaz; bkz.
/// <see cref="EfTuitionRepository.ApplyPaymentAsync"/>'deki iki kasiyer notu. Cagiran, islemi
/// kendi transaction'i icinde yapar: taksit artislari ve TuitionPayment satirlari gelir kaydiyla
/// birlikte ya hep yazilir ya hic.
/// </para>
/// </summary>
internal static class TuitionIncomeAllocator
{
    /// <summary>Ogrencinin gecerli plani: kendi aktif plani (en yeni), yoksa sinifinin aktif plani.</summary>
    public static async Task<TuitionPlan?> EffectivePlanAsync(YemekhaneDbContext db, Guid studentId, Guid? classId,
        CancellationToken cancellationToken)
    {
        var own = (await db.TuitionPlans.AsNoTracking()
                .Where(x => x.StudentId == studentId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (own is not null || classId is null) return own;
        return (await db.TuitionPlans.AsNoTracking()
                .Where(x => x.ClassId == classId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    /// <summary>
    /// Tahsilati taksitlere sayar. Daha once sayilmis tahsilat icin <c>null</c> doner (yeniden
    /// calistirmak guvenli); plan ya da acik taksit yoksa satirsiz sonuc doner ve tutarin tamami
    /// <see cref="TuitionAllocationResult.Unallocated"/> olur.
    /// </summary>
    public static async Task<TuitionAllocationResult?> AllocateAsync(YemekhaneDbContext db, IncomeTransaction income,
        DateTimeOffset now, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(income);
        if (income.StudentId is not { } studentId || income.IsVoided) return null;
        if (await db.TuitionPayments.AnyAsync(x => x.IncomeTransactionId == income.Id, cancellationToken)) return null;

        var amountCents = StudentBalanceService.ToCents(income.Amount);
        var classId = await db.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => x.ClassId).SingleOrDefaultAsync(cancellationToken);
        var plan = await EffectivePlanAsync(db, studentId, classId, cancellationToken);
        if (plan is null) return new TuitionAllocationResult(studentId, [], income.Amount);

        var open = (await db.TuitionInstallments.AsNoTracking()
                .Where(x => x.PlanId == plan.Id && x.StudentId == studentId && !x.IsCancelled && x.PaidCents < x.AmountCents)
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.DueOn).ThenBy(x => x.Sequence).ToList();

        var lines = new List<TuitionAllocationLine>();
        var remaining = amountCents;
        foreach (var installment in open)
        {
            if (remaining <= 0) break;
            var due = installment.AmountCents - installment.PaidCents;
            var share = Math.Min(due, remaining);
            remaining -= share;
            db.Add(new TuitionPayment
            {
                InstallmentId = installment.Id,
                IncomeTransactionId = income.Id,
                AmountCents = share,
                PaidAt = income.TransactionAt,
                CreatedBy = actorId,
                CreatedAt = now
            });
            await db.TuitionInstallments.Where(x => x.Id == installment.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.PaidCents, x => x.PaidCents + share)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            lines.Add(new TuitionAllocationLine(installment.Sequence, StudentBalanceService.ToLira(share), share == due));
        }

        return new TuitionAllocationResult(studentId, lines, StudentBalanceService.ToLira(remaining));
    }

    /// <summary>
    /// Iptal edilen tahsilatin taksit paylarini geri alir: TuitionPayment satirlari silinir, taksitin
    /// odenen tutari dusulur (sifirin altina inmez). Geri alinan taksit sayisini doner.
    /// </summary>
    public static async Task<int> ReverseAsync(YemekhaneDbContext db, Guid incomeTransactionId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payments = await db.TuitionPayments.Where(x => x.IncomeTransactionId == incomeTransactionId)
            .ToListAsync(cancellationToken);
        if (payments.Count == 0) return 0;
        foreach (var payment in payments)
        {
            var cents = payment.AmountCents;
            await db.TuitionInstallments.Where(x => x.Id == payment.InstallmentId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.PaidCents, x => x.PaidCents >= cents ? x.PaidCents - cents : 0)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }
        db.RemoveRange(payments);
        return payments.Select(x => x.InstallmentId).Distinct().Count();
    }

    /// <summary>Ogrencinin plandaki iptal edilmemis taksitlerinden kaci tamamen odendi / toplam kac taksit.</summary>
    public static async Task<(int Paid, int Count)> ProgressAsync(YemekhaneDbContext db, Guid planId, Guid studentId,
        CancellationToken cancellationToken)
    {
        var rows = await db.TuitionInstallments.AsNoTracking()
            .Where(x => x.PlanId == planId && x.StudentId == studentId && !x.IsCancelled)
            .Select(x => new { x.AmountCents, x.PaidCents }).ToListAsync(cancellationToken);
        return (rows.Count(x => x.PaidCents >= x.AmountCents), rows.Count);
    }

    /// <summary>Taksite sayilir turden, ogrenciye bagli, iptal edilmemis ve hic taksite islenmemis tahsilatlar.</summary>
    public static IQueryable<IncomeTransaction> Unapplied(YemekhaneDbContext db) =>
        from income in db.Set<IncomeTransaction>()
        join type in db.Set<IncomeType>() on income.IncomeTypeId equals type.Id
        where type.CountsTowardTuition && income.StudentId != null && !income.IsVoided
              && !db.TuitionPayments.Any(p => p.IncomeTransactionId == income.Id)
        select income;
}
