using System.Net.Http;
using Yemekhane.Application.Common;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Statements;

/// <summary>
/// Ekstre ekrani: memur tarih araligini secip getirir, veliye gosterir ya da PDF verir.
/// Yanlis ogrencinin verisinin ekranda kalmasi ve sessiz basarisizlik en riskli iki durum.
/// </summary>
public sealed class StudentStatementViewModelTests
{
    private static readonly Guid StudentId = Guid.NewGuid();

    private static StatementLine Line(string section, int day, string title, decimal amount = 0, int quantity = 0,
        string? status = null, bool cancelled = false) =>
        new(section, new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.FromHours(3)), title, null, amount, quantity, status, cancelled);

    private static StudentStatement Statement(params StatementLine[] lines) =>
        new(StudentId, "1042", "Ayşe Yılmaz", "Anasınıfı A", "A", "Fatma Yılmaz", "+905321234567",
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            4_800m, 0m, 500m, 250m, 250m, 48_000m, 4_800m, 43_200m, 4_800m, 2,
            [new StatementSectionSummary(StatementSections.Payment, "Ödemeler", 1, 4_800m)], lines);

    [Fact]
    public async Task LoadingFillsRowsAndSummary()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 5, "Anasınıfı Ücreti", 4_800m, status: "Tahsil edildi")) };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);

        await vm.LoadAsync();

        Assert.True(vm.HasRows);
        Assert.Single(vm.Rows);
        Assert.Equal("05.10.2026", vm.Rows[0].Date);
        Assert.Equal("Ödemeler", vm.Rows[0].Section);
        Assert.Contains("Tahsil edilen", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Kalan borç", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Gecikmiş", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("2 öğün", vm.SummaryText, StringComparison.Ordinal);
        Assert.Equal("Ayşe Yılmaz · No 1042", vm.Title);
    }

    /// <summary>Yemek satiri para degil adet gosterir; veli "yemek ucreti" saniyordu.</summary>
    [Fact]
    public async Task MealRowsShowCountsNotMoney()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Meal, 6, "Öğle Yemeği", quantity: 1, status: "Kullanıldı")) };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);

        await vm.LoadAsync();

        Assert.Equal("1 öğün", vm.Rows[0].Amount);
    }

    [Fact]
    public async Task CancelledRowIsMarked()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 8, "Nakit", 1_000m, status: "İptal", cancelled: true)) };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);

        await vm.LoadAsync();

        Assert.True(vm.Rows[0].IsCancelled);
        Assert.Equal("İptal", vm.Rows[0].Status);
    }

    [Fact]
    public async Task EmptyRangeTellsTheUser()
    {
        var api = new FakeApi { Result = Statement() };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);

        await vm.LoadAsync();

        Assert.False(vm.HasRows);
        Assert.Equal("Bu tarih aralığında kayıt bulunamadı.", vm.StatusMessage);
    }

    [Fact]
    public async Task ReversedRangeIsRejectedBeforeCallingTheApi()
    {
        var api = new FakeApi { Result = Statement() };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);
        vm.StartDate = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Local);
        vm.EndDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local);

        await vm.LoadAsync();

        Assert.Equal(0, api.StatementCalls);
        Assert.Equal("Bitiş tarihi başlangıçtan önce olamaz.", vm.ErrorMessage);
    }

    [Fact]
    public async Task ServerErrorIsShownNotThrown()
    {
        var api = new FakeApi { Fail = true };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);

        await vm.LoadAsync();

        Assert.Contains("Ekstre alınamadı", vm.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>Ogrenci degisince onceki ekstre EKRANDA KALMAMALI; yanlis veliye yanlis rakam gosterilir.</summary>
    [Fact]
    public async Task ChangingStudentClearsThePreviousStatement()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 5, "Nakit", 100m)) };
        var vm = new StudentStatementViewModel(api);
        vm.SetStudent(StudentId);
        await vm.LoadAsync();
        Assert.True(vm.HasRows);

        vm.SetStudent(Guid.NewGuid());

        Assert.False(vm.HasRows);
        Assert.Empty(vm.Rows);
        Assert.Equal("Öğrenci Ekstresi", vm.Title);
        Assert.False(vm.ExportPdfCommand.CanExecute(null));
    }

    [Fact]
    public async Task PdfIsSavedToTheChosenPath()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 5, "Nakit", 100m)) };
        var dialog = new FakeDialog { Path = @"C:\temp\ekstre.pdf" };
        var vm = new StudentStatementViewModel(api, dialog);
        vm.SetStudent(StudentId);
        await vm.LoadAsync();

        await ((AsyncCommand)vm.ExportPdfCommand).ExecuteAsync(null);

        Assert.Equal(@"C:\temp\ekstre.pdf", api.LastPdfPath);
        Assert.Contains("Ekstre kaydedildi", vm.StatusMessage!, StringComparison.Ordinal);
        // Ekrandaki aralik degil, GETIRILEN ekstrenin araligi indirilir.
        Assert.Equal(new DateOnly(2026, 10, 1), api.LastPdfFrom);
        Assert.Equal(new DateOnly(2026, 10, 31), api.LastPdfTo);
    }

    [Fact]
    public async Task CancellingTheSaveDialogDoesNothing()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 5, "Nakit", 100m)) };
        var vm = new StudentStatementViewModel(api, new FakeDialog { Path = null });
        vm.SetStudent(StudentId);
        await vm.LoadAsync();

        await ((AsyncCommand)vm.ExportPdfCommand).ExecuteAsync(null);

        Assert.Null(api.LastPdfPath);
        Assert.Null(vm.StatusMessage);
    }

    /// <summary>Disa aktarma yetkisi yoksa dugme etkin olmamali.</summary>
    [Fact]
    public async Task ExportIsDisabledWithoutPermission()
    {
        var api = new FakeApi { Result = Statement(Line(StatementSections.Payment, 5, "Nakit", 100m)) };
        var vm = new StudentStatementViewModel(api, new FakeDialog(), canExport: false);
        vm.SetStudent(StudentId);
        await vm.LoadAsync();

        Assert.False(vm.ExportPdfCommand.CanExecute(null));
    }

    private sealed class FakeDialog : IStatementFileDialog
    {
        public string? Path { get; init; }
        public string? ChoosePdfPath(string suggestedFileName) => Path;
    }

    private sealed class FakeApi : ITuitionApiClient
    {
        public StudentStatement? Result { get; init; }
        public bool Fail { get; init; }
        public int StatementCalls { get; private set; }
        public string? LastPdfPath { get; private set; }
        public DateOnly? LastPdfFrom { get; private set; }
        public DateOnly? LastPdfTo { get; private set; }

        public Task<StudentStatement> StatementAsync(Guid studentId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            StatementCalls++;
            if (Fail) throw new HttpRequestException();
            return Task.FromResult(Result!);
        }

        public Task DownloadStatementPdfAsync(Guid studentId, DateOnly startDate, DateOnly endDate, string path, CancellationToken cancellationToken = default)
        {
            LastPdfPath = path; LastPdfFrom = startDate; LastPdfTo = endDate;
            return Task.CompletedTask;
        }

        public Task<PagedResult<TuitionPlanDetails>> PlansAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TuitionPlanDetails> SavePlanAsync(SaveTuitionPlanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeletePlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
