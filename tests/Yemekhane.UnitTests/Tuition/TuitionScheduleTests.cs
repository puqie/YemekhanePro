using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Taksit takvimi para dagitir: toplamin kurusu kaybolursa okul ile veli arasinda
/// kapanmayan bir fark kalir. Bolunmeyen kurus ilk taksite eklenir ve uretilen
/// satirlarin toplami her zaman planin tutarina esittir.
/// </summary>
public sealed class TuitionScheduleTests
{
    private static TuitionPlan Plan(string kind, long amount, long down = 0, int count = 10, int dueDay = 5,
        int year = 2026, int month = 10) => new()
    {
        Kind = kind,
        Period = "2026-2027",
        AmountCents = amount,
        DownPaymentCents = down,
        InstallmentCount = count,
        DueDayOfMonth = dueDay,
        StartsOn = new DateOnly(year, month, 1)
    };

    [Fact]
    public void InstallmentPlanSplitsTotalEvenly()
    {
        var rows = TuitionSchedule.Build(Plan(TuitionPlanKinds.Installment, 4_800_000, count: 10));

        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal(480_000, row.AmountCents));
        Assert.Equal(4_800_000, rows.Sum(x => x.AmountCents));
    }

    /// <summary>Bolunmeyen kurus KAYBOLMAMALI; ilk taksite eklenir.</summary>
    [Fact]
    public void RemainderGoesToTheFirstInstallmentSoTheTotalIsExact()
    {
        var rows = TuitionSchedule.Build(Plan(TuitionPlanKinds.Installment, 1_000_007, count: 3));

        Assert.Equal(1_000_007, rows.Sum(x => x.AmountCents));
        Assert.Equal(333_337, rows[0].AmountCents);
        Assert.Equal(333_335, rows[1].AmountCents);
        Assert.Equal(333_335, rows[2].AmountCents);
    }

    [Fact]
    public void DownPaymentBecomesSequenceZeroAndIsNotSplit()
    {
        var rows = TuitionSchedule.Build(Plan(TuitionPlanKinds.Installment, 4_800_000, down: 800_000, count: 10));

        Assert.Equal(11, rows.Count);
        Assert.Equal(0, rows[0].Sequence);
        Assert.Equal(800_000, rows[0].AmountCents);
        Assert.Equal("Peşinat", rows[0].Note);
        Assert.Equal(4_800_000, rows.Sum(x => x.AmountCents));
        Assert.All(rows.Skip(1), row => Assert.Equal(400_000, row.AmountCents));
    }

    [Fact]
    public void MonthlyPlanRepeatsTheSameAmountEachMonth()
    {
        var rows = TuitionSchedule.Build(Plan(TuitionPlanKinds.Monthly, 400_000, count: 9));

        Assert.Equal(9, rows.Count);
        Assert.All(rows, row => Assert.Equal(400_000, row.AmountCents));
        Assert.Equal(new DateOnly(2026, 10, 5), rows[0].DueOn);
        Assert.Equal(new DateOnly(2027, 6, 5), rows[^1].DueOn);
    }

    [Fact]
    public void DailyPlanProducesNoInstallments() =>
        Assert.Empty(TuitionSchedule.Build(Plan(TuitionPlanKinds.Daily, 25_000)));

    [Fact]
    public void DueDatesWalkForwardOneMonthAtATime()
    {
        var rows = TuitionSchedule.Build(Plan(TuitionPlanKinds.Installment, 1_200_000, count: 4, dueDay: 15, month: 11));

        Assert.Equal(new DateOnly(2026, 11, 15), rows[0].DueOn);
        Assert.Equal(new DateOnly(2026, 12, 15), rows[1].DueOn);
        Assert.Equal(new DateOnly(2027, 1, 15), rows[2].DueOn);
        Assert.Equal(new DateOnly(2027, 2, 15), rows[3].DueOn);
    }

    /// <summary>Eski kayittan 31 gelirse subatta cokmemeli, ayin son gunune cekilmeli.</summary>
    [Fact]
    public void DueDayBeyondMonthLengthClampsToTheLastDay()
    {
        var plan = Plan(TuitionPlanKinds.Monthly, 100_000, count: 1, dueDay: 31, year: 2027, month: 2);

        Assert.Equal(new DateOnly(2027, 2, 28), TuitionSchedule.DueDate(plan, 0));
    }

    [Theory]
    [InlineData(100, 100, false, TuitionInstallmentStatuses.Paid)]
    [InlineData(100, 150, false, TuitionInstallmentStatuses.Paid)]
    [InlineData(100, 40, false, TuitionInstallmentStatuses.PartiallyPaid)]
    [InlineData(100, 0, false, TuitionInstallmentStatuses.Pending)]
    [InlineData(100, 0, true, TuitionInstallmentStatuses.Cancelled)]
    public void StatusBeforeTheDueDate(long amount, long paid, bool cancelled, string expected) =>
        Assert.Equal(expected, TuitionSchedule.StatusOf(amount, paid, cancelled,
            new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 1)));

    [Theory]
    [InlineData(100, 0, TuitionInstallmentStatuses.Overdue)]
    [InlineData(100, 40, TuitionInstallmentStatuses.Overdue)]
    [InlineData(100, 100, TuitionInstallmentStatuses.Paid)]
    public void StatusAfterTheDueDate(long amount, long paid, string expected) =>
        Assert.Equal(expected, TuitionSchedule.StatusOf(amount, paid, false,
            new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 20)));

    /// <summary>Iptal edilmis taksit vadesi gecse de "Gecikmiş" degildir; borc dogurmaz.</summary>
    [Fact]
    public void CancelledInstallmentNeverBecomesOverdue() =>
        Assert.Equal(TuitionInstallmentStatuses.Cancelled, TuitionSchedule.StatusOf(100, 0, true,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 1)));
}
