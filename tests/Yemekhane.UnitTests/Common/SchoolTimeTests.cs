using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;

namespace Yemekhane.UnitTests.Common;

/// <summary>
/// Sunucu saat dilimi ile OKUL saati (Istanbul) karistirilmamalidir.
///
/// Sunucu UTC calisiyorsa (konteyner varsayilani) Turkiye'de gece 00:00-03:00 arasi
/// hala "dun"dur. Bu fark KALICI veriye yazildiginda sonradan duzeltmek zordur:
/// taksit plani bir ay kayar, kayit tarihi yanlis gun olur.
/// </summary>
public sealed class SchoolTimeTests
{
    /// <summary>
    /// Taksit plani baslangici OKUL gununden hesaplanir. Ayin 1'i Istanbul'da baslamisken
    /// UTC hala onceki ayin son gunudur; sunucu saatiyle plan BIR AY geriye kayardi.
    /// </summary>
    [Fact]
    public void TheTuitionPlanStartsFromTheSchoolMonthNotTheServerMonth()
    {
        // 1 Ekim 2026, Istanbul'da 01:00 -> UTC'de hala 30 Eylul 22:00.
        var schoolToday = new DateOnly(2026, 10, 1);

        var plan = TuitionService.Validate(
            new SaveTuitionPlanRequest(TuitionPlanKinds.Monthly, "2026-2027", 1000m,
                ClassId: Guid.NewGuid(), InstallmentCount: 10, DueDayOfMonth: 5),
            schoolToday);

        Assert.Equal(new DateOnly(2026, 10, 1), plan.StartsOn);
    }

    /// <summary>Acikca verilen baslangic tarihi her zaman kazanir.</summary>
    [Fact]
    public void AnExplicitStartDateWins()
    {
        var plan = TuitionService.Validate(
            new SaveTuitionPlanRequest(TuitionPlanKinds.Monthly, "2026-2027", 1000m,
                ClassId: Guid.NewGuid(), InstallmentCount: 10, DueDayOfMonth: 5,
                StartsOn: new DateOnly(2026, 9, 15)),
            new DateOnly(2026, 10, 1));

        Assert.Equal(new DateOnly(2026, 9, 15), plan.StartsOn);
    }

    /// <summary>Okul gunu verilmezse sunucu gunune duser; imza geriye donuk uyumludur.</summary>
    [Fact]
    public void OmittingTheSchoolDayFallsBackToTheServerMonth()
    {
        var plan = TuitionService.Validate(
            new SaveTuitionPlanRequest(ClassId: Guid.NewGuid(), Kind: TuitionPlanKinds.Monthly,
                Period: "2026-2027", Amount: 1000m, InstallmentCount: 10, DueDayOfMonth: 5));

        Assert.Equal(1, plan.StartsOn.Day);
    }
}
