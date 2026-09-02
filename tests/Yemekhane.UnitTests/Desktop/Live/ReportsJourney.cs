using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
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

/// <summary>Canli yolculuklarin ortak bekleme / gecis yardimcilari.</summary>
internal static class Journey
{
    public const string Today = "2026-09-02";
    public static readonly DateTime TodayDate = new(2026, 9, 2);

    public static void Until(LiveUiHarness ui, Func<bool> condition, string what, int timeoutMs = 30000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs) { ui.Delay(100); ui.Pump(2); }
        Assert.True(condition(), "Zaman aşımı: " + what);
    }

    public static void Run(LiveUiHarness ui, Task task, string what)
    {
        Assert.True(LiveUiHarness.Wait(task, TimeSpan.FromSeconds(60)), "Zaman aşımı: " + what);
        if (task.IsFaulted) throw task.Exception!.GetBaseException();
        ui.Pump(3);
    }

    public static string Route(LiveUiHarness ui) => ((IShortcutCommandTarget)ui.Window).CurrentRoute;

    /// <summary>
    /// Dugme marka rengiyle (AccentBrush, turuncu) mi boyanmis? Her View DesignSystem.xaml'i kendi
    /// sozlugune ayri ayri birlestirdigi icin Style nesneleri ayni ORNEK degildir; renk karsilastirilir.
    /// </summary>
    public static bool IsPrimary(LiveUiHarness ui, Button button)
    {
        var accent = ((SolidColorBrush)ui.Window.TryFindResource("AccentBrush")).Color;
        return button.Background is SolidColorBrush brush && brush.Color == accent;
    }

    /// <summary>Gercek gecis: cihaz anahtariyla POST /api/access/check. OperationId dondurur.</summary>
    public static (Guid OperationId, string Decision) SimulateAccess(LiveUiHarness ui, string cardNumber, Guid deviceId, Guid mealTypeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/access/check")
        {
            Content = JsonContent.Create(new
            {
                cardNumber, deviceId, mealTypeId, timestamp = DateTimeOffset.Now,
                direction = "Entry", readerSource = "Device"
            })
        };
        request.Headers.Add("X-Device-Key", Environment.GetEnvironmentVariable("YP_DEVICE_KEY") ?? "test-cihaz-anahtari-1234567890");
        var send = ui.Http.SendAsync(request);
        Run(ui, send, "access/check");
        Assert.True(send.Result.IsSuccessStatusCode, "access/check " + send.Result.StatusCode);
        var read = send.Result.Content.ReadFromJsonAsync<AccessReply>();
        Run(ui, read, "access/check govde");
        return (read.Result!.OperationId, read.Result.Decision);
    }

    private sealed record AccessReply(Guid OperationId, string Decision);

    /// <summary>Bugun hakki henuz kullanilmamis aktif bir ogrencinin karti; yolculuk tekrarlarinda cakismaz.</summary>
    public static string UnusedCard(LiveDb db, bool descending) => db.Text(
        "SELECT c.card_number FROM student_cards c JOIN students s ON s.Id = c.StudentId " +
        "JOIN meal_entitlements e ON e.StudentId = s.Id AND e.EntitlementDate = '" + Today + "' AND e.Status = 'Active' AND e.ConsumedQuantity = 0 " +
        "WHERE s.IsActive = 1 AND s.IsDeleted = 0 AND c.IsActive = 1 ORDER BY c.card_number " + (descending ? "DESC" : "ASC") + " LIMIT 1")
        ?? throw new InvalidOperationException("Bugün hakkı kullanılmamış kart kalmadı.");

    public static IEnumerable<string> TextsIn(LiveUiHarness ui, DependencyObject root) =>
        ui.FindAll<TextBlock>(root).Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// Gorunen hucrelerde olculen metin genisligi hucreden buyukse kesilmis demektir.
    /// Yildiz (serbest metin) sutunlar sinirsiz uzunlukta olabilir; onlar not edilir, hata sayilmaz
    /// (hucre ipucu tam metni gosterir).
    /// </summary>
    public static List<string> ClippedCells(LiveUiHarness ui, DataGrid grid)
    {
        var clipped = new List<string>();
        foreach (var cell in ui.FindAll<DataGridCell>(grid))
        {
            var text = ui.FindAll<TextBlock>(cell).FirstOrDefault();
            if (text is null || string.IsNullOrEmpty(text.Text)) continue;
            if (cell.Column.Width.IsStar) continue;
            var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            // TextBlock kendi icerigine gore olculur; kirpan sey HUCREdir (dolgu dusuldukten sonra kalan yer).
            var available = cell.ActualWidth - cell.Padding.Left - cell.Padding.Right - cell.BorderThickness.Left - cell.BorderThickness.Right;
            if (formatted.Width > available + 1.5) clipped.Add($"'{text.Text}' {formatted.Width:0}px > {available:0}px");
        }
        return clipped;
    }
}

