using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Ogrenci Karti (eski programdaki "Sicil Karti") cekmecesinin GERCEK API uzerinden uctan
/// uca yolculugu: tum alanlarla ogrenci olustur -> API'den geri oku ve HER alani dogrula ->
/// "+" ile yeni sube ekle -> duzenle -> fotograf yukle -> cekmecede gorundugunu dogrula -> sil.
/// <c>YP_LIVE_API</c> yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class StudentCardJourney
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

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

    private static JsonElement ApiJson(LiveUiHarness ui, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
        var send = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(send, ApiTimeout), "API yaniti gelmedi: " + url);
        var body = send.Result.Content.ReadAsStringAsync();
        Assert.True(LiveUiHarness.Wait(body, ApiTimeout));
        Assert.True(send.Result.IsSuccessStatusCode, $"{url} -> {(int)send.Result.StatusCode}: {body.Result}");
        return JsonDocument.Parse(body.Result).RootElement.Clone();
    }

    /// <summary>Ogrenciyi numarasindan bulur; API'nin kendi listesinden okunur.</summary>
    private static JsonElement ApiStudentByNo(LiveUiHarness ui, string studentNo)
    {
        var page = ApiJson(ui, $"api/students?studentNo={studentNo}&isActive=&pageSize=5");
        var items = page.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, $"{studentNo} numarali ogrenci API'de bulunamadi");
        return items[0];
    }

    /// <summary>
    /// Listede tanim varsa ilkini secer; HIC YOKSA "+" ile olusturur. Tohumda bolum ve gorev
    /// tablolari bostur, dolayisiyla bu yol gercek kullanicinin ilk kurulumdaki akisidir.
    /// </summary>
    private static void SelectOrCreate(LiveUiHarness ui, LookupPickerViewModel picker, string nameIfMissing)
    {
        var existing = picker.Items.FirstOrDefault(x => x.Id != Guid.Empty);
        if (existing is not null) { picker.Selected = existing; return; }
        picker.OpenAddCommand.Execute(null); ui.Pump(2);
        picker.NewName = nameIfMissing;
        picker.AddCommand.Execute(null);
        Until(ui, () => !picker.IsAdding && picker.SelectedId is not null,
            $"{picker.Label} '+' ile eklenmeli: " + picker.Error);
        ui.Note($"'+' ile eklenen {picker.Label}: {nameIfMissing}");
    }

    [Fact]
    public void OgrenciKartiTumAlanlariyla() => Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("students");
        var vm = ui.Students;
        var suffix = DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var no = "8" + suffix;
        var birthDate = new DateTime(2013, 4, 5);

        // ---------------------------------------------------------- 1. cekmece acilir
        vm.NewStudentCommand.Execute(null);
        Until(ui, () => vm.IsFormOpen && vm.FormClass.IsLoaded && vm.FormSection.IsLoaded
                     && vm.FormDepartment.IsLoaded && vm.FormJob.IsLoaded, "tanim listeleri yuklenmeli");
        // Cekmece adi VIEW'in ad kapsamindadir; MainWindow.FindName onu GORMEZ (null doner).
        var drawer = ui.FindAll<Yemekhane.Desktop.Controls.Drawer>()
            .FirstOrDefault(x => x.Title == "Öğrenci Kartı");
        Assert.True(drawer is not null, "Ogrenci Karti cekmecesi bulunamadi");
        Assert.True(drawer!.IsVisible, "Ogrenci Karti cekmecesi acilmadi");
        Assert.Equal("", vm.FormStudentNo);
        // Tanim listeleri gercek veriden dolmali (tohumda 12 sinif, 5 sube).
        Assert.True(vm.FormClass.Items.Count > 1, $"sinif listesi bos: {vm.FormClass.Items.Count}");
        Assert.True(vm.FormSection.Items.Count > 1, $"sube listesi bos: {vm.FormSection.Items.Count}");
        ui.Note($"tanimlar: {vm.FormClass.Items.Count - 1} sinif, {vm.FormSection.Items.Count - 1} sube, "
              + $"{vm.FormDepartment.Items.Count - 1} bolum, {vm.FormJob.Items.Count - 1} gorev");
        ui.Shot("sicil-01-kart-bos");

        // ---------------------------------------------------------- 2. dogrulama: hatali TC
        vm.FormStudentNo = no; vm.FormFirstName = "SİCİL"; vm.FormLastName = "DENEME";
        vm.FormNationalId = "123";
        vm.SaveStudentCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.True(vm.HasError, "hatali TC sessiz kalmamali");
        Assert.Contains("11 rakam", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen, "hatada cekmece acik kalmali");
        ui.Shot("sicil-02-tc-hatasi");

        // ---------------------------------------------------------- 3. "+" ile yeni sube
        var newSection = "Z" + suffix;
        var sectionCountBefore = vm.FormSection.Items.Count;
        vm.FormSection.OpenAddCommand.Execute(null); ui.Pump(3);
        Assert.True(vm.FormSection.IsAdding, "'+' satir ici kutuyu acmali");
        ui.Shot("sicil-03-sube-ekle-kutusu");
        vm.FormSection.NewName = newSection;
        vm.FormSection.AddCommand.Execute(null);
        Until(ui, () => !vm.FormSection.IsAdding && vm.FormSection.Selected?.Name == newSection,
            "yeni sube eklenip secilmeli: " + vm.FormSection.Error);
        Assert.Null(vm.FormSection.Error);
        Assert.Equal(sectionCountBefore + 1, vm.FormSection.Items.Count);
        // Sunucuda GERCEKTEN olusmus olmali.
        var lookups = ApiJson(ui, "api/organization/sections/lookups");
        Assert.Contains(lookups.EnumerateArray(), x => x.GetProperty("name").GetString() == newSection);
        ui.Note($"'+' ile eklenen sube: {newSection}");
        ui.Shot("sicil-04-sube-eklendi");

        // ---------------------------------------------------------- 4. tum alanlari doldur ve kaydet
        vm.FormNationalId = "12345678901";
        vm.FormBirthDate = birthDate;
        vm.FormClass.Selected = vm.FormClass.Items.First(x => x.Id != Guid.Empty);
        // Tohumda bolum ve gorev tanimi YOKTUR: ikisi de "+" ile burada acilir. Bu, eski
        // programdaki "yanindaki + ile aninda yeni tanim" akisinin ta kendisidir.
        SelectOrCreate(ui, vm.FormDepartment, "Bölüm" + suffix);
        SelectOrCreate(ui, vm.FormJob, "Görev" + suffix);
        vm.FormAddress = "Atatürk Caddesi No 5\nÇankaya/ANKARA";
        vm.FormFingerprintId = "FP-" + no;
        vm.FormPid = "PI-" + no;
        vm.FormNotes = "Canlı sicil kartı testi";
        var expectedClassId = vm.FormClass.SelectedId;
        var expectedSectionId = vm.FormSection.SelectedId;
        var expectedDepartmentId = vm.FormDepartment.SelectedId;
        var expectedJobId = vm.FormJob.SelectedId;
        ui.Shot("sicil-05-kart-dolu");

        vm.SaveStudentCommand.Execute(null);
        Until(ui, () => !vm.IsFormOpen && vm.Details?.StudentNo == no, "kayit sonrasi detay: " + vm.ErrorMessage);
        Assert.False(vm.HasError, vm.ErrorMessage);
        var createdId = vm.Details!.Id;

        // ---------------------------------------------------------- 5. API'den geri oku: HER alan
        var saved = ApiJson(ui, $"api/students/{createdId:D}");
        Assert.Equal(no, saved.GetProperty("studentNo").GetString());
        Assert.Equal("SİCİL", saved.GetProperty("firstName").GetString());
        Assert.Equal("DENEME", saved.GetProperty("lastName").GetString());
        Assert.Equal("12345678901", saved.GetProperty("nationalId").GetString());
        Assert.Equal("2013-04-05", saved.GetProperty("birthDate").GetString());
        Assert.Equal(expectedClassId, saved.GetProperty("classId").GetGuid());
        Assert.Equal(expectedSectionId, saved.GetProperty("sectionId").GetGuid());
        Assert.Equal(expectedDepartmentId, saved.GetProperty("departmentId").GetGuid());
        Assert.Equal(expectedJobId, saved.GetProperty("jobId").GetGuid());
        Assert.Equal("FP-" + no, saved.GetProperty("fingerprintId").GetString());
        Assert.Equal("PI-" + no, saved.GetProperty("pid").GetString());
        Assert.Contains("Atatürk Caddesi", saved.GetProperty("address").GetString());
        Assert.Equal("Canlı sicil kartı testi", saved.GetProperty("notes").GetString());
        ui.Note($"kaydedilen ogrenci {no}: tum 13 alan API'de dogrulandi");
        // Sag panel ozetinde Bolum ve Gorev gorunmeli.
        Assert.False(string.IsNullOrWhiteSpace(vm.DetailDepartmentName), "ozette Bolum bos");
        Assert.False(string.IsNullOrWhiteSpace(vm.DetailJobName), "ozette Gorev bos");
        ui.Shot("sicil-06-kaydedildi");

        // ---------------------------------------------------------- 6. duzenle: alanlar geri gelir
        vm.EditStudentCommand.Execute(null);
        Until(ui, () => vm.IsFormOpen && vm.FormClass.SelectedId == expectedClassId, "duzenlemede sinif secili gelmeli");
        Assert.Equal("12345678901", vm.FormNationalId);
        Assert.Equal(birthDate, vm.FormBirthDate);
        Assert.Equal(expectedSectionId, vm.FormSection.SelectedId);
        Assert.Equal(expectedJobId, vm.FormJob.SelectedId);
        Assert.Equal("FP-" + no, vm.FormFingerprintId);
        Assert.Equal("PI-" + no, vm.FormPid);
        Assert.Contains("Atatürk Caddesi", vm.FormAddress);
        ui.Shot("sicil-07-duzenle-dolu");

        // ---------------------------------------------------------- 7. fotograf yukle
        var pngPath = Path.Combine(Path.GetTempPath(), $"yp-sicil-{no}.png");
        File.WriteAllBytes(pngPath, Yemekhane.UnitTests.Students.TestPng.Create(64));
        try
        {
            Assert.False(vm.HasPhoto, "yeni kayitta fotograf olmamali");
            vm.StagePhoto(pngPath); ui.Pump(3);
            Assert.True(vm.HasPhoto, "secilen fotograf onizlemede gorunmeli: " + vm.PhotoError);
            Assert.Null(vm.PhotoError);
            // Onizleme GERCEKTEN cizilmis olmali (bos bir Image kutusu degil).
            var preview = ui.FindAll<Image>().FirstOrDefault(x => x.Name == "StudentPhotoPreview");
            Assert.True(preview is not null, "fotograf onizleme kutusu bulunamadi");
            Assert.True(preview!.Source is not null, "onizleme kaynagi bos");
            ui.Shot("sicil-08-fotograf-secildi");

            vm.SaveStudentCommand.Execute(null);
            Until(ui, () => !vm.IsFormOpen && vm.Details?.PhotoPath is not null,
                "fotograf kaydedilmeli: " + vm.ErrorMessage);
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal($"photos/{createdId:D}.png", vm.Details!.PhotoPath);

            // Sunucudan gercekten indirilebilmeli.
            using var photoRequest = new HttpRequestMessage(HttpMethod.Get, $"api/students/{createdId:D}/photo");
            photoRequest.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
            var photoSend = ui.Http.SendAsync(photoRequest);
            Assert.True(LiveUiHarness.Wait(photoSend, ApiTimeout), "fotograf indirilemedi");
            Assert.True(photoSend.Result.IsSuccessStatusCode, $"fotograf GET -> {(int)photoSend.Result.StatusCode}");
            var bytes = photoSend.Result.Content.ReadAsByteArrayAsync();
            Assert.True(LiveUiHarness.Wait(bytes, ApiTimeout));
            Assert.True(bytes.Result.Length > 0, "indirilen fotograf bos");
            Assert.Equal("image/png", photoSend.Result.Content.Headers.ContentType?.MediaType);
            ui.Note($"fotograf yuklendi ve indirildi: {bytes.Result.Length} bayt");

            // Cekmeceyi tekrar acinca fotograf GORUNMELI (sunucudan indirilmis halde).
            vm.EditStudentCommand.Execute(null);
            Until(ui, () => vm.IsFormOpen && vm.HasPhoto, "kayitli fotograf cekmecede gorunmeli");
            ui.Shot("sicil-09-fotograf-cekmecede");

            // ------------------------------------------------------ 8. fotografi kaldir
            vm.RemovePhotoCommand.Execute(null); ui.Pump(3);
            Assert.False(vm.HasPhoto);
            vm.SaveStudentCommand.Execute(null);
            Until(ui, () => !vm.IsFormOpen && vm.Details?.PhotoPath is null, "fotograf silinmeli: " + vm.ErrorMessage);
            using var goneRequest = new HttpRequestMessage(HttpMethod.Get, $"api/students/{createdId:D}/photo");
            goneRequest.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
            var goneSend = ui.Http.SendAsync(goneRequest);
            Assert.True(LiveUiHarness.Wait(goneSend, ApiTimeout));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, goneSend.Result.StatusCode);
            ui.Shot("sicil-10-fotograf-kaldirildi");
        }
        finally { File.Delete(pngPath); }

        // ---------------------------------------------------------- 9. sil
        vm.StudentNo = no;
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(1), ApiTimeout), "liste yuklenemedi");
        ui.Pump(3);
        var listed = vm.Students.FirstOrDefault(x => x.StudentNo == no);
        Assert.True(listed is not null, "kaydedilen ogrenci listede yok");
        vm.OpenFullDetailCommand.Execute(listed);
        Until(ui, () => vm.Details?.Id == createdId, "silmeden once detay");
        vm.DeleteCommand.Execute(null); ui.Delay(200); ui.Pump(3);
        Assert.True(vm.IsDeleteArmed, "silme once onay istemeli");
        vm.DeleteCommand.Execute(null);
        Until(ui, () => vm.Details is null && vm.HasInfo, "silme: " + vm.ErrorMessage);
        Assert.Contains(no, vm.InfoMessage);
        var afterDelete = ApiJson(ui, $"api/students?studentNo={no}&isActive=&pageSize=5");
        Assert.Equal(0, afterDelete.GetProperty("totalCount").GetInt32());
        ui.Shot("sicil-11-silindi");

        vm.StudentNo = null;
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(1), ApiTimeout));
    });
}
