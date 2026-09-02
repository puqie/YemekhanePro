using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Detay sekmesinin YUKLENMEDI / KAYIT YOK / HATA durumlarini ayirt ettigini
/// dogrular.
///
/// Onceki davranista bos donen bir sekme bomboş beyaz alan birakiyordu;
/// kullanici verinin henuz gelmedigini mi yoksa gercekten hic kayit
/// olmadigini mi anlayamiyordu. IzInler, Tatil/Aktarim, SMS Gecmisi ve
/// Denetim sekmeleri normal kurulumda cogu zaman bostur, yani bu durum
/// istisna degil kural.
/// </summary>
public sealed class StudentDetailTabStateTests
{
    private static StudentDetailTabViewModel Tab(string key, IReadOnlyList<object> rows) =>
        new(key, () => Task.FromResult(rows));

    /// <summary>Yukleme oncesi "kayit yok" DENMEMELI -- henuz bilinmiyor.</summary>
    [Fact]
    public void YuklemedenOnceBosSayilmaz() =>
        Assert.False(Tab("Leaves", []).IsEmpty);

    /// <summary>Yukleme bitti ve hic kayit gelmediyse kullanici bunu gormeli.</summary>
    [Fact]
    public async Task YuklemeSonrasiKayitYoksaBosDurumBildirilir()
    {
        var tab = Tab("Leaves", []);
        await tab.LoadAsync();
        Assert.True(tab.IsLoaded);
        Assert.True(tab.IsEmpty);
        Assert.Equal("Kayıt yok.", tab.EmptyText);
    }

    /// <summary>Kayit varsa bos durum mesaji gorunmemeli.</summary>
    [Fact]
    public async Task KayitVarsaBosDurumGorunmez()
    {
        var tab = Tab("Payments", [new StudentDetailRow("Tutar: ₺750,00")]);
        await tab.LoadAsync();
        Assert.False(tab.IsEmpty);
        Assert.Single(tab.Items);
    }

    /// <summary>
    /// Hata durumunda "Kayıt yok." YAZILMAMALI: veri olmadigini degil
    /// alinamadigini soylemek gerekir, yoksa kullanici gercek kaydi kaybettigini sanir.
    /// </summary>
    [Fact]
    public async Task HataDurumundaBosDurumBildirilmez()
    {
        var tab = new StudentDetailTabViewModel("Payments",
            () => Task.FromException<IReadOnlyList<object>>(new HttpRequestException("bağlantı yok")));
        await tab.LoadAsync();
        Assert.NotNull(tab.Error);
        Assert.False(tab.IsEmpty);
    }

    /// <summary>
    /// KIMLIK ile GORUNEN METIN ayri olmali: Key API'ye gider (LoadTabAsync
    /// switch'i ve "Leaves" aramasi), Title yalnizca ekranda gorunur.
    /// </summary>
    [Fact]
    public void KimlikIngilizceKalirBaslikTurkcelesir()
    {
        var tab = Tab("Access History", []);
        Assert.Equal("Access History", tab.Key);
        Assert.Equal("Geçiş Geçmişi", tab.Title);
    }
}