[Collection("UI")]
public class ReportsJourney
{
    private static readonly DateTime SeptemberStart = new(2026, 9, 1);
    private static readonly DateTime SeptemberEnd = new(2026, 9, 30);

    [Fact]
    public void OnBirRaporVeriFiltreSiralamaSayfalamaDisaAktarma() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        ui.LoadAll();
        var exports = Path.Combine(LiveUiHarness.ShotDir, "exports");
        Directory.CreateDirectory(exports);
        var dialogs = new PathDialogs(exports);
        // Harness'in ReportsViewModel'i gercek SaveFileDialog acar; ayni API istemcisiyle,
        // yolu kendimiz veren bir dialog servisiyle kurup pencereye takiyoruz.
        using var vm = new ReportsViewModel(new ReportApiClient(ui.Http, ui.Session), ui.Permissions, new MemoryLayouts(), dialogs);
        ui.Window.ReportsDataContext = vm;
        ui.Navigate("reports");
        var view = ui.FindAll<Yemekhane.Desktop.Views.ReportsView>().Single();
        var grid = ui.FindAll<DataGrid>(view).Single(x => x.Name == "ReportGrid");
        var filterHeights = new Dictionary<string, double>();

        // 1) Her rapor turu: bugun + Eylul; ozet satiri SQLite ile birebir; tasarim kontrolleri.
        foreach (var option in vm.ReportTypes)
        {
            vm.StartDate = Journey.TodayDate; vm.EndDate = Journey.TodayDate;
            vm.SelectedReport = option;                       // farkli turde ApplyAsync'i kendisi tetikler
            Journey.Until(ui, () => !vm.IsLoading, option.Name + " seçim");
            Journey.Run(ui, vm.ApplyAsync(), option.Name + " bugün");
            Assert.Null(vm.ErrorMessage);
            AssertSummary(db, vm, option.Type, Journey.Today, Journey.Today, option.Name + " bugün");
            ui.Shot($"reports-{option.Type}-bugun");
            if (vm.Summary.TotalRecords == 0)
            {
                var empty = ui.FindAll<TextBlock>(view).Single(x => x.Text.StartsWith("Bu filtrelerle", StringComparison.Ordinal));
                Assert.True(empty.IsVisible, option.Name + ": boş rapor mesajı görünmüyor");
            }

            vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
            Journey.Run(ui, vm.ApplyAsync(), option.Name + " Eylül");
            AssertSummary(db, vm, option.Type, "2026-09-01", "2026-09-30", option.Name + " Eylül");
            Assert.Equal(Math.Min(vm.PageSize, vm.Summary.TotalRecords), vm.Rows.Count);
            Assert.Equal(vm.Rows.Count, grid.Items.Count);
            ui.Shot($"reports-{option.Type}-eylul");

            var scroll = ui.FindAll<ScrollViewer>(grid).FirstOrDefault();
            Assert.False(scroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
                $"{option.Name}: 1440px'te yatay kaydırma çıkıyor (sütun toplamı {grid.Columns.Sum(c => c.ActualWidth):0}px, tablo {grid.ActualWidth:0}px)");
            var clipped = Journey.ClippedCells(ui, grid);
            Assert.True(clipped.Count == 0, $"{option.Name}: kesik hücre(ler): {string.Join(" | ", clipped.Take(5))}");
            var texts = Journey.TextsIn(ui, grid).ToList();
            Assert.DoesNotContain(texts, x => x is "OK" or "ALLOW" or "DENY" or "ACTIVE" or "VOIDED" or "USED" or "OPEN" or "TIMEOUT" or "Entry / Device");
            var filters = ui.FindAll<WrapPanel>(view).First();
            filterHeights[option.Name] = filters.ActualHeight;
            ui.Note($"{option.Name}: Eylül toplam {vm.Summary.TotalRecords}, sütun toplamı {grid.Columns.Sum(c => c.ActualWidth):0}px / grid {grid.ActualWidth:0}px, filtre paneli {filters.ActualHeight:0}px");
        }
        ui.Note("Filtre paneli yükseklikleri: " + string.Join(", ", filterHeights.Select(x => $"{x.Key}={x.Value:0}")));

