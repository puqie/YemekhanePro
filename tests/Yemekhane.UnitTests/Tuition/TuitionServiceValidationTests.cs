using Yemekhane.Application.Common;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Plan dogrulamasi kasa borcunu belirler: hatali plan veliye yanlis borc cikarir.
/// Kurallar servis katmaninda durur ki API ve masaustu ayni mesaji gorsun.
/// </summary>
public sealed class TuitionServiceValidationTests
{
    private static SaveTuitionPlanRequest Request(
        string kind = TuitionPlanKinds.Installment,
        decimal amount = 48_000m,
        decimal down = 0m,
        int count = 10,
        int dueDay = 5,
        Guid? classId = null,
        Guid? studentId = null,
        string period = "2026-2027") =>
        new(kind, period, amount, classId ?? (studentId is null ? Guid.NewGuid() : null), studentId, down, count, dueDay,
            new DateOnly(2026, 10, 1));

    private static string Error(SaveTuitionPlanRequest request) =>
        Assert.Throws<RequestValidationException>(() => TuitionService.Validate(request)).Message;

    [Fact]
    public void ValidInstallmentRequestBecomesAPlanInCents()
    {
        var plan = TuitionService.Validate(Request(down: 8_000m));

        Assert.Equal(4_800_000, plan.AmountCents);
        Assert.Equal(800_000, plan.DownPaymentCents);
        Assert.Equal(TuitionPlanKinds.Installment, plan.Kind);
        Assert.Equal(10, plan.InstallmentCount);
        Assert.Equal(5, plan.DueDayOfMonth);
    }

    /// <summary>Plan iki kapsama birden ait olamaz; yoksa ogrenci iki kez borclanir.</summary>
    [Fact]
    public void PlanBelongsToExactlyOneScope()
    {
        Assert.Equal("Plan ya bir sınıfa ya da bir öğrenciye tanımlanmalıdır.",
            Error(Request(classId: Guid.NewGuid(), studentId: Guid.NewGuid())));
        Assert.Equal("Plan ya bir sınıfa ya da bir öğrenciye tanımlanmalıdır.",
            Error(new SaveTuitionPlanRequest(TuitionPlanKinds.Monthly, "2026-2027", 400m)));
    }

    [Fact]
    public void StudentScopedPlanIsAccepted()
    {
        var studentId = Guid.NewGuid();

        var plan = TuitionService.Validate(Request(studentId: studentId));

        Assert.Equal(studentId, plan.StudentId);
        Assert.Null(plan.ClassId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.555)]
    public void AmountMustBePositiveWithAtMostTwoDecimals(decimal amount) =>
        Assert.Equal("Tutar sıfırdan büyük ve en fazla iki ondalık basamaklı olmalıdır.", Error(Request(amount: amount)));

    [Fact]
    public void AmountHasAnUpperBound() =>
        Assert.StartsWith("Tutar en fazla", Error(Request(amount: TuitionService.MaxAmount + 1)), StringComparison.Ordinal);

    /// <summary>Pesinat toplami asarsa taksite bolunecek para kalmaz.</summary>
    [Fact]
    public void DownPaymentMustStayBelowTheTotal() =>
        Assert.Equal("Peşinat toplam ücretten küçük olmalıdır.", Error(Request(amount: 1_000m, down: 1_000m)));

    [Theory]
    [InlineData(0)]
    [InlineData(TuitionSchedule.MaxInstallmentCount + 1)]
    public void InstallmentCountIsBounded(int count) =>
        Assert.StartsWith("Taksit sayısı", Error(Request(count: count)), StringComparison.Ordinal);

    /// <summary>29-31 her ayda bulunmaz; taksit gunu 28'de siniraldi.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    public void DueDayIsBoundedToDaysEveryMonthHas(int dueDay) =>
        Assert.StartsWith("Ayın günü", Error(Request(dueDay: dueDay)), StringComparison.Ordinal);

    [Fact]
    public void MonthlyPlanRejectsADownPayment() =>
        Assert.Equal("Aylık sabit ücrette peşinat girilmez.", Error(Request(kind: TuitionPlanKinds.Monthly, down: 100m)));

    [Fact]
    public void DailyPlanRejectsInstallmentFields()
    {
        Assert.Equal("Günlük ücrette peşinat ve taksit sayısı girilmez.",
            Error(Request(kind: TuitionPlanKinds.Daily, amount: 250m, count: 10)));

        var plan = TuitionService.Validate(Request(kind: TuitionPlanKinds.Daily, amount: 250m, count: 0));

        Assert.Equal(25_000, plan.AmountCents);
        Assert.Empty(TuitionSchedule.Build(plan));
    }

    [Fact]
    public void UnknownKindIsRejected() =>
        Assert.Equal("Ücret türü Installment, Monthly veya Daily olmalıdır.", Error(Request(kind: "Yearly")));

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("2026-2027 eğitim öğretim yılı tamamı")]
    public void PeriodLengthIsChecked(string period) =>
        Assert.StartsWith("Dönem 2-20 karakter", Error(Request(period: period)), StringComparison.Ordinal);

    /// <summary>Kayitli plandan uretilen taksitlerin toplami her zaman tutari verir.</summary>
    [Fact]
    public void GeneratedInstallmentsAlwaysSumToTheTotal()
    {
        foreach (var amount in new[] { 48_000m, 1_000.07m, 999.99m, 12_345.67m })
        {
            var plan = TuitionService.Validate(Request(amount: amount, down: 0m, count: 7));
            Assert.Equal(plan.AmountCents, TuitionSchedule.Build(plan).Sum(x => x.AmountCents));
        }
    }
}
