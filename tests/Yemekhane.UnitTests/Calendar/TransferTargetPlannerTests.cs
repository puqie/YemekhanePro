using Yemekhane.Application.Calendar;

namespace Yemekhane.UnitTests.Calendar;

/// <summary>
/// Cok gunlu tatilde hak devri.
///
/// <para>
/// ONCEKI HATA: bes gunluk bir tatilde her gunun hakki AYNI "sonraki is gunune"
/// tasiniyordu; o tek gunde bes ogun hakki birikiyor, izleyen gunler bos kaliyordu.
/// Dogrusu bes gunun BES AYRI GUNE dagilmasidir.
/// </para>
/// <para>
/// Tarihler 2026 Eylul: 7'si PAZARTESI, 12-13 hafta sonu, 19-20 hafta sonu.
/// </para>
/// </summary>
public sealed class TransferTargetPlannerTests
{
    /// <summary>Hafta sonlarini atlayan, tatilleri de disarida birakan sahte takvim.</summary>
    private static Func<DateOnly, CancellationToken, Task<DateOnly?>> NextBusinessDay(
        params DateOnly[] holidays)
        => (date, _) =>
        {
            for (var offset = 1; offset <= 400; offset++)
            {
                var candidate = date.AddDays(offset);
                if (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (holidays.Contains(candidate)) continue;
                return Task.FromResult<DateOnly?>(candidate);
            }
            return Task.FromResult<DateOnly?>(null);
        };

    private static Func<DateOnly, CancellationToken, Task<bool>> Occupied(params DateOnly[] full)
        => (date, _) => Task.FromResult(full.Contains(date));

    private static readonly Func<DateOnly, CancellationToken, Task<bool>> NothingOccupied =
        (_, _) => Task.FromResult(false);

    [Fact]
    public void EylulYedisiPazartesidir() =>
        Assert.Equal(DayOfWeek.Monday, new DateOnly(2026, 9, 7).DayOfWeek);

    /// <summary>
    /// BES GUNLUK TATIL: 7-11 Eylul (Pzt-Cum) tatil edilirse bes hak, izleyen bes
    /// IS GUNUNE dagilmalidir (14-18 Eylul) -- hepsi 14'une yigilmamali.
    /// </summary>
    [Fact]
    public async Task BesGunlukTatilBesAyriGuneDagilir()
    {
        DateOnly[] tatil =
        [
            new(2026, 9, 7), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11)
        ];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, NothingOccupied, NextBusinessDay(tatil));

