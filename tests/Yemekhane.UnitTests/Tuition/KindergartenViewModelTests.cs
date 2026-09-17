using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Yemekhane.Application.Common;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;
using Yemekhane.Domain.Entities;
using Yemekhane.UnitTests.Desktop;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Anasinifi ekrani: liste "3/10" ilerlemesiyle gelir, satir secince taksitler ve tahsilatlar
/// okunur, arama Turkce harfe duyarsizdir, "taksitlere isle" dugmesi yalnizca islenmemis tahsilat
/// varken ve yazma yetkisiyle gorunur, ogrenci yoksa <see cref="KindergartenViewModel.HasStudents"/>
/// false doner (menu ogesi gizlenir).
/// </summary>
public sealed class KindergartenViewModelTests
{
    private static readonly Guid Ayse = Guid.NewGuid(), Ismail = Guid.NewGuid();

    [Fact]
    public async Task LoadsRowsWithProgressAndSummary()
    {
        var api = new FakeApi();
        var vm = new KindergartenViewModel(api, ["cash.read", "cash.write"]);

        await vm.LoadAsync();

        Assert.True(vm.HasStudents);
        Assert.True(vm.ShowContent);
        Assert.False(vm.IsEmpty);
        Assert.Equal(["AYŞE ÇELİK", "İSMAİL YURDAKUL"], vm.Students.Select(x => x.StudentName));
        var ayse = vm.Students[0];
        Assert.Equal("3/10", ayse.Progress);
        Assert.Equal("3", ayse.PaymentCountText);
        Assert.Equal("₺4.800,00", ayse.OverdueText);
        Assert.Equal("1 taksit gecikmiş", ayse.OverdueCountText);
        Assert.Equal("01.12.2026", ayse.LastPaidText);
        Assert.True(ayse.IsOverdue);
        Assert.Equal("05.12.2026", ayse.NextDueText);
        var ismail = vm.Students[1];
        Assert.Equal("Plan yok", ismail.Progress);
        Assert.Equal("", ismail.TotalPaid);
        Assert.False(ismail.IsOverdue);
        Assert.Equal("2 öğrenci · 0 tamamladı · 1 gecikmiş · ödenen ₺14.400,00 · kalan ₺33.600,00", vm.SummaryText);
    }

    [Fact]
    public async Task EmptySchoolHidesTheMenuAndShowsTheEmptyState()
    {
        var api = new FakeApi { Overview = new KindergartenOverview([], 0, false) };
        var vm = new KindergartenViewModel(api, ["cash.read"]);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await vm.LoadAsync();

        Assert.False(vm.HasStudents);
        Assert.True(vm.IsEmpty);
        Assert.True(vm.ShowTypeHint);
        Assert.Equal("Kayıtlı anasınıfı öğrencisi yok.", vm.SummaryText);
        // Kabuk menu gorunurlugunu bu bildirimle gunceller; ilk deger zaten false oldugu icin
        // bildirim gelmeyebilir, ama ogrenci gelince gelmelidir.
        api.Overview = FakeApi.DefaultOverview();
        await vm.LoadAsync();
        Assert.True(vm.HasStudents);
        Assert.Contains(nameof(KindergartenViewModel.HasStudents), changes);
        Assert.False(vm.ShowTypeHint);
    }

    [Fact]
    public async Task SelectingAStudentLoadsInstallmentsAndPayments()
    {
        var api = new FakeApi();
        var vm = new KindergartenViewModel(api, ["cash.read"]);
        await vm.LoadAsync();

        vm.SelectedStudent = vm.Students[0];
        await Task.Yield();

        Assert.True(vm.HasDetail);
        Assert.True(vm.DetailHasPlan);
        Assert.Equal("AYŞE ÇELİK · No 3001 · Anasınıfı A", vm.DetailTitle);
        Assert.Equal("3/10 taksit ödendi · 3 tahsilat", vm.DetailProgressText);
        Assert.Contains("sınıf planı", vm.DetailPlanText);
        Assert.Contains("son ödeme 01.12.2026", vm.DetailPlanText);
        Assert.Equal(10, vm.Installments.Count);
        Assert.Equal(3, vm.Payments.Count);
        Assert.Equal("3. taksit", vm.Payments[0].Installment);
        Assert.Equal("Anasınıfı Ücreti", vm.Payments[0].IncomeType);

        vm.SelectedStudent = vm.Students[1];
        await Task.Yield();
        Assert.False(vm.DetailHasPlan);
        Assert.Contains("ücret planı tanımlı değil", vm.DetailPlanText);
        Assert.Empty(vm.Installments);
    }

