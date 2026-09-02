using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Ogrenciler ekraninin GERCEK API'ye karsi uctan uca yolculugu: liste/sayfalama/filtreler,
/// secim ve detay sekmeleri, yeni ogrenci, duzenleme/iptal, pasiflestirme/aktiflestirme,
/// kart atama/degistirme, izin, rotalar ve yerlesim. <c>YP_LIVE_API</c> yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class StudentsJourney
{
    // ---------------------------------------------------------------- yardimcilar

    /// <summary>
    /// Yolculugu, komut govdelerinden kacan hatalari (AsyncCommand.UnhandledError) toplayarak
    /// kosar: bu hatalar gercek uygulamada kullaniciya bir iletisim kutusuyla gider; testte
    /// sessizce yutulursa "dugme calismadi" gibi gorunur ve kok neden kaybolur.
    /// </summary>
    private static void Run(Action<LiveUiHarness> journey) => LiveUiHarness.Run(ui =>
    {
        var errors = new List<string>();
        EventHandler<Exception> handler = (_, ex) => errors.Add(ex.GetType().Name + ": " + ex.Message);
        AsyncCommand.UnhandledError += handler;
        try { journey(ui); }
        finally
        {
            AsyncCommand.UnhandledError -= handler;
            foreach (var error in errors) ui.Note("KOMUT HATASI: " + error);
        }
        Assert.True(errors.Count == 0, "komut govdesinden kacan hata: " + string.Join(" | ", errors));
    });

    private static void Until(LiveUiHarness ui, Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) { ui.Delay(100); ui.Pump(); }
        ui.Pump(4);
        Assert.True(condition(), "Beklenen durum olusmadi: " + what);
    }

    private static void Reload(LiveUiHarness ui)
    {
        var vm = ui.Students;
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(1), TimeSpan.FromSeconds(20)), "liste yuklenemedi");
        ui.Pump(4);
    }

    private static StudentListItemSnapshot ApiStudent(LiveUiHarness ui, string studentNo)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/students?studentNo={studentNo}&pageSize=5");
        req.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
        var task = ui.Http.SendAsync(req);
        Assert.True(LiveUiHarness.Wait(task, TimeSpan.FromSeconds(20)));
        var body = task.Result.Content.ReadAsStringAsync();
        Assert.True(LiveUiHarness.Wait(body, TimeSpan.FromSeconds(20)));
        using var doc = JsonDocument.Parse(body.Result);
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return new(null, null, null, false, 0);
        var item = items[0];
        return new(item.GetProperty("id").GetGuid(), item.GetProperty("cardNumber").GetString(),
            item.GetProperty("firstName").GetString(), item.GetProperty("isActive").GetBoolean(),
            doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    private static int ApiCount(LiveUiHarness ui, string query)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/students?" + query + "&pageSize=1");
        req.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
        var task = ui.Http.SendAsync(req);
        Assert.True(LiveUiHarness.Wait(task, TimeSpan.FromSeconds(20)));
        var body = task.Result.Content.ReadAsStringAsync();
        Assert.True(LiveUiHarness.Wait(body, TimeSpan.FromSeconds(20)));
        using var doc = JsonDocument.Parse(body.Result);
        return doc.RootElement.TryGetProperty("totalCount", out var total) ? total.GetInt32() : -1;
    }

    private static JsonElement ApiJson(LiveUiHarness ui, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
        var task = ui.Http.SendAsync(req);
        Assert.True(LiveUiHarness.Wait(task, TimeSpan.FromSeconds(20)));
        var body = task.Result.Content.ReadAsStringAsync();
        Assert.True(LiveUiHarness.Wait(body, TimeSpan.FromSeconds(20)));
        return JsonDocument.Parse(body.Result).RootElement.Clone();
    }

    /// <summary>Sekmenin API'deki satir sayisi; URL eslemesi StudentApiClient.LoadTabAsync ile ayni.</summary>
    private static int ApiTabCount(LiveUiHarness ui, string key, Guid id)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (url, array) = key switch
        {
            "Cards" => ($"api/students/{id:D}/cards", (string?)null),
            "Parents" => ($"api/students/{id:D}/parents", null),
            "Entitlements" => ($"api/meal-entitlements/student/{id:D}?startsOn={today.AddMonths(-1):yyyy-MM-dd}&endsOn={today.AddMonths(1):yyyy-MM-dd}", null),
            "Access History" => ($"api/daily-tracking?studentId={id:D}&pageSize=100", "items"),
            "Leaves" => ($"api/leaves/student/{id:D}", null),
            "Holiday/Transfer" => ($"api/meal-transfers?studentId={id:D}", null),
            "Payments" => ($"api/income/transactions?studentId={id:D}&pageSize=100", "items"),
            "SMS History" => ($"api/sms?studentId={id:D}&pageSize=100", "items"),
            "Audit" => ($"api/audit-logs?entity=Student&entityId={id:D}&pageSize=100", "items"),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        var json = ApiJson(ui, url);
        return (array is null ? json : json.GetProperty(array)).GetArrayLength();
    }

    private sealed record StudentListItemSnapshot(Guid? Id, string? CardNumber, string? FirstName, bool IsActive, int Total);

    /// <summary>
    /// YALNIZCA Ogrenciler ekranindaki dugme: MainWindow tum ekranlari barindirir ve
    /// "Hakediş Ver" / "SMS Gönder" gibi metinler baska (gizli) ekranlarda da vardir;
    /// pencere genelinde ilk eslesme yanlis ekranin gorunmez dugmesi olabilir.
    /// </summary>
    private static Button? FindButton(LiveUiHarness ui, string content) =>
        ui.FindAll<Button>((DependencyObject)ui.Window.FindName("StudentsHost")).FirstOrDefault(b => (b.Content as string) == content);

    private static bool IsOnScreen(LiveUiHarness ui, FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToAncestor(ui.Window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (bounds.Top < 0 || bounds.Bottom > ui.Window.ActualHeight || bounds.Left < 0 || bounds.Right > ui.Window.ActualWidth) return false;
        // Bir ScrollViewer icinde gorunum alaninin disinda kaldiysa "ekranda" sayilmaz.
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is ScrollViewer sv)
            {
                var inViewport = element.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (inViewport.Top < 0 || inViewport.Bottom > sv.ViewportHeight + 0.5) return false;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    private static void Select(LiveUiHarness ui, string studentNo)
    {
        var vm = ui.Students;
        var item = vm.Students.FirstOrDefault(x => x.StudentNo == studentNo);
        Assert.NotNull(item);
        vm.OpenFullDetailCommand.Execute(item);
        Until(ui, () => vm.Details?.Id == item!.Id && vm.SelectedStudent?.Id == item.Id, "detay " + studentNo);
        var grid = ui.FindAll<DataGrid>().First(g => g.Name == "StudentsGrid");
        grid.ScrollIntoView(item!);
        ui.Pump(3);
    }

    private static void LoadTab(LiveUiHarness ui, string key)
    {
        var vm = ui.Students;
        var tab = vm.Tabs.First(t => t.Key == key);
        vm.SelectedTab = tab;
        Until(ui, () => tab.IsLoaded || tab.Error is not null, "sekme " + key);
    }

    // ---------------------------------------------------------------- 1. liste, sayfalama, filtreler

    [Fact]
    public void ListeSayfalamaVeFiltreler() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        Assert.Equal(50, vm.Students.Count);
        var activeTotal = ApiCount(ui, "isActive=true");
        Assert.Equal(activeTotal, vm.TotalCount);
        Assert.Equal($"Sayfa 1 / {(int)Math.Ceiling(activeTotal / 50.0)} • {activeTotal:N0} kayıt", vm.PageText);
        ui.Shot("students-01-liste");

        // Sayfalama
        vm.NextPageCommand.Execute(null); Until(ui, () => vm.Page == 2, "sayfa 2");
        Assert.Contains("Sayfa 2 /", vm.PageText);
        Assert.NotEqual("5001", vm.Students[0].StudentNo);
        ui.Shot("students-02-sayfa2");
        vm.PreviousPageCommand.Execute(null); Until(ui, () => vm.Page == 1, "sayfa 1");
        Assert.False(vm.PreviousPageCommand.CanExecute(null), "1. sayfada Onceki pasif olmali");

        // Genel arama (yazarken, gecikmeli): Turkce buyuk/kucuk harf ve noktali i. Her terimden
        // once liste tam hale getirilir ki bekleme onceki sonucla "kendiliginden" gecmesin;
        // beklenen sayi API'den alinir, sonuc satirlari terimle eslesmeli.
        foreach (var (term, minimum, matches) in new (string, int, Func<Yemekhane.Application.Students.StudentListItem, bool>)[]
        {
            ("ali", 40, s => s.FirstName == "ALİ"), ("ALI", 40, s => s.FirstName == "ALİ"), ("ALİ", 40, s => s.FirstName == "ALİ"),
            ("öz", 40, s => s.LastName.StartsWith("ÖZ") || s.FirstName.StartsWith("ÖZ")), ("ÖZTÜRK", 5, s => s.LastName == "ÖZTÜRK"), ("öztürk", 5, s => s.LastName == "ÖZTÜRK"),
        })
        {
            vm.Search = null;
            Until(ui, () => !vm.IsLoading && vm.TotalCount == activeTotal, "liste sifirlanmali", 6000);
            var expected = ApiCount(ui, "search=" + Uri.EscapeDataString(term) + "&isActive=true");
            Assert.True(expected >= minimum, $"API '{term}' icin {expected} dondu (en az {minimum} bekleniyordu)");
            vm.Search = term;
            Until(ui, () => !vm.IsLoading && vm.TotalCount == expected, $"arama '{term}': ekran {vm.TotalCount}, API {expected}", 6000);
            Assert.All(vm.Students, s => Assert.True(matches(s), $"'{term}' aramasinda eslesmeyen satir: {s.FirstName} {s.LastName}"));
            ui.Note($"arama '{term}': {vm.TotalCount} kayit (API {expected})");
            if (term == "öztürk") ui.Shot("students-03-arama-ozturk");
        }
        vm.Search = "a"; ui.Delay(800); ui.Pump();
        Assert.False(vm.HasError, "tek karakter arama hata uretmemeli: " + vm.ErrorMessage);
        vm.Search = null; Reload(ui);
        Assert.Equal(activeTotal, vm.TotalCount);

        // Tek tek filtreler: sonuc API ile birebir
        void Filter(Action set, string apiQuery, string label)
        {
            set(); Reload(ui);
            var expected = ApiCount(ui, apiQuery);
            Assert.True(expected > 0, $"{label}: API 0 dondu, filtre anlamsiz");
            Assert.True(vm.TotalCount == expected, $"{label}: ekran {vm.TotalCount}, API {expected}");
            ui.Note($"filtre {label}: {vm.TotalCount}");
        }
        Filter(() => vm.StudentNo = "5009", "studentNo=5009&isActive=true", "ogrenci no");
        Assert.Single(vm.Students); Assert.Equal("ÖZTÜRK", vm.Students[0].LastName);
        vm.StudentNo = null;
        Filter(() => vm.CardNumber = "8350010", "cardNumber=8350010&isActive=true", "kart no");
        Assert.Equal("5010", vm.Students[0].StudentNo);
        vm.CardNumber = null;
        Filter(() => vm.FirstName = "ali", "firstName=ali&isActive=true", "ad (kucuk harf)");
        Assert.All(vm.Students, s => Assert.Equal("ALİ", s.FirstName));
        vm.FirstName = null;
        Filter(() => vm.LastName = "öz", "lastName=%C3%B6z&isActive=true", "soyad (kucuk harf)");
        Assert.All(vm.Students, s => Assert.StartsWith("ÖZ", s.LastName));
        vm.LastName = null;
        Filter(() => vm.IsActive = false, "isActive=false", "durum pasif");
        Assert.All(vm.Students, s => Assert.False(s.IsActive));
        ui.Shot("students-04-pasifler");
        Filter(() => vm.IsActive = null, "page=1", "durum tumu");
        vm.IsActive = true;
        Filter(() => vm.ClassId = "7", "className=7&isActive=true", "sinif");
        Assert.All(vm.Students, s => Assert.StartsWith("7", s.ClassName));
        vm.ClassId = null;
        Filter(() => vm.SectionId = "E", "sectionName=E&isActive=true", "sube");
        Assert.All(vm.Students, s => Assert.Equal("E", s.SectionName));
        vm.SectionId = null;
        vm.DepartmentId = "yok"; Reload(ui);
        Assert.Equal(0, vm.TotalCount); Assert.True(vm.IsEmpty, "bos sonucta 'bulunamadi' mesaji gorunmeli");
        ui.Shot("students-05-bos-sonuc");
        vm.DepartmentId = null; Reload(ui);
    });

    // ---------------------------------------------------------------- 2. secim ve sekmeler

    [Fact]
    public void SecimFormVeSekmeler() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        Select(ui, "5009");
        Assert.Equal("5009", vm.FormStudentNo); Assert.Equal("ALİ", vm.FormFirstName); Assert.Equal("ÖZTÜRK", vm.FormLastName);
        Assert.Equal("5C", vm.SelectedStudent!.ClassName);
        Assert.Equal(10, vm.Tabs.Count);
        ui.Shot("students-10-secim-5009");

        // Duzenle dugmesi secimden sonra ETKIN olmali (CanExecuteChanged tetiklenmeli).
        Assert.True(FindButton(ui, "Düzenle")!.IsEnabled, "Duzenle secimden sonra pasif kaldi");
        Assert.True(FindButton(ui, "İzin Ver")!.IsEnabled, "Izin Ver secimden sonra pasif kaldi");

        // Her sekmenin satir sayisi API ile birebir; bos sekmede "Kayıt yok." gorunur.
        var id = vm.Details!.Id;
        foreach (var tab in vm.Tabs)
        {
            LoadTab(ui, tab.Key);
            Assert.Null(tab.Error);
            var expected = tab.Key == "General" ? 1 : ApiTabCount(ui, tab.Key, id);
            Assert.True(expected == tab.Items.Count, $"{tab.Title}: ekran {tab.Items.Count} satir, API {expected}");
            if (tab.Items.Count == 0) Assert.True(tab.IsEmpty, $"{tab.Title}: bos sekmede 'Kayıt yok.' gorunmeli");
            ui.Note($"sekme {tab.Title}: {tab.Items.Count} satir" + (tab.Items.Count > 0 ? " | " + ((Yemekhane.Desktop.Services.StudentDetailRow)tab.Items[0]).Summary : ""));
            // Sekme icerigi ham Ingilizce deger tasimamali.
            foreach (var row in tab.Items.Cast<Yemekhane.Desktop.Services.StudentDetailRow>())
                foreach (var raw in new[] { "ALLOW", "DENY", "Active", "Manual", "True", "False" })
                    Assert.DoesNotContain(raw, row.Summary);
        }
        ui.Shot("students-11-sekme-denetim");
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Entitlements"); ui.Pump(4);
        ui.Shot("students-12-sekme-hakedisler");

        // Ayni adli ikinci ALİ ÖZTÜRK: form ve sekmeler DOGRU kisiye gecmeli.
        Select(ui, "5010");
        Assert.Equal("8350010", vm.SelectedStudent!.CardNumber);
        Assert.Equal("5010", vm.Details!.StudentNo);
        LoadTab(ui, "General");
        Assert.Contains("No: 5010", ((Yemekhane.Desktop.Services.StudentDetailRow)vm.Tabs[0].Items[0]).Summary);
        LoadTab(ui, "Cards");
        Assert.Contains("8350010", ((Yemekhane.Desktop.Services.StudentDetailRow)vm.Tabs.First(t => t.Key == "Cards").Items[0]).Summary);
        ui.Shot("students-13-secim-5010");

        // Salt okunur blok (Kart No) gorunur ve dogru kisiyi gosterir.
        var cardBox = ui.FindAll<TextBox>().First(b => b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStudent.CardNumber");
        Assert.True(cardBox.IsVisible); Assert.Equal("8350010", cardBox.Text);
    });

    // ---------------------------------------------------------------- 3-5. yeni, duzenle, pasif/aktif

    [Fact]
    public void YeniDuzenlePasifAktif() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        var no = "9" + DateTime.Now.ToString("HHmmss");

        // Yeni ogrenci: bos form, zorunlu alan hatasi Turkce ve gorunur.
        Select(ui, "5001");
        vm.NewStudentCommand.Execute(null); ui.Pump(3);
        Assert.True(vm.IsFormOpen); Assert.Equal("", vm.FormStudentNo); Assert.Equal("", vm.FormFirstName);
        ui.Shot("students-20-yeni-bos");
        vm.SaveStudentCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.True(vm.HasError, "bos formda Kaydet sessiz kalmamali");
        Assert.Contains("Öğrenci NO", vm.ErrorMessage);
        ui.Shot("students-21-yeni-hata");

        // Mevcut no ile cakisma
        vm.FormStudentNo = "5001"; vm.FormFirstName = "DENEME"; vm.FormLastName = "ÖĞRENCİ";
        vm.SaveStudentCommand.Execute(null);
        Until(ui, () => vm.HasError && vm.ErrorMessage!.Contains("5001"), "cakisma mesaji: " + vm.ErrorMessage);
        Assert.True(vm.IsFormOpen, "cakismada form acik kalmali");
        ui.Shot("students-22-yeni-cakisma");

        // Basarili kayit: listede ve API'de
        vm.FormStudentNo = no; vm.FormNotes = "Canli test notu";
        vm.SaveStudentCommand.Execute(null);
        Until(ui, () => !vm.IsFormOpen && vm.Details?.StudentNo == no, "kayit sonrasi detay");
        var created = ApiStudent(ui, no);
        Assert.Equal(1, created.Total); Assert.Equal("DENEME", created.FirstName);
        Assert.False(vm.HasError, vm.ErrorMessage);
        // Kaydedilen ogrenci listede secili ve form dolu olmali (bos form = kullanici kaydin kaybolduğunu sanir).
        Assert.Equal(no, vm.FormStudentNo);
        Assert.Equal(created.Id, vm.SelectedStudent?.Id);
        Assert.True(vm.Students.Any(s => s.StudentNo == no),
            $"kaydedilen {no} listede yok; liste {vm.Students.Count} kayit, ilk: {vm.Students.FirstOrDefault()?.StudentNo}, secim {vm.SelectedStudent?.StudentNo}, sayfa {vm.PageText}");
        Assert.Equal("Canli test notu", vm.Details!.Notes);
        ui.Shot("students-23-yeni-kaydedildi");

        // Duzenle -> Iptal: degisiklik geri alinir
        vm.EditStudentCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsFormOpen);
        vm.FormFirstName = "YANLIŞ";
        Assert.True(vm.CancelEditCommand.CanExecute(null), "Iptal komutu olmali");
        vm.CancelEditCommand.Execute(null); ui.Pump();
        Assert.False(vm.IsFormOpen);
        Assert.Equal("DENEME", vm.FormFirstName);
        ui.Shot("students-24-duzenle-iptal");

        // Duzenle -> Kaydet: liste ve form guncel
        vm.EditStudentCommand.Execute(null); ui.Pump();
        vm.FormFirstName = "DENİZ";
        vm.SaveStudentCommand.Execute(null);
        Until(ui, () => !vm.IsFormOpen && vm.Details?.FirstName == "DENİZ", "duzenleme kaydi");
        Assert.Equal("DENİZ", vm.Students.First(s => s.StudentNo == no).FirstName);
        Assert.Equal("DENİZ", vm.FormFirstName);
        Assert.Equal("DENİZ", ApiStudent(ui, no).FirstName);
        ui.Shot("students-25-duzenle-kaydet");

        // Pasiflestir: islem yapilan kayit secili kalir ve rozeti Pasif olur; "Yenile"
        // (Aktif filtresi) sonrasi listeden cikar; Pasif filtresinde gorunur.
        Assert.True(vm.DeactivateCommand.CanExecute(null));
        Assert.True(FindButton(ui, "Pasifleştir")!.IsVisible);
        vm.DeactivateCommand.Execute(null);
        Until(ui, () => !vm.IsLoading && vm.Details?.IsActive == false && vm.SelectedStudent?.IsActive == false, $"pasif sonrasi durum (details={vm.Details?.IsActive}, secim={vm.SelectedStudent?.StudentNo}/{vm.SelectedStudent?.IsActive}, liste={vm.Students.Count}, hata={vm.ErrorMessage})");
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.False(ApiStudent(ui, no).IsActive);
        Assert.Equal(no, vm.SelectedStudent!.StudentNo);
        Assert.False(vm.DeactivateCommand.CanExecute(null), "pasif ogrencide Pasiflestir pasif olmali");
        Assert.True(vm.ShowActivate && !vm.ShowDeactivate);
        ui.Pump(4);
        Assert.True(FindButton(ui, "Aktifleştir")!.IsVisible, "Aktiflestir dugmesi gorunmeli");
        Assert.False(FindButton(ui, "Pasifleştir")!.IsVisible, "Pasiflestir dugmesi gizlenmeli");
        ui.Shot("students-26-pasif");
        Reload(ui);
        Assert.DoesNotContain(vm.Students, s => s.StudentNo == no);
        vm.IsActive = false; Reload(ui);
        Select(ui, no);
        Assert.False(vm.SelectedStudent!.IsActive);
        ui.Shot("students-26b-pasif-filtre");

        // Tekrar aktiflestirme yolu
        Assert.True(vm.ActivateCommand.CanExecute(null), "pasif ogrencide Aktiflestir olmali");
        vm.ActivateCommand.Execute(null);
        Until(ui, () => vm.Details?.IsActive == true && vm.SelectedStudent?.IsActive == true, "aktiflestirme: " + vm.ErrorMessage);
        Assert.True(ApiStudent(ui, no).IsActive);
        // Yeni numara (9xxxxxx) siralamada son sayfaya duser; Aktif listesinde varligi
        // numara filtresiyle dogrulanir.
        vm.IsActive = true; vm.StudentNo = no; Reload(ui);
        Assert.Single(vm.Students); Assert.True(vm.Students[0].IsActive);
        ui.Shot("students-27-aktif");
        vm.StudentNo = null; Reload(ui);

        // Mevcut ogrencide duzenleme sinif/subeyi SILMEMELI (PUT tam kayit bekler).
        Select(ui, "5010");
        Assert.Equal("8B", vm.SelectedStudent!.ClassName);
        vm.EditStudentCommand.Execute(null); ui.Pump();
        vm.FormNotes = "Sınıf korunmalı";
        vm.SaveStudentCommand.Execute(null);
        Until(ui, () => !vm.IsFormOpen && vm.Details?.Notes == "Sınıf korunmalı", "5010 not kaydi: " + vm.ErrorMessage);
        Assert.Equal("8B", vm.Students.First(s => s.StudentNo == "5010").ClassName);
        Assert.Equal("B", vm.Students.First(s => s.StudentNo == "5010").SectionName);
        Assert.NotNull(vm.Details!.ClassId);
        ui.Shot("students-28-duzenle-sinif-korunur");

        // Sil: iki adimli onay; kayit API'den de kaybolur (IsDeleted).
        vm.StudentNo = no; Reload(ui);
        Select(ui, no);
        var silButton = FindButton(ui, "Sil");
        Assert.True(silButton is not null && silButton.IsVisible && silButton.IsEnabled, "Sil dugmesi yok/pasif");
        vm.DeleteCommand.Execute(null); ui.Delay(200); ui.Pump(3);
        Assert.True(vm.IsDeleteArmed);
        Assert.Equal("Silmeyi Onayla", silButton!.Content);
        Assert.True(FindButton(ui, "Vazgeç")!.IsVisible);
        ui.Shot("students-29-sil-onay");
        vm.DeleteCommand.Execute(null);
        Until(ui, () => vm.Details is null && !vm.IsLoading && vm.HasInfo, "silme: " + vm.ErrorMessage);
        Assert.DoesNotContain(vm.Students, s => s.StudentNo == no);
        Assert.Equal(0, ApiStudent(ui, no).Total);
        Assert.Contains(no, vm.InfoMessage);
        Assert.True(vm.IsEmpty, "silinen tek kayit listeden cikinca 'bulunamadi' gorunmeli");
        ui.Shot("students-29b-silindi");
        vm.StudentNo = null; Reload(ui);
    });

    // ---------------------------------------------------------------- 6. kart

    [Fact]
    public void KartAtamaVeDegistirme() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        var suffix = DateTime.Now.ToString("HHmmss");

        // Kartsiz bir ogrenciye ilk karti ata (tohumda her 9. ogrenci kartsiz; onceki
        // kosular 5009'a kart vermis olabilir, o yuzden listeden dinamik secilir).
        var cardless = vm.Students.First(s => s.CardNumber is null).StudentNo;
        Select(ui, cardless);
        Assert.Null(vm.SelectedStudent!.CardNumber);
        Assert.Equal("Kart Ata", vm.CardActionText);
        Assert.NotNull(FindButton(ui, "Kart Ata"));
        vm.NewCardNumber = "T1" + suffix;        vm.ReplaceCardCommand.Execute(null);
        Until(ui, () => vm.Students.First(s => s.StudentNo == cardless).CardNumber == "T1" + suffix, "listede kart no: " + vm.ErrorMessage);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Equal("T1" + suffix, ApiStudent(ui, cardless).CardNumber);
        Assert.Equal(cardless, vm.SelectedStudent?.StudentNo);
        Assert.Equal("Kart Değiştir", vm.CardActionText);
        Assert.Equal("", vm.NewCardNumber);
        LoadTab(ui, "Cards");
        Assert.Contains("T1" + suffix, ((Yemekhane.Desktop.Services.StudentDetailRow)vm.Tabs.First(t => t.Key == "Cards").Items[0]).Summary);
        ui.Shot("students-30-kart-atandi");

        // Kartli ogrencide kart degistir: eski pasif, yeni aktif
        vm.NewCardNumber = "T2" + suffix;        vm.ReplaceCardCommand.Execute(null);
        Until(ui, () => vm.Students.First(s => s.StudentNo == cardless).CardNumber == "T2" + suffix, "yeni kart: " + vm.ErrorMessage);
        var cards = vm.Tabs.First(t => t.Key == "Cards");
        Until(ui, () => cards.IsLoaded && cards.Items.Count == 2, "kartlar sekmesi yenilenmeli");
        var rows = cards.Items.Cast<Yemekhane.Desktop.Services.StudentDetailRow>().Select(r => r.Summary).ToList();
        Assert.Contains(rows, r => r.Contains("T2" + suffix) && r.Contains("Aktif"));
        Assert.Contains(rows, r => r.Contains("T1" + suffix) && r.Contains("Pasif"));
        ui.Shot("students-31-kart-degisti");

        // Baska ogrencinin karti: hata mesaji
        vm.NewCardNumber = "8350010";        vm.ReplaceCardCommand.Execute(null);
        Until(ui, () => vm.HasError, "kullanilan kart hatasi");
        Assert.Contains("Kart", vm.ErrorMessage);
        ui.Shot("students-32-kart-cakisma");

        // Kart Oku modali
        vm.OpenCardWorkflowCommand.Execute(null); ui.Delay(300); ui.Pump(3);
        Assert.True(vm.IsCardWorkflowOpen);
        ui.Shot("students-33-kart-oku");
        vm.CloseCardWorkflowCommand.Execute(null); ui.Pump();
        Assert.False(vm.IsCardWorkflowOpen);
    });

    // ---------------------------------------------------------------- 7-9. izin ve rotalar

    [Fact]
    public void IzinVeRotalar() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        Select(ui, "5010");
        LoadTab(ui, "Leaves");
        var leaves = vm.Tabs.First(t => t.Key == "Leaves");
        var before = leaves.Items.Count;
        vm.LeaveStartsOn = DateTime.Today.AddDays(7); vm.LeaveEndsOn = DateTime.Today.AddDays(8);
        vm.GiveLeaveCommand.Execute(null);
        Until(ui, () => vm.Tabs.First(t => t.Key == "Leaves").IsLoaded && vm.Tabs.First(t => t.Key == "Leaves").Items.Count == before + 1, "izin sekmesinde yeni izin: " + vm.ErrorMessage);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Same(vm.Tabs.First(t => t.Key == "Leaves"), vm.SelectedTab);
        ui.Shot("students-40-izin");
        var apiLeaves = ApiJson(ui, $"api/leaves/student/{vm.Details!.Id:D}");
        Assert.Equal(before + 1, apiLeaves.GetArrayLength());

        // Hakedis Ver -> Hakedisler ekrani, cekmece dogru ogrenciyle
        Assert.True(vm.GrantEntitlementCommand.CanExecute(null), vm.GrantEntitlementReason);
        vm.GrantEntitlementCommand.Execute(null); ui.Pump(6);
        Assert.True(ui.Entitlements.IsGrantOpen, "hakedis cekmecesi acilmali");
        Assert.Equal(vm.Details!.Id.ToString("D"), ui.Entitlements.ManualStudentIds);
        ui.Shot("students-41-hakedis-rotasi");

        // SMS Gonder -> SMS ekrani dogru ogrenciyle
        ui.Navigate("students");
        Assert.True(vm.OpenSmsCommand.CanExecute(null));
        vm.OpenSmsCommand.Execute(null); ui.Delay(1500); ui.Pump(6);
        ui.Shot("students-42-sms-rotasi");
        Assert.Equal("Manual", ui.Sms.TargetType);
        // KAPSAM DISI BULGU (SMS ekrani): /api/sms/targets soyada gore ilk 100 ogrenciyi
        // dondurur; hedef ogrenci bu 100'de degilse SmsViewModel.SelectStudent onu isaretleyemez
        // ve rota "dogru ogrenciyle" acilmis olmaz. Burada yalnizca kaydedilir, dusurulmez.
        var target = ui.Sms.Students.FirstOrDefault(x => x.Id == vm.Details.Id);
        ui.Note($"SMS rotasi: hedef {vm.Details.StudentNo} listede {(target is null ? "YOK" : "var")}, secili={target?.IsSelected}, liste {ui.Sms.Students.Count} ogrenci");
    });

    // ---------------------------------------------------------------- 10. tasarim

    [Fact]
    public void Tasarim() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        Select(ui, "5010");
        LoadTab(ui, "Entitlements"); // en kalabalik sekme: yerlesimi zorlar
        ui.Pump(6);
        ui.Shot("students-50-tasarim");

        // Sutunlar kesik degil
        var grid = ui.FindAll<DataGrid>().First(g => g.Name == "StudentsGrid");
        var clipped = new List<string>();
        foreach (var cell in ui.FindAll<DataGridCell>(grid))
        {
            var text = ui.FindAll<TextBlock>(cell).FirstOrDefault();
            if (text is null || string.IsNullOrEmpty(text.Text)) continue;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var needed = text.DesiredSize.Width + cell.Padding.Left + cell.Padding.Right;
            if (cell.ActualWidth + 0.5 < needed) clipped.Add($"{cell.Column.Header}: '{text.Text}' {needed:F0}px > {cell.ActualWidth:F0}px");
        }
        Assert.True(clipped.Count == 0, "kesik hucreler: " + string.Join("; ", clipped.Distinct()));

        // Sag panel: form + eylem dugmeleri + sekmeler hepsi ekranda
        foreach (var label in new[] { "Düzenle", "Pasifleştir", "İzin Ver", "SMS Gönder", "Hakediş Ver", "Okuyucudan Al", "Kart Değiştir" })
        {
            var button = FindButton(ui, label);
            Assert.True(button is not null, $"{label} dugmesi yok");
            Assert.True(IsOnScreen(ui, button!), $"{label} dugmesi ekranda/gorunur degil");
        }
        var tabHeaders = ui.FindAll<ListBoxItem>().Concat<FrameworkElement>(ui.FindAll<TabItem>())
            .Where(i => ui.FindAll<TextBlock>(i).Any(t => t.Text is "Genel" or "Denetim")).ToList();
        Assert.True(tabHeaders.Count >= 2, "sekme basliklari bulunamadi");
        Assert.All(tabHeaders, h => Assert.True(IsOnScreen(ui, h), "sekme basligi ekranda degil"));
        var content = ui.FindAll<ListBox>().FirstOrDefault(l => l.ItemsSource == vm.SelectedTab!.Items);
        Assert.True(content is not null && content.ActualHeight >= 120, $"sekme icerigi cok kucuk: {content?.ActualHeight}");
        // Sekme siralari secime gore yer degistirmemeli: Genel her zaman ilk, Denetim son.
        var genel = tabHeaders.First(h => ui.FindAll<TextBlock>(h).Any(t => t.Text == "Genel"));
        var denetim = tabHeaders.First(h => ui.FindAll<TextBlock>(h).Any(t => t.Text == "Denetim"));
        var gTop = genel.TransformToAncestor(ui.Window).Transform(new Point(0, 0)).Y;
        vm.SelectedTab = vm.Tabs.Last(); ui.Pump(6);
        var gTopAfter = genel.TransformToAncestor(ui.Window).Transform(new Point(0, 0)).Y;
        Assert.True(Math.Abs(gTop - gTopAfter) < 1, "sekme secilince 'Genel' baslik satiri yer degistirdi");
        Assert.True(denetim.TransformToAncestor(ui.Window).Transform(new Point(0, 0)).Y >= gTop, "Denetim, Genel'in ustune cikti");
        ui.Shot("students-51-tasarim-son-sekme");
    });
}
