using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Sicil Aktar: Turkce baslikli, noktali virgullu, UTF-8 BOM'lu bir CSV (5 yeni, 2 guncelleme,
/// 3 hatali satir) onizlenir, hata raporu indirilir, "hatalilari atla" ile uygulanir ve
/// sonuc API'den dogrulanir. Ardindan hatasiz dosya, XLSX ve 10 MB ustu dosya denenir.
/// Dosya diyalogu yerine test dikisi (IFileDialogService) kullanilir.
/// </summary>
[Collection("UI")]
public class ImportJourney
{
    [Fact]
    public void CsvXlsxVeHataAkisi() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var dialogs = new StubDialogs();
        var vm = new StudentImportViewModel(new StudentImportApiClient(ui.Http, ui.Session), dialogs, ui.Permissions);
        ui.Window.StudentImportDataContext = vm;
        ui.Navigate("student-import");
        Assert.True(vm.CanImport);
        ui.Shot("import-01-bos");

        var stamp = DateTime.Now.ToString("HHmmss");
        string No(int i) => $"IMP{stamp}-{i}";
        var csv = new StringBuilder("NO;KART NO;AD;SOYAD;SINIF;TELEFON\r\n");
        csv.Append($"{No(1)};IMPK{stamp}-1;Çağrı;Şahinoğlu;5A;05321112201\r\n");
        csv.Append($"{No(2)};IMPK{stamp}-2;Öznur;Güngör;5B;05321112202\r\n");
        csv.Append($"{No(3)};IMPK{stamp}-3;Işıl;Öztürk;6A;\r\n");
        csv.Append($"{No(4)};IMPK{stamp}-4;Ümit;İnceoğlu;;05321112204\r\n");
        csv.Append($"{No(5)};IMPK{stamp}-5;Gökçe;Ağaoğlu;7C;05321112205\r\n");
        // Mevcut ogrenciler (tohum: 5001/5002) guncellenir. Kart numaralari SABIT YAZILMAZ:
        // ayni veritabaninda kosan diger yolculuklar (otomatik SMS'in kart yenileme akisi)
        // bu ogrencinin kartini degistirebiliyor. Sabit "8350001" o durumda PASIF bir kart
        // olur, satir GUNCELLEME yerine HATA sayilir ve bu yolculuk yalnizca kosu sirasina
        // bagli olarak duserdi (urun hatasi degil). Ogrencinin O ANKI aktif karti okunur.
        using var seedDb = LiveDb.Open();
        string ActiveCard(string studentNo) => seedDb.Text(
            "SELECT c.card_number FROM student_cards c JOIN students s ON s.Id = c.StudentId " +
            $"WHERE s.student_no = '{studentNo}' AND c.IsActive = 1")
            ?? throw new InvalidOperationException($"{studentNo} icin aktif kart bulunamadi");
        var card5001 = ActiveCard("5001");
        csv.Append($"5001;{card5001};GÜNCEL;ÖĞRENCİ BİR;5A;\r\n");
        csv.Append($"5002;{ActiveCard("5002")};GÜNCEL;ÖĞRENCİ İKİ;5B;\r\n");
        // Hatali satirlar: bos ad (satir 9), olmayan sinif (satir 10), baskasinin karti (satir 11).
        csv.Append($"{No(6)};IMPK{stamp}-6;;Adsız;5A;\r\n");
        csv.Append($"{No(7)};IMPK{stamp}-7;Sınıfsız;Öğrenci;13Z;\r\n");
        csv.Append($"{No(8)};8350007;Mükerrer;Kart;5A;\r\n");
        var path = Path.Combine(LiveUiHarness.ShotDir, $"sicil-{stamp}.csv");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));

        dialogs.OpenResult = path;
        vm.ChooseFileCommand.Execute(null);
        Assert.True(vm.HasFile);
        Assert.Equal(Path.GetFileName(path), vm.FileName);
        vm.PreviewCommand.Execute(null); ui.Delay(4000); ui.Pump();
        Assert.True(vm.HasPreview, "Önizleme oluşmadı: " + vm.ErrorMessage);
        Assert.Equal(10, vm.TotalCount);
        Assert.Equal(5, vm.NewCount);
        Assert.Equal(2, vm.UpdateCount);
        Assert.Equal(3, vm.ErrorCount);
        var errors = vm.Rows.Where(r => r.Errors.Count > 0).ToList();
        Assert.Equal([9, 10, 11], errors.Select(r => r.RowNumber).ToArray());
        Assert.Contains(errors[0].Errors, e => e.Message == "AD zorunludur.");
        Assert.Contains(errors[1].Errors, e => e.Message.Contains("'13Z' adlı aktif sınıf bulunamadı", StringComparison.Ordinal));
        Assert.Contains(errors[2].Errors, e => e.Message.StartsWith("KART NO veritabanında", StringComparison.Ordinal));
        Assert.Equal("Çağrı", vm.Rows[0].FirstName);
        Assert.Equal("Şahinoğlu", vm.Rows[0].LastName);
        Assert.True(vm.HasErrorRows);
        Assert.False(vm.ApplyCommand.CanExecute(null), "hatalı satır varken İçe Aktar aktif");
        ui.Shot("import-02-onizleme");
        var texts = ui.FindAll<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Yeni", texts); Assert.Contains("Güncellenecek", texts); Assert.Contains("Hatalı", texts);
        Assert.DoesNotContain(texts, t => t is "New" or "Update" or "Error");

        // Hata raporu CSV
        var report = Path.Combine(LiveUiHarness.ShotDir, $"hata-raporu-{stamp}.csv");
        dialogs.SaveResult = report;
        vm.DownloadErrorsCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(report), "hata raporu kaydedilmedi");
        var lines = File.ReadAllLines(report, Encoding.UTF8);
        Assert.Equal("Satır;NO;KART NO;AD;SOYAD;Hata Kodu;Hata", lines[0].TrimStart('﻿'));
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("9;", lines[1], StringComparison.Ordinal);
        Assert.Contains("AD zorunludur", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("10;", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("11;", lines[3], StringComparison.Ordinal);

        // Hatalilari atlayarak uygula; sonucu API'den dogrula.
        vm.ApplyValidRows = true;
        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null); ui.Delay(4000); ui.Pump();
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.Result);
        Assert.Equal(5, vm.Result!.CreatedCount);
        Assert.Equal(2, vm.Result.UpdatedCount);
        Assert.Equal(3, vm.Result.ErrorCount);
        Assert.False(vm.HasPreview);
        Assert.Contains("5 öğrenci oluşturuldu, 2 öğrenci güncellendi", vm.StatusMessage, StringComparison.Ordinal);
        ui.Shot("import-03-uygulandi");
        var imported = LiveApi.Get<PagedResult<StudentListItem>>(ui, $"api/students?search=IMP{stamp}&pageSize=50");
        Assert.Equal(5, imported.TotalCount);
        Assert.Contains(imported.Items, s => s.FirstName == "Çağrı" && s.LastName == "Şahinoğlu" && s.ClassName == "5A" && s.CardNumber == $"IMPK{stamp}-1");
        var updated = LiveApi.Get<PagedResult<StudentListItem>>(ui, "api/students?search=5001&pageSize=5");
        var s5001 = updated.Items.Single(s => s.StudentNo == "5001");
        Assert.Equal("GÜNCEL", s5001.FirstName);
        Assert.Equal("ÖĞRENCİ BİR", s5001.LastName);
        // CSV'ye YAZILAN kartla karsilastirilir; sabit "8350001" degil (bkz. ActiveCard notu).
        Assert.Equal(card5001, s5001.CardNumber);
        Assert.Equal(0, LiveApi.Get<PagedResult<StudentListItem>>(ui, $"api/students?search={No(6)}").TotalCount);

        // Bastan basla -> temiz ekran
        vm.ResetCommand.Execute(null);
        Assert.False(vm.HasFile); Assert.False(vm.HasPreview); Assert.Null(vm.StatusMessage); Assert.Null(vm.Result);

        // Hatasiz dosya: "hatalilari atla" kutusu gizli olmali.
        var clean = Path.Combine(LiveUiHarness.ShotDir, $"sicil-temiz-{stamp}.csv");
        File.WriteAllText(clean, $"NO;KART NO;AD;SOYAD\r\n{No(21)};IMPK{stamp}-21;Temiz;Bir\r\n{No(22)};IMPK{stamp}-22;Temiz;İki\r\n", new UTF8Encoding(true));
        // Onizleme/uygulama uc noktalari kullanici basina dakikada 5 istekle sinirli
        // ("expensive" politikasi); bu 6. cagri olur ve 429 ile duserdi. Pencere sifirlanana
        // kadar beklenir -- ayni kullanicinin rapor disa aktarmalari da bu sayaca dahildir.
        ui.Note("hiz siniri penceresi icin 61 sn bekleniyor");
        ui.Delay(61000);
        dialogs.OpenResult = clean;
        vm.ChooseFileCommand.Execute(null);
        vm.PreviewCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.True(vm.HasPreview, vm.ErrorMessage);
        Assert.False(vm.HasErrorRows);
        var skipBox = ui.FindAll<CheckBox>().First(c => c.Content as string == "Hatalı satırları atla, geçerli olanları aktar");
        Assert.Equal(Visibility.Collapsed, skipBox.Visibility);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        ui.Shot("import-04-hatasiz");
        vm.ApplyCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.Equal(2, vm.Result?.CreatedCount);

        // XLSX
        vm.ResetCommand.Execute(null);
        var xlsx = Path.Combine(LiveUiHarness.ShotDir, $"sicil-{stamp}.xlsx");
        WriteXlsx(xlsx, [["NO", "KART NO", "AD", "SOYAD", "SINIF"], [No(31), $"IMPK{stamp}-31", "Excel", "Öğrencisi", "8A"], [No(32), $"IMPK{stamp}-32", "", "Adsız", "8A"]]);
        dialogs.OpenResult = xlsx;
        vm.ChooseFileCommand.Execute(null);
        vm.PreviewCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.True(vm.HasPreview, "XLSX önizlemesi oluşmadı: " + vm.ErrorMessage);
        Assert.Equal(2, vm.TotalCount); Assert.Equal(1, vm.NewCount); Assert.Equal(1, vm.ErrorCount);
        Assert.Equal("Öğrencisi", vm.Rows[0].LastName);
        ui.Shot("import-05-xlsx");

        // 10 MB ustu: sunucuya gitmeden Turkce hata.
        vm.ResetCommand.Execute(null);
        var huge = Path.Combine(LiveUiHarness.ShotDir, $"sicil-buyuk-{stamp}.csv");
        using (var stream = File.Create(huge)) { stream.SetLength(10_000_001); }
        dialogs.OpenResult = huge;
        vm.ChooseFileCommand.Execute(null);
        vm.PreviewCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasPreview);
        Assert.Contains("bayt", vm.ErrorMessage, StringComparison.Ordinal);
        ui.Shot("import-06-buyuk-dosya");

        // Yanlis baslik: sunucunun Turkce mesaji ekrana ulasir.
        vm.ResetCommand.Execute(null);
        var wrong = Path.Combine(LiveUiHarness.ShotDir, $"sicil-yanlis-{stamp}.csv");
        File.WriteAllText(wrong, "NUMARA;ISIM\r\n1;Ali\r\n", new UTF8Encoding(true));
        dialogs.OpenResult = wrong;
        vm.ChooseFileCommand.Execute(null);
        vm.PreviewCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.HasPreview);
        Assert.Contains("Zorunlu başlıklar eksik", vm.ErrorMessage, StringComparison.Ordinal);
        ui.Shot("import-07-yanlis-baslik");
        File.Delete(huge);
    }, TimeSpan.FromMinutes(10));

    private static void WriteXlsx(string path, string[][] rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        for (var r = 0; r < rows.Length; r++)
        {
            var row = new Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < rows[r].Length; c++)
                row.Append(new Cell
                {
                    CellReference = $"{(char)('A' + c)}{r + 1}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(rows[r][c]))
                });
            data.Append(row);
        }
        worksheet.Worksheet = new Worksheet(data);
        workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Sicil" }));
        workbook.Workbook.Save();
    }

    private sealed class StubDialogs : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? SaveResult { get; set; }
        public string? OpenFile(string title, string filter) => OpenResult;
        public string? SaveFile(string title, string filter, string suggestedFileName) => SaveResult;
    }
}