        // Sayisal sutunlar saga yasli, Uygula turuncu (Primary).
        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.Income);
        Journey.Until(ui, () => !vm.IsLoading, "Gelir");
        vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
        Journey.Run(ui, vm.ApplyAsync(), "Gelir Eylül");
        var amountHeader = ui.FindAll<DataGridColumnHeader>(grid).Single(x => (x.Content as string) == "TUTAR");
        Assert.Equal(HorizontalAlignment.Right, amountHeader.HorizontalContentAlignment);
        var amountCells = ui.FindAll<DataGridCell>(grid).Where(x => (x.Column.Header as string) == "TUTAR").Take(3).ToList();
        Assert.NotEmpty(amountCells);
        Assert.All(amountCells, cell => Assert.Equal(TextAlignment.Right, ui.FindAll<TextBlock>(cell).First().TextAlignment));
        Assert.All(amountCells, cell => Assert.Contains("₺", ui.FindAll<TextBlock>(cell).First().Text));
        var apply = ui.FindAll<Button>(view).Single(x => (x.Content as string) == "Uygula");
        Assert.True(Journey.IsPrimary(ui, apply), "Uygula düğmesi turuncu (Primary) değil");

        // 2) Filtreler tek tek (Gunluk Gecis, Eylul) -- SQLite ile ayni sayi.
        vm.SelectedReport = vm.ReportTypes[0];
        Journey.Until(ui, () => !vm.IsLoading, "Günlük Geçiş");
        var range = LiveDb.Range("a.Timestamp", "2026-09-01", "2026-09-30");
        var from = "FROM access_logs a JOIN devices d ON d.Id = a.DeviceId LEFT JOIN students s ON s.Id = a.StudentId AND s.IsDeleted = 0 " +
                   "LEFT JOIN classes c ON c.Id = s.ClassId LEFT JOIN sections sec ON sec.Id = s.SectionId LEFT JOIN meal_types m ON m.Id = a.MealTypeId WHERE " + range;
        var cases = new (string Name, Action Set, string Sql)[]
        {
            ("öğrenci no", () => vm.StudentNo = "5001", "s.student_no IS NOT NULL AND instr(s.student_no, '5001') > 0"),
            ("kart", () => vm.CardNo = "83500", "instr(a.CardNumber, '83500') > 0"),
            ("ad", () => vm.FirstName = "ADA", "s.FirstName IS NOT NULL AND instr(s.FirstName, 'ADA') > 0"),
            ("soyad", () => vm.LastName = "YILMAZ", "s.LastName IS NOT NULL AND instr(s.LastName, 'YILMAZ') > 0"),
            ("sınıf", () => vm.ClassName = "5A", "c.Name IS NOT NULL AND instr(c.Name, '5A') > 0"),
            ("şube", () => vm.Section = "A", "sec.Name IS NOT NULL AND instr(sec.Name, 'A') > 0"),
            ("bölüm", () => vm.Department = "Fen", "0"),
            ("görev", () => vm.Job = "Öğretmen", "0"),
            ("öğün", () => vm.MealType = "Öğle", "m.Name IS NOT NULL AND instr(m.Name, 'Öğle') > 0"),
            ("cihaz", () => vm.Device = "Kantin", "instr(d.Name, 'Kantin') > 0"),
            ("karar", () => vm.Decision = "DENY", "a.Decision = 'DENY'"),
            ("durum", () => vm.Status = "Kart pasif", "instr(a.Reason, 'Kart pasif') > 0"),
        };
        foreach (var (name, set, sql) in cases)
        {
            vm.ResetCommand.Execute(null);
            Journey.Until(ui, () => !vm.IsLoading && vm.StudentNo is null && vm.Status is null, "sıfırla");
            vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
            set();
            Journey.Run(ui, vm.ApplyAsync(), "filtre " + name);
            var expected = db.Count($"SELECT COUNT(*) {from} AND {sql}");
            Assert.True(expected == vm.Summary.TotalRecords, $"filtre {name}: SQLite {expected}, ekran {vm.Summary.TotalRecords}");
            ui.Note($"filtre {name}: {expected} kayıt");
        }
        ui.Shot("reports-filtre-durum");
        vm.ResetCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.Status is null, "sıfırla");
        Assert.Equal(Journey.TodayDate, vm.StartDate);
        Assert.Equal(Journey.TodayDate, vm.EndDate);

        // 3) Siralama: ilk tiklama artan, ikinci azalan (ReportsView.ReportGrid_Sorting -> SortAsync).
        vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
        Journey.Run(ui, vm.ApplyAsync(), "Eylül");
        Journey.Run(ui, vm.SortAsync("studentNo"), "sırala artan");
        var numbers = vm.Rows.Select(x => x.StudentNo).ToList();
        Assert.Equal(numbers.OrderBy(x => x, StringComparer.Ordinal), numbers);
        Journey.Run(ui, vm.SortAsync("studentNo"), "sırala azalan");
        numbers = vm.Rows.Select(x => x.StudentNo).ToList();
        Assert.Equal(numbers.OrderByDescending(x => x, StringComparer.Ordinal), numbers);
        ui.Shot("reports-siralama-azalan");

        // 4) Sayfalama.
        var total = vm.Summary.TotalRecords;
        Assert.True(total > 100, "Eylül geçiş sayısı sayfalama için yetersiz");
        Assert.Equal($"Sayfa 1 / {(int)Math.Ceiling(total / 50d)}", vm.PageText);
        var firstOnPage1 = vm.Rows[0].Source.Id;
        vm.NextPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.Page == 2, "sonraki sayfa");
        Assert.NotEqual(firstOnPage1, vm.Rows[0].Source.Id);
        ui.Shot("reports-sayfa-2");
        vm.PreviousPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLoading && vm.Page == 1, "önceki sayfa");
        Assert.Equal(firstOnPage1, vm.Rows[0].Source.Id);
        foreach (var size in new[] { 200, 100, 50 })
        {
            vm.PageSize = size;
            Journey.Until(ui, () => !vm.IsLoading && vm.Rows.Count == Math.Min(size, total), $"sayfa boyutu {size}");
            Assert.Equal($"Sayfa 1 / {(int)Math.Ceiling(total / (double)size)}", vm.PageText);
        }

        // 5) Disa aktarma: Gelir (Eylul) PDF / Excel / CSV.
        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.Income);
        Journey.Until(ui, () => !vm.IsLoading, "Gelir");
        vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
        Journey.Run(ui, vm.ApplyAsync(), "Gelir Eylül");
        var incomeTotal = vm.Summary.TotalRecords;
        Assert.True(incomeTotal > 0);

        var csv = Export(ui, vm, vm.ExportCsvCommand, dialogs);
        var bytes = File.ReadAllBytes(csv);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV UTF-8 BOM yok");
        var lines = File.ReadAllLines(csv, Encoding.UTF8);
        Assert.Equal(incomeTotal + 1, lines.Length);
        Assert.Contains("\"Öğrenci No\"", lines[0]);
        Assert.Contains("\"Öğün\"", lines[0]);
        Assert.Contains(lines.Skip(1), x => x.Contains("Eylül ayı ödemesi", StringComparison.Ordinal));
        Assert.Contains(lines.Skip(1), x => x.EndsWith(",00\"", StringComparison.Ordinal));
        ui.Note("CSV ilk veri satırı: " + lines[1]);

        var xlsx = Export(ui, vm, vm.ExportExcelCommand, dialogs);
        using (var document = SpreadsheetDocument.Open(xlsx, false))
        {
            var sheet = document.WorkbookPart!.WorksheetParts.First().Worksheet;
            var rows = sheet.Descendants<Row>().ToList();
            var header = rows.Single(x => x.RowIndex!.Value == 5).Elements<Cell>().Select(CellText).ToList();
            Assert.Equal(["Tarih", "Öğrenci No", "Ad Soyad", "Kart", "Açıklama", "Durum", "Tutar"], header);
            Assert.Equal(incomeTotal, rows.Count(x => x.RowIndex!.Value >= 6 && CellText(x.Elements<Cell>().First()) != "Toplam"));
        }

        var pdf = Export(ui, vm, vm.ExportPdfCommand, dialogs);
        using (var document = PdfDocument.Open(pdf))
        {
            var text = document.GetPage(1).Text;
            Assert.Contains("Gelir Raporu", text);
            Assert.Contains("Öğrenci No", text);
            Assert.Contains("Toplam kayıt", text);
            // Turkce karakterlerin metin olarak geri okunabilmesi gomulu fontun dogru kodlandigini gosterir.
            Assert.Contains("Eylül", text);
            ui.Note("PDF fontları: " + string.Join(", ", document.GetPage(1).GetWords().Select(w => w.FontName).Distinct()));
        }
        ui.Shot("reports-disa-aktarma");

        // Gunluk Kasa CSV: gun x tur kirilimi -> satir sayisi grup sayisi kadar.
        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.DailyCash);
        Journey.Until(ui, () => !vm.IsLoading, "Günlük Kasa");
        vm.StartDate = SeptemberStart; vm.EndDate = SeptemberEnd;
        Journey.Run(ui, vm.ApplyAsync(), "Günlük Kasa Eylül");
        var cashCsv = Export(ui, vm, vm.ExportCsvCommand, dialogs);
        Assert.Equal(vm.Summary.TotalRecords + 1, File.ReadAllLines(cashCsv, Encoding.UTF8).Length);
        Assert.NotEqual(incomeTotal, vm.Summary.TotalRecords);
        ui.Shot("reports-gunluk-kasa");

        // 6) Secilenleri kopyala: gorunen sutun basliklari + sekmeli satirlar; gizli sutun kopyaya girmez.
        vm.ReplaceSelection(vm.Rows.Take(2));
        vm.CopySelectedCommand.Execute(null);
        var copied = dialogs.Copied.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, copied.Length);
        Assert.Equal(string.Join('\t', vm.Columns.Where(x => x.IsVisible).OrderBy(x => x.DisplayIndex).Select(x => x.Header)), copied[0]);
        Assert.Contains("₺", copied[1]);
        vm.Columns.Single(x => x.Key == "Status").IsVisible = false;
        vm.CopySelectedCommand.Execute(null);
        Assert.DoesNotContain("DURUM", dialogs.Copied.Split(Environment.NewLine)[0]);
        vm.Columns.Single(x => x.Key == "Status").IsVisible = true;
        ui.Note("Kopyalanan: " + copied[1]);
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

    private static string CellText(Cell cell) =>
        cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? "";

    private static void AssertSummary(LiveDb db, ReportsViewModel vm, ReportType type, string start, string end, string label)
    {
        var (total, passed, denied, meals, amount) = Expected(db, type, start, end);
        var s = vm.Summary;
        Assert.True(total == s.TotalRecords, $"{label}: toplam SQLite {total}, ekran {s.TotalRecords}");
        Assert.True(passed == s.Passed, $"{label}: geçen SQLite {passed}, ekran {s.Passed}");
        Assert.True(denied == s.Denied, $"{label}: reddedilen SQLite {denied}, ekran {s.Denied}");
        Assert.True(meals == s.TotalMeals, $"{label}: yemek SQLite {meals}, ekran {s.TotalMeals}");
        Assert.True(amount == s.Amount, $"{label}: tutar SQLite {amount}, ekran {s.Amount}");
        Assert.Contains(total.ToString("N0", CultureInfo.GetCultureInfo("tr-TR")), vm.SummaryText);
    }

    private static (long Total, long Passed, long Denied, long Meals, decimal Amount) Expected(LiveDb db, ReportType type, string start, string end)
    {
        string R(string column) => LiveDb.Range(column, start, end);
        switch (type)
        {
            case ReportType.DailyAccess:
            case ReportType.DeniedAccess:
            {
                var where = $"FROM access_logs a JOIN devices d ON d.Id = a.DeviceId WHERE {R("a.Timestamp")}" + (type == ReportType.DeniedAccess ? " AND a.Decision = 'DENY'" : "");
                var passed = db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'ALLOW'");
                return (db.Count("SELECT COUNT(*) " + where), passed, db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'DENY'"), passed, 0m);
            }
            case ReportType.MealEntitlement:
            {
                var where = $"FROM meal_entitlements e JOIN students s ON s.Id = e.StudentId AND s.IsDeleted = 0 JOIN meal_types m ON m.Id = e.MealTypeId WHERE e.EntitlementDate >= '{start}' AND e.EntitlementDate <= '{end}'";
                return (db.Count("SELECT COUNT(*) " + where), 0, 0, db.Count("SELECT COALESCE(SUM(e.Quantity), 0) " + where), 0m);
            }
            case ReportType.StudentMealUsage:
            case ReportType.ClassMeal:
            {
                var where = $"FROM meal_usage u JOIN students s ON s.Id = u.StudentId AND s.IsDeleted = 0 JOIN meal_types m ON m.Id = u.MealTypeId JOIN access_logs a ON a.Id = u.AccessLogId WHERE {R("u.UsedAt")}";
                var total = db.Count("SELECT COUNT(*) " + where);
                return (total, db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'ALLOW'"), db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'DENY'"), total, 0m);
            }
            case ReportType.DailyCash:
            case ReportType.Income:
            {
                var where = $"FROM income_transactions t JOIN income_types i ON i.Id = t.IncomeTypeId WHERE {R("t.TransactionAt")}";
                var transactions = db.Count("SELECT COUNT(*) " + where);
                var amount = db.Money($"SELECT COALESCE(SUM(CASE WHEN t.IsVoided = 0 THEN t.Amount ELSE 0 END), 0) {where}");
                if (type == ReportType.Income) return (transactions, 0, 0, 0, amount);
                var groups = db.Count($"SELECT COUNT(*) FROM (SELECT 1 {where} GROUP BY date(t.TransactionAt, '+3 hours'), t.IncomeTypeId, t.IsVoided)");
                return (groups, 0, 0, transactions, amount);
            }
            case ReportType.Sms:
                return (db.Count($"SELECT COUNT(*) FROM sms_logs l WHERE {R("COALESCE(l.SentAt, l.CreatedAt)")}"), 0, 0, 0, 0m);
            case ReportType.Turnstile:
            {
                var where = $"FROM turnstile_events t JOIN devices d ON d.Id = t.DeviceId LEFT JOIN access_logs a ON a.Id = t.AccessLogId WHERE {R("t.Timestamp")}";
                var passed = db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'ALLOW'");
                return (db.Count("SELECT COUNT(*) " + where), passed, db.Count($"SELECT COUNT(*) {where} AND a.Decision = 'DENY'"), passed, 0m);
            }
            case ReportType.CardMovements:
                return (db.Count($"SELECT COUNT(*) FROM student_cards c JOIN students s ON s.Id = c.StudentId AND s.IsDeleted = 0 WHERE {R("c.ValidFrom")}"), 0, 0, 0, 0m);
            case ReportType.HolidayTransfer:
            {
                var holidays = db.Count($"SELECT COUNT(*) FROM holidays WHERE Date >= '{start}' AND Date <= '{end}'");
                var transfers = $"FROM meal_transfers mt JOIN students s ON s.Id = mt.StudentId AND s.IsDeleted = 0 JOIN meal_types m ON m.Id = mt.MealTypeId WHERE mt.OriginalDate >= '{start}' AND mt.OriginalDate <= '{end}'";
                return (holidays + db.Count("SELECT COUNT(*) " + transfers), 0, 0, db.Count("SELECT COALESCE(SUM(mt.Quantity), 0) " + transfers), 0m);
            }
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

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