        Assert.Equal(
        [
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16),
            new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18)
        ], plan.Select(x => x.Target));
    }

    /// <summary>
    /// UC GUNU olana UC gun: kullanicinin kurali "5 gunu olana 5, 3 gunu olana 3".
    /// </summary>
    [Fact]
    public async Task UcGunlukTatilUcAyriGuneDagilir()
    {
        DateOnly[] tatil = [new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11)];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, NothingOccupied, NextBusinessDay(tatil));

        Assert.Equal(3, plan.Count);
        Assert.Equal(
            [new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16)],
            plan.Select(x => x.Target));
    }

    /// <summary>
    /// HEDEF GUN DOLUYSA devamina eklenir. 14'unde zaten hak varsa ilk devir 15'ine
    /// gitmeli; ustune yigilmamali.
    /// </summary>
    [Fact]
    public async Task DoluGunAtlanir()
    {
        DateOnly[] tatil = [new(2026, 9, 10), new(2026, 9, 11)];

        var plan = await TransferTargetPlanner.PlanAsync(
            tatil, Occupied(new DateOnly(2026, 9, 14)), NextBusinessDay(tatil));

        Assert.Equal(
            [new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16)],
            plan.Select(x => x.Target));
    }

    /// <summary>
    /// Art arda DOLU gunler zinciri uzatir; hak kaybolmaz, ileriye tasinir.
    /// Kullanici karari: zincirin uzunlugu sinirlandirilmaz.
    /// </summary>
    [Fact]
    public async Task ArtArdaDoluGunlerZinciriUzatir()
    {
        DateOnly[] tatil = [new(2026, 9, 11)];
        DateOnly[] dolu =
        [
            new(2026, 9, 14), new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17)
        ];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, Occupied(dolu), NextBusinessDay(tatil));

        Assert.Equal([new DateOnly(2026, 9, 18)], plan.Select(x => x.Target));
    }

    /// <summary>
    /// Devredilen gun DE tatile denk gelirse zincir devam eder (kullanici karari).
    /// 11'i tatil, 14-15 de tatil: hak 16'sina gider.
    /// </summary>
    [Fact]
    public async Task DevredilenGunDeTatilseZincirDevamEder()
    {
        DateOnly[] tatil = [new(2026, 9, 11), new(2026, 9, 14), new(2026, 9, 15)];

        var plan = await TransferTargetPlanner.PlanAsync(
            [new DateOnly(2026, 9, 11)], NothingOccupied, NextBusinessDay(tatil));

        Assert.Equal([new DateOnly(2026, 9, 16)], plan.Select(x => x.Target));
    }

    /// <summary>
    /// Hafta sonu ASLA hedef olmaz: cuma tatil edilirse hak pazartesiye gider.
    /// </summary>
    [Fact]
    public async Task HaftaSonuHedefOlmaz()
    {
        var cuma = new DateOnly(2026, 9, 11);
        Assert.Equal(DayOfWeek.Friday, cuma.DayOfWeek);

        var plan = await TransferTargetPlanner.PlanAsync([cuma], NothingOccupied, NextBusinessDay(cuma));

        Assert.Equal(DayOfWeek.Monday, plan[0].Target.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 9, 14), plan[0].Target);
    }

    /// <summary>
    /// AYNI hedefe iki hak yerlesemez. Bu, duzeltilen hatanin dogrudan korumasi:
    /// planlayici kendi yerlestirdiklerini de dolu saymalidir.
    /// </summary>
    [Fact]
    public async Task IkiHakAyniGuneYerlesmez()
    {
        DateOnly[] tatil =
        [
            new(2026, 9, 7), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11)
        ];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, NothingOccupied, NextBusinessDay(tatil));

        Assert.Equal(plan.Count, plan.Select(x => x.Target).Distinct().Count());
    }

    /// <summary>Her hedef, kendi kaynagindan SONRA olmalidir.</summary>
    [Fact]
    public async Task HedefKaynaktanSonradir()
    {
        DateOnly[] tatil = [new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11)];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, NothingOccupied, NextBusinessDay(tatil));

        Assert.All(plan, x => Assert.True(x.Target > x.Source, $"{x.Target} <= {x.Source}"));
    }

    /// <summary>
    /// Kaynaklar sirasiz gelse bile erken tarihli hak once yerlesir: tatilin ilk
    /// gunu en yakin bos gune gitmelidir.
    /// </summary>
    [Fact]
    public async Task SirasizKaynaklarTariheGoreYerlesir()
    {
        DateOnly[] tatil = [new(2026, 9, 11), new(2026, 9, 9), new(2026, 9, 10)];

        var plan = await TransferTargetPlanner.PlanAsync(tatil, NothingOccupied, NextBusinessDay(tatil));

        Assert.Equal([new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11)],
            plan.Select(x => x.Source));
        Assert.Equal([new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16)],
            plan.Select(x => x.Target));
    }

    /// <summary>Bos kaynak listesi bos plan verir; cokmez.</summary>
    [Fact]
    public async Task BosListeBosPlanVerir() =>
        Assert.Empty(await TransferTargetPlanner.PlanAsync([], NothingOccupied, NextBusinessDay()));

    /// <summary>
    /// Hedef BULUNAMAZSA o kaynak plana girmez. Cagiran eksik satiri gorup
    /// kullaniciya bildirmelidir; sessizce dusurmek HAK KAYBI demektir.
    /// </summary>
    [Fact]
    public async Task HedefBulunamazsaKaynakPlanaGirmez()
    {
        var plan = await TransferTargetPlanner.PlanAsync(
            [new DateOnly(2026, 9, 11)], NothingOccupied, (_, _) => Task.FromResult<DateOnly?>(null));

        Assert.Empty(plan);
    }
}
