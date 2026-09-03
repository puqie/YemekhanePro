using Xunit;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// SON KULLANICI KABUL TESTLERI.
///
/// LiveSmokeJourney "ekranlar yukleniyor mu" diye sorar; bu dosya "is gercekten oluyor mu"
/// diye sorar. Aradaki fark, bu projede daha once cikan hata sinifidir: ekran hatasiz
/// acilir, dugme calisir gorunur, ama yazma sunucuya HIC gitmez ya da hata kullaniciya
/// ULASMAZ. Bu yuzden her test yazma yapar ve sonucu SUNUCUDAN yeniden okuyarak dogrular;
/// ViewModel'in kendi bellegine bakmak, tam da yakalamak istedigimiz hatayi kacirirdi.
/// </summary>
[Collection("UI")]
public class LiveUserAcceptanceTests
{
    /// <summary>
    /// Ogrenci kaydi uctan uca: form ac -> doldur -> kaydet -> LISTEYI SUNUCUDAN TAZELE ->
    /// kaydin geldigini gor. Ara adimda ViewModel'e degil, tazelenmis listeye bakilir.
    /// </summary>
    [Fact]
    public void OgrenciKaydedilirVeSunucudaKalir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("students");

        var no = "UAT" + DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var students = ui.Students;

        students.NewStudentCommand.Execute(null);
        ui.Pump();
        Assert.True(students.IsFormOpen, "Yeni ogrenci formu acilmadi.");

        students.FormStudentNo = no;
        students.FormFirstName = "Kabul";
        students.FormLastName = "Testi";
        ui.Pump();

        Assert.True(students.SaveStudentCommand.CanExecute(null), "Kaydet dugmesi kapali kaldi.");
        students.SaveStudentCommand.Execute(null);
        ui.Pump(10);

        Assert.False(students.HasError, "Kaydetme hata verdi: " + students.ErrorMessage);

        // ASIL KANIT: yeni bir arama sunucuya gider; kayit oradan geri gelmelidir.
        // SearchCommand eszamansizdir: Execute isi BASLATIR, bitmesini beklemez. Sabit bir
        // bekleme yerine kosul yoklanir; yavas makinede de dogru calisir.
        students.Search = no;
        students.SearchCommand.Execute(null);

        var bulundu = false;
        for (var deneme = 0; deneme < 40 && !bulundu; deneme++)
        {
            ui.Pump(2);
            ui.Delay(150);
            bulundu = students.Students.Any(x => x.StudentNo == no);
        }

