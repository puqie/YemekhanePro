using Yemekhane.Domain.Entities;

namespace Yemekhane.Application.Tuition;

/// <summary>Uretilecek tek taksit satiri (kayit oncesi, saf hesap sonucu).</summary>
/// <param name="Sequence">0 = pesinat, 1..N = taksitler.</param>
public readonly record struct PlannedInstallment(int Sequence, DateOnly DueOn, long AmountCents, string? Note);

/// <summary>
/// Taksit takviminin saf hesabi: veritabani ve saat dilimi bilmez, ayni girdiye her zaman
/// ayni ciktiyi verir. Kurallar:
///
/// * Kurus artigi ILK taksite eklenir. 48.000,01 ₺'yi 10'a bolerken son taksiti 1 kurus
///   fazla yapmak yerine ilk taksite koyariz; veli ilk odemede fazlaligi gorur, son ayda
///   surprizle karsilasmaz ve toplam her zaman tam tutar eder.
/// * Ayin 31'i secilemez (subatta karsiligi yok); ust sinir 28'dir ve her ayda vardir.
/// * Pesinat 0'dan buyukse 0 numarali satir olarak plan baslangicinda tahakkuk eder.
/// </summary>
public static class TuitionSchedule
{
    /// <summary>Taksit gunu bu araligin disinda olamaz; 29-31 subatta bulunmaz.</summary>
    public const int MinDueDay = 1;
    public const int MaxDueDay = 28;
    /// <summary>Tek planda uretilebilecek azami taksit; kazara 999 girilip tablo sismesin.</summary>
    public const int MaxInstallmentCount = 60;

    public static IReadOnlyList<PlannedInstallment> Build(TuitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind == TuitionPlanKinds.Daily) return [];

        var rows = new List<PlannedInstallment>();
        var count = Math.Max(plan.InstallmentCount, 0);
        if (plan.Kind == TuitionPlanKinds.Monthly)
        {
            for (var index = 0; index < count; index++)
                rows.Add(new PlannedInstallment(index + 1, DueDate(plan, index), plan.AmountCents, null));
            return rows;
        }

        if (plan.DownPaymentCents > 0)
            rows.Add(new PlannedInstallment(0, plan.StartsOn, plan.DownPaymentCents, "Peşinat"));

        var remaining = plan.AmountCents - plan.DownPaymentCents;
        if (remaining <= 0 || count == 0) return rows;

        var each = remaining / count;
        var leftover = remaining - (each * count);
        for (var index = 0; index < count; index++)
            rows.Add(new PlannedInstallment(index + 1, DueDate(plan, index), index == 0 ? each + leftover : each, null));
        return rows;
    }

    /// <summary>
    /// Taksitin vade tarihi: baslangic ayindan itibaren <paramref name="monthOffset"/> ay sonra,
    /// planin secilen gununde. Gun ayin son gununden buyukse (28 siniri sayesinde olmaz ama
    /// eski kayitlar 31 tasiyabilir) ayin son gunune cekilir.
    /// </summary>
    public static DateOnly DueDate(TuitionPlan plan, int monthOffset)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var month = new DateOnly(plan.StartsOn.Year, plan.StartsOn.Month, 1).AddMonths(monthOffset);
        var day = Math.Clamp(plan.DueDayOfMonth, MinDueDay, DateTime.DaysInMonth(month.Year, month.Month));
        return new DateOnly(month.Year, month.Month, day);
    }

    /// <summary>Vadesi gecmis ve tamami odenmemis taksit "Gecikmiş" sayilir.</summary>
    public static string StatusOf(long amountCents, long paidCents, bool isCancelled, DateOnly dueOn, DateOnly today)
    {
        if (isCancelled) return TuitionInstallmentStatuses.Cancelled;
        if (paidCents >= amountCents) return TuitionInstallmentStatuses.Paid;
        if (dueOn < today) return TuitionInstallmentStatuses.Overdue;
        return paidCents > 0 ? TuitionInstallmentStatuses.PartiallyPaid : TuitionInstallmentStatuses.Pending;
    }
}
