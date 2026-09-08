using Yemekhane.Application.Meals;

namespace Yemekhane.UnitTests.Meals;

/// <summary>
/// Turnike okutmasinda ogun secimi. Turnikenin basinda ogunu secen kimse yoktur; tek kaynak
/// ogun tanimindaki saat penceresidir.
/// </summary>
public sealed class MealWindowResolverTests
{
    private static MealTypeDetails Meal(string name, string? starts = null, string? ends = null, bool active = true) =>
        new(Guid.NewGuid(), name,
            starts is null ? null : TimeOnly.Parse(starts, System.Globalization.CultureInfo.InvariantCulture),
            ends is null ? null : TimeOnly.Parse(ends, System.Globalization.CultureInfo.InvariantCulture), active);

    [Fact]
    public void PicksTheMealWhoseWindowContainsTheTime()
    {
        var breakfast = Meal("Kahvaltı", "07:00", "09:00");
        var lunch = Meal("Öğle", "11:30", "13:30");

        Assert.Same(lunch, MealWindowResolver.Resolve([breakfast, lunch], new TimeOnly(12, 15)));
        Assert.Same(breakfast, MealWindowResolver.Resolve([breakfast, lunch], new TimeOnly(8, 0)));
    }

    [Fact]
    public void WindowBoundariesAreInclusive()
    {
        var lunch = Meal("Öğle", "11:30", "13:30");
        var dinner = Meal("Akşam", "18:00", "20:00");

        Assert.Same(lunch, MealWindowResolver.Resolve([lunch, dinner], new TimeOnly(11, 30)));
        Assert.Same(lunch, MealWindowResolver.Resolve([lunch, dinner], new TimeOnly(13, 30)));
        Assert.Null(MealWindowResolver.Resolve([lunch, dinner], new TimeOnly(13, 31)));
    }

    [Fact]
    public void WindowMayWrapPastMidnight()
    {
        var night = Meal("Gece", "22:00", "02:00");
        var lunch = Meal("Öğle", "11:30", "13:30");

        Assert.Same(night, MealWindowResolver.Resolve([night, lunch], new TimeOnly(23, 30)));
        Assert.Same(night, MealWindowResolver.Resolve([night, lunch], new TimeOnly(1, 0)));
        Assert.Null(MealWindowResolver.Resolve([night, lunch], new TimeOnly(15, 0)));
    }

    /// <summary>Birden cok ogun varken hicbir pencere uymuyorsa ogun YOK; tek ogunde ise saat sorulmaz.</summary>
    [Fact]
    public void NoMatchingWindowYieldsNullOnlyWhenThereAreSeveralMeals()
    {
        var lunch = Meal("Öğle", "11:30", "13:30");
        var dinner = Meal("Akşam", "18:00", "20:00");

        Assert.Null(MealWindowResolver.Resolve([lunch, dinner], new TimeOnly(15, 0)));
        Assert.Null(MealWindowResolver.Resolve([], new TimeOnly(12, 0)));
    }

    /// <summary>
    /// Sahada okul "Ogle 11:30-12:30" tanimlayip 11:10'da kart okuttu ve hicbir sey olmadi.
    /// Tek aktif ogun her saat gecerlidir; pencere yalnizca ogunler arasinda secim icindir.
    /// </summary>
    [Fact]
    public void SingleActiveMealIgnoresItsHours()
    {
        var lunch = Meal("Öğle", "11:30", "12:30");
        var retired = Meal("Eski Akşam", "18:00", "20:00", active: false);

        Assert.Same(lunch, MealWindowResolver.Resolve([lunch], new TimeOnly(11, 10)));
        Assert.Same(lunch, MealWindowResolver.Resolve([lunch, retired], new TimeOnly(7, 0)));
    }

    /// <summary>Saat girilmemis ogun her saat gecerlidir (tek ogunlu okul), ama pencereli ogun onun onune gecer.</summary>
    [Fact]
    public void MealWithoutHoursIsFallbackOnly()
    {
        var anytime = Meal("Yemek");
        var lunch = Meal("Öğle", "11:30", "13:30");

        Assert.Same(anytime, MealWindowResolver.Resolve([anytime], new TimeOnly(15, 0)));
        Assert.Same(lunch, MealWindowResolver.Resolve([anytime, lunch], new TimeOnly(12, 0)));
        Assert.Same(anytime, MealWindowResolver.Resolve([anytime, lunch], new TimeOnly(15, 0)));
    }

    [Fact]
    public void InactiveMealsAreNeverChosen()
    {
        var inactive = Meal("Eski Öğle", "11:30", "13:30", active: false);

        Assert.Null(MealWindowResolver.Resolve([inactive], new TimeOnly(12, 0)));
    }

    /// <summary>Cakisan pencerelerde secim ada gore deterministiktir; listedeki siraya bagli degil.</summary>
    [Fact]
    public void OverlappingWindowsAreResolvedByNameNotListOrder()
    {
        var b = Meal("B Öğün", "11:00", "14:00");
        var a = Meal("A Öğün", "12:00", "13:00");

        Assert.Same(a, MealWindowResolver.Resolve([b, a], new TimeOnly(12, 30)));
        Assert.Same(a, MealWindowResolver.Resolve([a, b], new TimeOnly(12, 30)));
    }
}
