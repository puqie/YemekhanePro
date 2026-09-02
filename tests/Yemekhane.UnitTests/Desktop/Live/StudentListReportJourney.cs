using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using Xunit;
using Yemekhane.Application.Reports;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Sicil Listesi raporunun gercek API + 420 ogrencilik veriyle uctan uca yolculugu:
/// rapor secilir, filtrelenir, sayfalanir, CSV/Excel/PDF uretilir ve Ogrenciler ekranindaki
/// "Dışa Aktar" dugmesi buraya getirir. <c>YP_LIVE_API</c> yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class StudentListReportJourney
{
    [Fact]
    public void SicilListesiFiltreSayfalamaDisaAktarmaVeOgrencilerdenGelis() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        ui.LoadAll();
        var exports = Path.Combine(LiveUiHarness.ShotDir, "sicil-exports");
        Directory.CreateDirectory(exports);
        var dialogs = new PathDialogs(exports);
        // Harness'in ReportsViewModel'i gercek SaveFileDialog acar; ayni API istemcisiyle,
        // yolu kendimiz veren bir dialog servisiyle kurup pencereye takiyoruz.
        using var vm = new ReportsViewModel(new ReportApiClient(ui.Http, ui.Session), ui.Permissions, new MemoryLayouts(), dialogs);
        ui.Window.ReportsDataContext = vm;
        ui.Navigate("reports");
        var view = ui.FindAll<Yemekhane.Desktop.Views.ReportsView>().Single();
        var grid = ui.FindAll<DataGrid>(view).Single(x => x.Name == "ReportGrid");

        // 1) Rapor listesinde ILK sirada ve alt basligi var.
        Assert.Equal(ReportType.StudentList, vm.ReportTypes[0].Type);
        Assert.Equal("Sicil Listesi", vm.ReportTypes[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(vm.ReportTypes[0].Subtitle));
        Assert.Equal("12 canlı rapor", vm.ReportCountText);

        // Alt basliklar 178px'lik kenar listesine SIGMALI: "Detaylı: bölüm, görev, cihaz, öğün, neden"
        // ekranda "...cihaz,..." diye kirpiliyordu (canli ekran goruntusunde goruldu).
        var subtitleBlocks = ui.FindAll<TextBlock>(view).Where(x => vm.ReportTypes.Any(r => r.Subtitle == x.Text)).ToList();
        Assert.NotEmpty(subtitleBlocks);
        foreach (var block in subtitleBlocks)
        {
            var measured = new FormattedText(block.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
                Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            Assert.True(measured.Width <= block.ActualWidth + 1.5,
                $"rapor alt başlığı kırpılıyor: '{block.Text}' {measured.Width:0}px > {block.ActualWidth:0}px");
        }

        vm.SelectedReport = vm.ReportTypes[0];
        Journey.Until(ui, () => !vm.IsLoading, "Sicil Listesi seçimi");
        Journey.Run(ui, vm.ApplyAsync(), "Sicil Listesi");
        Assert.Null(vm.ErrorMessage);

        // 2) Toplam SQLite ile birebir; tarih filtresi (bugun) YOK SAYILIR.
        var total = db.Count("SELECT COUNT(*) FROM students WHERE IsDeleted = 0");
        var active = db.Count("SELECT COUNT(*) FROM students WHERE IsDeleted = 0 AND IsActive = 1");
        Assert.True(total > 400, $"tohumda {total} öğrenci var, sayfalama denemesi için yetersiz");
        Assert.Equal(total, vm.Summary.TotalRecords);
        Assert.Equal(active, vm.Summary.TotalMeals);
        Assert.Contains("Aktif", vm.SummaryText);
        Assert.Contains(total.ToString("N0", CultureInfo.GetCultureInfo("tr-TR")), vm.SummaryText);
        // Ekranda tarih kutulari gizli, nedeni yaziyor.
        Assert.False(vm.ShowDateFilters);
        Assert.True(vm.ShowDateNote);
        Assert.Contains(Journey.TextsIn(ui, view), x => x.Contains("Tarih filtresi uygulanmaz", StringComparison.Ordinal));
        ui.Note($"Sicil Listesi: {total} öğrenci, {active} aktif");

        // 3) Sutunlar ve 1440px yerlesimi: yatay kaydirma yok, kesik hucre yok.
        // VELİ (ad) varsayilan GIZLI: dokuz sutun 1440px'e sigmiyor, veli adi sutunu
        // eklendiginde diger sutunlar alt sinira ezilip metinleri kesiliyordu. Veli
        // telefonu gorunur kalir; veli adi Kolonlar menusunden acilabilir.
        Assert.Equal(["NO", "AD SOYAD", "SINIF", "ŞUBE", "KART NO", "VELİ TEL", "DURUM", "KAYIT"],
            grid.Columns.Select(x => (string)x.Header));
        Assert.Contains(vm.Columns, c => c.Header == "VELİ" && !c.IsVisible);
        var scroll = ui.FindAll<ScrollViewer>(grid).FirstOrDefault();
        Assert.False(scroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
            $"Sicil Listesi: 1440px'te yatay kaydırma (sütun toplamı {grid.Columns.Sum(c => c.ActualWidth):0}px, tablo {grid.ActualWidth:0}px)");
        var clipped = Journey.ClippedCells(ui, grid);
        Assert.True(clipped.Count == 0, "kesik hücre(ler): " + string.Join(" | ", clipped.Take(5)));
        // Ham kod ekranda gorunmez: ACTIVE/INACTIVE Turkcelesir.
        var texts = Journey.TextsIn(ui, grid).ToList();
        Assert.DoesNotContain(texts, x => x is "ACTIVE" or "INACTIVE");
        Assert.Contains(texts, x => x == "Aktif");
        ui.Shot("rapor-sicil-01-liste");

        // 4) Siralama sinif > sube > no (artan), sayfa boyutu kadar satir.
        Assert.Equal(Math.Min(vm.PageSize, total), vm.Rows.Count);
        var keys = vm.Rows.Select(x => (x.Class, x.Section, x.StudentNo)).ToList();
        Assert.Equal(keys.OrderBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.Section, StringComparer.Ordinal)
            .ThenBy(x => x.StudentNo, StringComparer.Ordinal), keys);

        // 5) Sayfalama: 400+ kayit sayfalanir.
        Assert.Equal($"Sayfa 1 / {(int)Math.Ceiling(total / 50d)}", vm.PageText);
        var firstOnPage1 = vm.Rows[0].Source.Id;
        vm.NextPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.Page == 2, "sonraki sayfa");
        Assert.NotEqual(firstOnPage1, vm.Rows[0].Source.Id);
        ui.Shot("rapor-sicil-02-sayfa2");
        vm.PreviousPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.Page == 1, "önceki sayfa");
        Assert.Equal(firstOnPage1, vm.Rows[0].Source.Id);

        // 6) Filtreler tek tek; her biri SQLite ile ayni sayiyi vermeli.
        var cases = new (string Name, Action Set, string Sql)[]
        {
            ("sınıf", () => vm.ClassName = "5A", "c.Name = '5A'"),
            ("öğrenci no", () => vm.StudentNo = "5001", "s.student_no LIKE '%5001%'"),
            ("ad", () => vm.FirstName = "ADA", "s.FirstName LIKE '%ADA%'"),
            ("soyad", () => vm.LastName = "YILMAZ", "s.LastName LIKE '%YILMAZ%'"),
            ("durum pasif", () => vm.SelectedActiveState = vm.ActiveStates.Single(x => x.Name == "Pasif"), "s.IsActive = 0"),
            ("durum aktif", () => vm.SelectedActiveState = vm.ActiveStates.Single(x => x.Name == "Aktif"), "s.IsActive = 1"),
        };
        foreach (var (name, set, sql) in cases)
        {
            vm.ResetCommand.Execute(null);
            Journey.Until(ui, () => !vm.IsLoading && vm.ClassName is null && vm.StudentNo is null, "sıfırla");
            set();
            Journey.Run(ui, vm.ApplyAsync(), "filtre " + name);
            var expected = db.Count("SELECT COUNT(*) FROM students s LEFT JOIN classes c ON c.Id = s.ClassId " +
                                    $"WHERE s.IsDeleted = 0 AND {sql}");
            Assert.True(expected == vm.Summary.TotalRecords,
                $"filtre {name}: SQLite {expected}, ekran {vm.Summary.TotalRecords}");
            Assert.True(expected > 0, $"filtre {name}: tohumda hiç kayıt yok, test bir şey kanıtlamaz");
            ui.Note($"sicil filtre {name}: {expected} kayıt");
        }
        ui.Shot("rapor-sicil-03-filtre");

        // "Aktif" filtresi PASIF ogrenci getirmemeli (ACTIVE, INACTIVE'in icinde geciyordu).
        Assert.DoesNotContain(vm.Rows, x => x.Status == "Pasif");

        vm.ResetCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.ClassName is null, "sıfırla");
        Journey.Run(ui, vm.ApplyAsync(), "tüm sicil");

        // 7) Disa aktarma: CSV / Excel / PDF -- baslıklar Turkce, satir sayisi toplamla ayni.
        var csv = Export(ui, vm, vm.ExportCsvCommand, dialogs);
        var bytes = File.ReadAllBytes(csv);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV UTF-8 BOM yok");
        var lines = File.ReadAllLines(csv, Encoding.UTF8);
        Assert.Equal(total + 1, lines.Length);
        foreach (var header in new[] { "Öğrenci No", "Ad", "Soyad", "Sınıf", "Şube", "Kart No", "Veli", "Veli Telefonu", "Durum", "Kayıt Tarihi" })
            Assert.Contains($"\"{header}\"", lines[0]);
        // Ham kod dosyaya sizmaz.
        Assert.DoesNotContain(lines, x => x.Contains("\"ACTIVE\"", StringComparison.Ordinal));
        Assert.Contains(lines.Skip(1), x => x.Contains("\"Aktif\"", StringComparison.Ordinal));
        ui.Note("Sicil CSV ilk veri satırı: " + lines[1]);

        var xlsx = Export(ui, vm, vm.ExportExcelCommand, dialogs);
        using (var document = SpreadsheetDocument.Open(xlsx, false))
        {
            var sheet = document.WorkbookPart!.WorksheetParts.First().Worksheet;
            var rows = sheet.Descendants<Row>().ToList();
            var header = rows.Single(x => x.RowIndex!.Value == 5).Elements<Cell>().Select(CellText).ToList();
            Assert.Contains("Veli Telefonu", header);
            Assert.Contains("Kayıt Tarihi", header);
            Assert.Equal(total, rows.Count(x => x.RowIndex!.Value >= 6 && CellText(x.Elements<Cell>().First()) != "Toplam"));
        }

        var pdf = Export(ui, vm, vm.ExportPdfCommand, dialogs);
        using (var document = PdfDocument.Open(pdf))
        {
            var page = document.GetPage(1);
            Assert.True(page.Width > page.Height, "Sicil Listesi 12 sütun: yatay sayfa bekleniyor");
            var text = string.Join(" ", page.GetWords().Select(x => x.Text));
            Assert.Contains("Sicil Listesi", text);
            Assert.Contains("Toplam öğrenci", text);
            // Gecis raporlarinin ozeti burada anlamsizdir.
            Assert.DoesNotContain("Geçen:", text);
        }
        ui.Shot("rapor-sicil-04-disa-aktarma");

        // 8) Ogrenciler ekranindaki "Dışa Aktar": Raporlar'a Sicil Listesi secili getirir.
        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.DailyAccess);
        Journey.Until(ui, () => !vm.IsLoading, "Günlük Geçiş");
        ui.Navigate("students");
        var studentsView = ui.FindAll<Yemekhane.Desktop.Views.StudentsView>().Single();
        var exportButton = ui.FindAll<Button>(studentsView).Single(x => (x.Content as string) == "Dışa Aktar");
        Assert.True(exportButton.IsVisible || ui.Students.CanExport, "Dışa Aktar düğmesi görünmüyor");
        exportButton.Command.Execute(null);
        ui.Pump(6);
        Journey.Until(ui, () => vm.SelectedReport.Type == ReportType.StudentList, "Sicil Listesi'ne yönlendirme");
        Assert.Equal("reports", Journey.Route(ui));
        Journey.Until(ui, () => !vm.IsLoading, "sicil yüklendi");
        Assert.Equal(total, vm.Summary.TotalRecords);
        ui.Shot("rapor-sicil-05-ogrencilerden");
    });

    private static string Export(LiveUiHarness ui, ReportsViewModel vm, System.Windows.Input.ICommand command, PathDialogs dialogs)
    {
        dialogs.LastPath = null;
        command.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && (vm.StatusMessage?.Contains("kaydedildi", StringComparison.Ordinal) == true || vm.ErrorMessage is not null), "dışa aktarma", 120000);
        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(dialogs.LastPath!), "dosya yok: " + dialogs.LastPath);
        Assert.True(new FileInfo(dialogs.LastPath!).Length > 0);
        return dialogs.LastPath!;
    }

    private static string CellText(Cell cell) => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? "";

    private sealed class PathDialogs(string directory) : IReportDialogService
    {
        public string? LastPath { get; set; }
        public string Copied { get; private set; } = "";
        public string? ChoosePath(ReportType type, ReportExportFormat format) => LastPath = Path.Combine(directory,
            $"{type}.{format switch { ReportExportFormat.Pdf => "pdf", ReportExportFormat.Excel => "xlsx", _ => "csv" }}");
        public void CopyText(string value) => Copied = value;
    }

    private sealed class MemoryLayouts : IReportLayoutStore
    {
        public IReadOnlyList<ReportColumnLayout> Load(ReportType type) => [];
        public void Save(ReportType type, IReadOnlyList<ReportColumnLayout> columns) { }
    }
}