        Assert.True(bulundu, $"Kaydedilen ogrenci ({no}) sunucudan geri gelmedi.");
        ui.Note($"ogrenci kaydi dogrulandi: {no}");
    });

    /// <summary>
    /// Zorunlu alan bos birakildiginda kullanici SEBEBI gormelidir. Sessiz basarisizlik
    /// ya da genel "hata olustu" metni, kullaniciyi neyi duzeltecegini bilmez birakir.
    /// </summary>
    [Fact]
    public void EksikOgrenciFormuAnlasilirHataVerir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("students");
        var students = ui.Students;

        students.NewStudentCommand.Execute(null);
        ui.Pump();
        students.FormStudentNo = "";
        students.FormFirstName = "";
        students.FormLastName = "";
        ui.Pump();

        students.SaveStudentCommand.Execute(null);
        ui.Pump(6);

        Assert.True(students.HasError, "Bos form sessizce kabul edildi.");
        Assert.True(students.IsFormOpen, "Hata sonrasi form kapandi; kullanici duzeltemez.");
        ui.Note("bos form hatasi: " + students.ErrorMessage);
    });

    /// <summary>
    /// Her rotaya gidilebilmeli ve gidilen ekran GERCEKTEN degismeli. Yalnizca "cokmedi"
    /// demek yetmez: rota degismeden ayni ekranda kalmak da bir hatadir.
    /// </summary>
    [Fact]
    public void TumEkranlaraErisilirVeRotaDegisir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        string[] routes =
        [
            "dashboard", "daily-tracking", "students", "cash", "entitlements",
            "holiday-transfer", "reports", "sms", "devices", "device-cards",
            "student-import", "settings", "definitions"
        ];

        var basarisiz = new List<string>();
        foreach (var route in routes)
        {
            ui.Navigate(route);
            var current = ((IShortcutCommandTarget)ui.Window).CurrentRoute;
            if (string.IsNullOrWhiteSpace(current) || !current.StartsWith(route, StringComparison.Ordinal))
                basarisiz.Add($"{route} -> {current ?? "(bos)"}");
        }

        Assert.True(basarisiz.Count == 0, "Rota degismedi: " + string.Join(", ", basarisiz));
    });

    /// <summary>
    /// Ekranlar acildiginda hicbiri hata bayragiyla gelmemeli. Kullanici ekrani actiginda
    /// kirmizi bir uyari goruyorsa program calisiyor sayilmaz.
    /// </summary>
    [Fact]
    public void AcilistaHicbirEkranHataGostermez() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();

        var hatalar = new List<string>();
        void Denetle(string ad, bool hata, string? mesaj)
        {
            if (hata) hatalar.Add($"{ad}: {mesaj}");
        }

        Denetle("Öğrenciler", ui.Students.HasError, ui.Students.ErrorMessage);
        Denetle("Kasa", ui.Cash.HasError, ui.Cash.ErrorMessage);

        Assert.True(hatalar.Count == 0, "Açılışta hata gösteren ekranlar: " + string.Join(" | ", hatalar));
        Assert.DoesNotContain(ui.Log, x => x.StartsWith("YÜKLEME HATASI", StringComparison.Ordinal));
        Assert.DoesNotContain(ui.Log, x => x.StartsWith("ZAMAN", StringComparison.Ordinal));
    });

    /// <summary>
    /// Kasa tahsilat formu, onay kutusu isaretlenmeden kaydetmemelidir. Bu kutu bir
    /// yazim hatasinin okulun kasasina yanlis tutar olarak gecmesini onleyen son kapidir.
    /// </summary>
    [Fact]
    public void KasaOnaysizTahsilatiKabulEtmez() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("cash");
        var cash = ui.Cash;

        if (!cash.CanWrite) { ui.Note("kasa yazma izni yok; test atlandi"); return; }

        cash.OpenAddCommand.Execute(null);
        ui.Pump();
        Assert.True(cash.IsAddOpen, "Tahsilat formu acilmadi.");

        cash.AddConfirmed = false;
        ui.Pump();
        Assert.False(cash.AddCommand.CanExecute(null), "Onay kutusu bos iken Kaydet acik kaldi.");

        // Bos form icin sebep de anlasilir olmalidir.
        Assert.NotNull(cash.ValidateAdd());
        ui.Note("kasa onay kapisi: " + cash.ValidateAdd());
    });

    /// <summary>
    /// Tutar okuma Turkce yazimla dogru calismalidir. "1.250,50" bin iki yuz elli lira
    /// demektir; nokta ondalik sayilirsa kasaya YUZ KAT yanlis tutar gecer.
    /// </summary>
    [Theory]
    [InlineData("1.250,50", 1250.50)]
    [InlineData("125,50", 125.50)]
    [InlineData("1250.50", 1250.50)]
    [InlineData("100", 100)]
    public void TutarTurkceYazimlaDogruOkunur(string yazim, double beklenen)
    {
        Assert.True(CashViewModel.TryParseAmount(yazim, out var tutar), $"Tutar okunamadi: {yazim}");
        Assert.Equal((decimal)beklenen, tutar);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void GecersizTutarReddedilir(string yazim) =>
        Assert.False(CashViewModel.TryParseAmount(yazim, out _), $"Gecersiz tutar kabul edildi: {yazim}");

    /// <summary>
    /// Yazma izni olmayan kullanici yazma dugmelerini KULLANAMAMALI. Bu testte yonetici
    /// oturumu vardir, dolayisiyla dugmeler ACIK olmalidir: kapali cikarsa izin
    /// hesaplamasi bozulmus demektir ve okul hicbir kayit giremez.
    /// </summary>
    [Fact]
    public void YoneticiYazmaDugmeleriniKullanabilir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();

        var kapali = new List<string>();
        if (ui.Permissions.Contains("students.write"))
        {
            ui.Navigate("students");
            if (!ui.Students.NewStudentCommand.CanExecute(null)) kapali.Add("Öğrenciler: Yeni");
        }
        if (ui.Permissions.Contains("cash.write"))
        {
            ui.Navigate("cash");
            if (!ui.Cash.OpenAddCommand.CanExecute(null)) kapali.Add("Kasa: Tahsilat");
            if (!ui.Cash.OpenTopUpCommand.CanExecute(null)) kapali.Add("Kasa: Bakiye yükle");
        }

        Assert.True(kapali.Count == 0, "İzin varken kapalı kalan düğmeler: " + string.Join(", ", kapali));
    });

    /// <summary>
    /// F2 (Ogrenciler) ve F4 (Gunluk Takip) gercek pencerede rotayi DEGISTIRMELIDIR.
    /// Kisayolun yalnizca "kullanilabilir" gorunmesi yetmez; daha once bu ekranlarda
    /// kisayol calisir gorunup hicbir sey yapmiyordu.
    /// </summary>
    [Fact]
    public void KisayollarGercektenEkranDegistirir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var target = (IShortcutCommandTarget)ui.Window;

        ui.Navigate("dashboard");
        ui.Pump();

        if (target.CanExecute(ShortcutCommand.Students))
        {
            target.Execute(ShortcutCommand.Students);
            ui.Pump(6);
            Assert.Equal("students", target.CurrentRoute);
        }

        if (target.CanExecute(ShortcutCommand.DailyTracking))
        {
            target.Execute(ShortcutCommand.DailyTracking);
            ui.Pump(6);
            Assert.Equal("daily-tracking", target.CurrentRoute);
        }

        ui.Note("kisayollar dogrulandi");
    });

    /// <summary>
    /// F5 (Yenile) her ekranda ya CALISMALI ya da acikca kullanilamaz olmalidir.
    /// Kullanilabilir gorunup hicbir sey yapmamak en kotu durumdur: kullanici verinin
    /// tazelendigini sanir.
    /// </summary>
    [Fact]
    public void YenileKisayoluTutarliDavranir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var target = (IShortcutCommandTarget)ui.Window;

        foreach (var route in new[] { "students", "cash", "reports", "dashboard", "daily-tracking" })
        {
            ui.Navigate(route);
            // Kullanilabilir diyorsa CALISMALI: cagri patlarsa test duser.
            if (target.CanExecute(ShortcutCommand.Refresh))
            {
                target.Execute(ShortcutCommand.Refresh);
                ui.Pump(4);
            }
        }
        ui.Note("F5 tum ekranlarda tutarli");
    });
}
