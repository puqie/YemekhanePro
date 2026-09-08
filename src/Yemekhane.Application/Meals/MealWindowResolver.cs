namespace Yemekhane.Application.Meals;

/// <summary>
/// Turnikeden gelen bir okutma icin O ANDAKI ogunu secer.
///
/// Turnike "hangi ogun" bilmez ve basinda ogunu secen bir operator yoktur; masaustu kasasi gibi
/// bir ekran da yoktur. Tek kaynak ogun tanimindaki saat penceresidir (StartsAt-EndsAt).
/// Penceresiz ogun her saat gecerlidir ama pencereli bir ogun o saati kapsiyorsa onun onune gecmez.
/// Ayni saati kapsayan birden cok pencerede secim ada gore deterministiktir; rastgele degil.
///
/// TEK aktif ogun varsa saat sorulmaz: pencere yalnizca birden cok ogun arasinda secim icindir.
/// Sahada okul "Ogle 11:30-12:30" tanimlayip 11:10'da kart okuttu; okutma sessizce dustu ve
/// "sistemde dusmedi" dendi. Tek ogunlu okulda saatin kimseyi engellemesi beklenmiyor.
/// </summary>
public static class MealWindowResolver
{
    public static MealTypeDetails? Resolve(IReadOnlyList<MealTypeDetails> mealTypes, TimeOnly localTime)
    {
        ArgumentNullException.ThrowIfNull(mealTypes);

        var active = mealTypes.Where(meal => meal.IsActive).OrderBy(meal => meal.Name, StringComparer.Ordinal).ToList();
        if (active.Count == 1) return active[0];

        MealTypeDetails? windowed = null;
        MealTypeDetails? windowless = null;
        foreach (var meal in active)
        {
            if (meal.StartsAt is not { } start || meal.EndsAt is not { } end)
            {
                windowless ??= meal;
                continue;
            }

            if (Contains(start, end, localTime)) windowed ??= meal;
        }

        return windowed ?? windowless;
    }

    /// <summary>Bitis dahildir; gece yarisini asan pencere (22:00-02:00) desteklenir.</summary>
    internal static bool Contains(TimeOnly start, TimeOnly end, TimeOnly time) =>
        start <= end
            ? time >= start && time <= end
            : time >= start || time <= end;
}