    [Fact]
    public async Task SearchIsTurkishInsensitiveAndReportsNoMatch()
    {
        var vm = new KindergartenViewModel(new FakeApi(), ["cash.read"]);
        await vm.LoadAsync();

        vm.Search = "ismail";
        Assert.Equal(["İSMAİL YURDAKUL"], vm.Students.Select(x => x.StudentName));
        vm.Search = "CELIK";
        Assert.Equal(["AYŞE ÇELİK"], vm.Students.Select(x => x.StudentName));
        vm.Search = "3001";
        Assert.Single(vm.Students);
        vm.Search = "yok böyle";
        Assert.Empty(vm.Students);
        Assert.True(vm.HasFilterNoMatch);
        Assert.False(vm.IsEmpty);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(2, vm.Students.Count);
    }

    [Fact]
    public async Task ReconcileIsOfferedOnlyWithUnappliedIncomeAndWritePermission()
    {
        var api = new FakeApi { Overview = FakeApi.DefaultOverview() with { UnappliedIncomeCount = 4 } };
        var reader = new KindergartenViewModel(api, ["cash.read"]);
        await reader.LoadAsync();
        Assert.False(reader.ShowReconcile);
        Assert.False(reader.ReconcileCommand.CanExecute(null));

        var writer = new KindergartenViewModel(api, ["cash.read", "cash.write"]);
        await writer.LoadAsync();
        Assert.True(writer.ShowReconcile);
        Assert.Equal("Kasadaki 4 tahsilatı taksitlere işle", writer.ReconcileText);
        Assert.True(writer.ReconcileCommand.CanExecute(null));

        api.ReconcileResult = new TuitionReconcileResult(4, 3, 1);
        api.Overview = api.Overview with { UnappliedIncomeCount = 0 };
        writer.ReconcileCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(1, api.ReconcileCalls);
        Assert.Equal("3 tahsilat taksitlere sayıldı; 1 tahsilatın açık taksiti yok.", writer.StatusMessage);
        Assert.False(writer.ShowReconcile);
    }

    [Fact]
    public async Task ApiFailureIsShownNotThrown()
    {
        var vm = new KindergartenViewModel(new FakeApi { Fail = true }, ["cash.read"]);

        await vm.ProbeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("API bağlantısını", vm.Error);
        Assert.False(vm.HasStudents);
        Assert.False(vm.IsEmpty);
    }

    /// <summary>
    /// Ekran gercek XAML ile 1280x720'de cizilir: liste sutunlari kirpilmadan sigar, satir secilince
    /// sag panelde taksit tablosu gorunur. Yalnizca ViewModel testi hatali baglama yolunu yakalamaz.
    /// </summary>
    [Fact]
    public void ViewRendersListAndDetailAt1280x720() => UiThread.Run(() =>
    {
        var vm = new KindergartenViewModel(new FakeApi(), ["cash.read", "cash.write"]);
        var view = new KindergartenView { DataContext = vm };
        var host = UiThread.Host(view, 1280, 720);
        host.Measure(new Size(1280, 720));
        host.Arrange(new Rect(0, 0, 1280, 720));
        host.UpdateLayout();

        vm.LoadAsync().GetAwaiter().GetResult();
        vm.SelectedStudent = vm.Students[0];
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        host.UpdateLayout();

        var students = (DataGrid)view.FindName("StudentsGrid");
        Assert.Equal(2, students.Items.Count);
        var visible = students.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        Assert.Equal(9, visible.Count);
        Assert.True(visible.Sum(c => c.ActualWidth) <= students.ActualWidth + 1,
            $"Sütunlar listeye sığmıyor: {visible.Sum(c => c.ActualWidth):0} > {students.ActualWidth:0}");
        var installments = (DataGrid)view.FindName("InstallmentsGrid");
        Assert.Equal(10, installments.Items.Count);
        Assert.True(installments.ActualWidth > 300, $"Taksit tablosu dar: {installments.ActualWidth:0}px");
        var payments = (DataGrid)view.FindName("PaymentsGrid");
        Assert.Equal(3, payments.Items.Count);
    });

