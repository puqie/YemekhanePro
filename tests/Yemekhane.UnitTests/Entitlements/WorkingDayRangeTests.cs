using Yemekhane.Application.Entitlements;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Kac gun" sayisindan bitis tarihi hesabi.
///
/// <para>
/// Kullanici hakedis verirken bitis TARIHI degil GUN SAYISI dusunur ("10 gunluk
/// yemek hakki"). Bitis tarihini elle bulmak icin takvime bakip hafta sonlarini
/// saymak gerekiyordu; yanlis sayilan her gun eksik ya da fazla hak olusturuyordu.
/// </para>
/// <para>
/// 2026-09-07 bir PAZARTESI; testlerdeki tarihler bu bilinen baslangica gore
/// secilmistir.
/// </para>
/// </summary>
public sealed class WorkingDayRangeTests
{
    private static readonly DateOnly Pazartesi = new(2026, 9, 7);

    [Fact]
    public void BaslangicGunuBilinenPazartesidir() =>
        Assert.Equal(DayOfWeek.Monday, Pazartesi.DayOfWeek);

    /// <summary>Tek gun istendiginde bitis, baslangicin KENDISIDIR.</summary>
    [Fact]
    public void BirGunAyniTariheBiter() =>
        Assert.Equal(Pazartesi, WorkingDayRange.EndDateFor(Pazartesi, 1, false, false));

    /// <summary>Hafta ici bes gun, ayni haftanin cumasinda biter.</summary>
    [Fact]
    public void BesGunCumaTarihindeBiter() =>
        Assert.Equal(new DateOnly(2026, 9, 11), WorkingDayRange.EndDateFor(Pazartesi, 5, false, false));

    /// <summary>
    /// On gun IKI hafta ici haftaya yayilir: hafta sonu ATLANIR ve 10 gun her zaman
    /// 10 hak demektir. Takvim gunu sayilsaydi bitis 16 Eylul olur ve yalnizca 8 hak
    /// olusurdu.
    /// </summary>
    [Fact]
    public void OnGunHaftaSonunuAtlar() =>
        Assert.Equal(new DateOnly(2026, 9, 18), WorkingDayRange.EndDateFor(Pazartesi, 10, false, false));

    /// <summary>Cumartesi dahil edilirse alti gun cumartesiye ulasir.</summary>
    [Fact]
    public void CumartesiDahilEdilinceSayimaGirer() =>
        Assert.Equal(new DateOnly(2026, 9, 12), WorkingDayRange.EndDateFor(Pazartesi, 6, true, false));

    /// <summary>Her gun dahilse sayim takvim gunuyle ayni ilerler.</summary>
    [Fact]
    public void HerGunDahilseTakvimGunuGibiIlerler() =>
        Assert.Equal(new DateOnly(2026, 9, 16), WorkingDayRange.EndDateFor(Pazartesi, 10, true, true));

    /// <summary>
    /// Baslangic dahil olmayan bir gunse ILERI KAYDIRILIR: kullanicinin pazar gunu
    /// girmesi "hak yok" degil, "ertesi is gununden basla" anlamina gelir.
    /// </summary>
    [Fact]
    public void DahilOlmayanBaslangicIleriKaydirilir()
    {
        var pazar = new DateOnly(2026, 9, 6);
        Assert.Equal(DayOfWeek.Sunday, pazar.DayOfWeek);

        // Pazar kapali: sayim pazartesiden baslar, bes gun sonra cuma biter.
        Assert.Equal(new DateOnly(2026, 9, 11), WorkingDayRange.EndDateFor(pazar, 5, false, false));
    }

    /// <summary>Gecersiz gun sayisi hesaplanamaz; cagiran bunu kullaniciya soylemelidir.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GecersizGunSayisiNullDoner(int dayCount) =>
        Assert.Null(WorkingDayRange.EndDateFor(Pazartesi, dayCount, false, false));

    /// <summary>
    /// HICBIR gun dahil degilse (hafta sonlari kapali ve... hepsi kapali olamaz ama
    /// kullanici cumartesi/pazar kapaliyken yalnizca hafta sonu isteseydi) hesap
    /// sonsuz donguye girmemeli, null donmelidir.
    /// </summary>
    [Fact]
    public void SonsuzDonguyeGirmez()
    {
        // Hafta ici her zaman dahildir, dolayisiyla gercek bir "hicbir gun" durumu
        // yoktur; yine de cok buyuk bir istek makul surede sonuclanmali.
        var result = WorkingDayRange.EndDateFor(Pazartesi, 100_000, false, false);
        Assert.Null(result);
    }

    /// <summary>
    /// Ters yon: var olan bir aralik kac gun ediyor? Mevcut kayitlarin gun sayisi
    /// olarak gosterilmesi icin gerekir.
    /// </summary>
    [Fact]
    public void AralikGunSayisinaCevrilir() =>
        Assert.Equal(10, WorkingDayRange.CountIncludedDays(
            Pazartesi, new DateOnly(2026, 9, 18), false, false));

    /// <summary>
    /// Gidip gelme TUTARLI olmalidir: N gun icin bulunan bitis, geri sayildiginda
    /// yine N vermelidir. Tutarsizlik, kullanicinin gordugu sayi ile olusan hak
    /// sayisinin ayrismasi demektir.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(45)]
    public void GidipGelmeTutarlidir(int dayCount)
    {
        var end = WorkingDayRange.EndDateFor(Pazartesi, dayCount, false, false);

        Assert.NotNull(end);
        Assert.Equal(dayCount, WorkingDayRange.CountIncludedDays(Pazartesi, end!.Value, false, false));
    }

    [Fact]
    public void BitisBaslangictanOnceyseSifirDoner() =>
        Assert.Equal(0, WorkingDayRange.CountIncludedDays(
            Pazartesi, Pazartesi.AddDays(-1), false, false));
}