    private sealed class FakeApi : ITuitionApiClient
    {
        public KindergartenOverview Overview { get; set; } = DefaultOverview();
        public TuitionReconcileResult ReconcileResult { get; set; } = new(0, 0, 0);
        public int ReconcileCalls { get; private set; }
        public bool Fail { get; set; }

        public static KindergartenOverview DefaultOverview() => new(
        [
            new KindergartenStudentRow(Ayse, "3001", "AYŞE ÇELİK", "Anasınıfı A", true, 10, 3, 3, 48_000m, 14_400m, 33_600m, 4_800m, 1, new DateOnly(2026, 12, 5), new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.FromHours(3))),
            new KindergartenStudentRow(Ismail, "3002", "İSMAİL YURDAKUL", "Anasınıfı B", false, 0, 0, 0, 0, 0, 0, 0, 0, null, null),
        ], 0, true);

        public Task<KindergartenOverview> KindergartenAsync(CancellationToken cancellationToken = default) =>
            Fail ? throw new System.Net.Http.HttpRequestException("bağlantı yok") : Task.FromResult(Overview);

        public Task<TuitionReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
        {
            ReconcileCalls++;
            return Task.FromResult(ReconcileResult);
        }

        public Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default)
        {
            if (studentId == Ismail)
                return Task.FromResult(new StudentTuitionSummary(Ismail, "3002", "İSMAİL YURDAKUL", "Anasınıfı B", false, null, []));
            var planId = Guid.NewGuid();
            var installments = Enumerable.Range(1, 10).Select(i =>
            {
                var due = new DateOnly(2026, 10, 5).AddMonths(i - 1);
                var paid = i <= 3 ? 4_800m : 0m;
                var status = i <= 3 ? TuitionInstallmentStatuses.Paid : due < new DateOnly(2026, 12, 15) ? TuitionInstallmentStatuses.Overdue : TuitionInstallmentStatuses.Pending;
                return new TuitionInstallmentDetails(Guid.NewGuid(), planId, Ayse, i, due, 4_800m, paid, 4_800m - paid, status, TuitionInstallmentStatuses.Label(status), false, null);
            }).ToList();
            var plan = new TuitionPlanDetails(planId, TuitionScopes.Class, Guid.NewGuid(), "Anasınıfı A", null, null, null,
                TuitionPlanKinds.Installment, TuitionPlanKinds.Label(TuitionPlanKinds.Installment), "2026-2027", 48_000m, 0m, 10, 5,
                new DateOnly(2026, 10, 1), null, true, 48_000m, 14_400m, 33_600m, 4_800m, installments);
            var payments = Enumerable.Range(1, 3).Reverse().Select(i => new TuitionPaymentDetails(Guid.NewGuid(), Guid.NewGuid(), i,
                new DateTimeOffset(2026, 9 + i, 5, 10, 0, 0, TimeSpan.FromHours(3)), 4_800m, "Anasınıfı Ücreti", i == 3 ? "Aralık" : null)).ToList();
            return Task.FromResult(new StudentTuitionSummary(Ayse, "3001", "AYŞE ÇELİK", "Anasınıfı A", true, plan, payments));
        }

        public Task<PagedResult<TuitionPlanDetails>> PlansAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TuitionPlanDetails> SavePlanAsync(SaveTuitionPlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StudentStatement> StatementAsync(Guid studentId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadStatementPdfAsync(Guid studentId, DateOnly startDate, DateOnly endDate, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
