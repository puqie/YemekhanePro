using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;
using Yemekhane.UnitTests.Api;

// Sahte istemcilerde bazi olaylar arabirim geregi vardir ama tetiklenmez.
#pragma warning disable CS0067

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// CALISMA ZAMANI baglama hatalarinin yakalanmasi.
///
/// Statik cozumleme yolun var oldugunu dogrular; bu testler WPF'in gercekte
/// baglamayi KURABILDIGINI dogrular. Yanlis tip donusumu, eksik converter
/// ya da salt okunur hedefe TwoWay baglama yalnizca gorsel agac gercek bir
/// DataContext ile kurulurken ortaya cikar.
///
/// DataContext'siz test bir ise yaramaz: WPF cozecek bir kaynak olmadigi
/// icin baglamayi hic denemez ve bozuk bir yol bile sessiz kalir.
///
/// WPF bu hatalari yutar ve yalnizca izleme kanalina yazar; burada o kanal
/// dinlenir.
/// </summary>
[Collection("UI")]
public sealed class LiveBindingTests
{
    public static TheoryData<string> Views() =>
    [
        "students", "cash", "entitlements", "calendar", "devices",
        "devicecards", "sms", "reports", "settings", "daily", "bulk", "definitions",
    ];

    /// <summary>
    /// Gorunumu, ona atanan GERCEK ViewModel ile birlikte uretir.
    /// Istemciler ulasilamayan bir adrese bakar: veri gelmez ama
    /// baglamalar cozulur -- test edilen sey de budur.
    /// </summary>
    private static (FrameworkElement View, object Model) Create(string name)
    {
        var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromMilliseconds(200) };
        var session = new OfflineSession();
        var routes = new ShellNavigationService([
            ShellRoutes.Students, ShellRoutes.Entitlements, ShellRoutes.Cash, ShellRoutes.Sms,
            ShellRoutes.Reports, ShellRoutes.Devices, ShellRoutes.DeviceCards,
            ShellRoutes.Settings, ShellRoutes.HolidayTransfer, ShellRoutes.DailyTracking,
        ]);

        return name switch
        {
            "students" => (new StudentsView(), new StudentsViewModel(
                new StudentApiClient(http, session), routes,
                ["students.read", "students.write", "students.deactivate", "cards.manage"])),

            "cash" => (new CashView(), new CashViewModel(
                new CashApiClient(http, session), ["cash.read", "cash.write", "cash.manage"])),

            "entitlements" => (new MealEntitlementsView(), new MealEntitlementsViewModel(
                new MealEntitlementApiClient(http, session), ["entitlements.manage", "entitlements.bulk"])),

            "calendar" => (new CalendarView(), new CalendarViewModel(
                new CalendarApiClient(http, session), ["calendar.manage"])),

            "devices" => (new DevicesView(), new DevicesViewModel(
                new DeviceApiClient(http, session), new SilentRealtime(),
                new HashSet<string>(StringComparer.Ordinal) { "devices.manage" })),

            "devicecards" => (new DeviceCardsView(), new DeviceCardsViewModel(
                new DeviceCardsApiClient(http, session))),

            "sms" => (new SmsView(), new SmsViewModel(
                new SmsApiClient(http, session), ["sms.read", "sms.send", "sms.manage"])),

            "reports" => (new ReportsView(), new ReportsViewModel(
                new ReportApiClient(http, session), ["reports.read", "reports.export"])),

            "settings" => (new SettingsView(), new SettingsViewModel(
                new SettingsApiClient(http, session), routes, ["settings.read", "settings.manage"])),

            "daily" => (new DailyTrackingView(), new DailyTrackingViewModel(
                new DailyTrackingApiClient(http, session), new SilentRealtime(),
                new MemoryPreferences(), new SilentSound())),

            "bulk" => (new BulkOperationWizardView(), new BulkOperationWizardViewModel(
                new BulkOperationApiClient(http, session), ["entitlements.bulk", "calendar.manage"])),

            "definitions" => (new DefinitionsView(), new DefinitionsViewModel(
                new DefinitionsApiClient(http, session), ["entitlements.manage", "students.read", "students.write"])),

            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Bilinmeyen görünüm."),
        };
    }

    /// <summary>
    /// Her ekran, GERCEK ViewModel'i baglandiginda hatasiz kurulmalidir.
    /// Bir baglama cozulemezse ilgili alan sessizce bos kalir: kullanici
    /// veri girer, kaydeder ve verisinin kayboldugunu gorur.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void ViewBindsToItsViewModelWithoutErrors(string name)
    {
        var errors = RunAndCollect(() =>
        {
            var (view, model) = Create(name);
            UiThread.ApplyResources(view);
            view.DataContext = model;
            Measure(view);
        });

        Assert.True(errors.Count == 0,
            $"{name}: {errors.Count} bağlama hatası — ilgili alanlar boş kalır:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, errors.Take(10))}");
    }

    /// <summary>
    /// Uygulama acilirken DataContext bir sure null kalir. Bu asamada da
    /// ekran cokmeden kurulabilmelidir.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void ViewStillBuildsBeforeItsDataArrives(string name)
    {
        UiThread.Run(() =>
        {
            var (view, _) = Create(name);
            UiThread.ApplyResources(view);
            Measure(view);          // DataContext atanmadan
        });
    }

    /// <summary>Gorsel agaci olcup yerlestirir; baglamalar bu sirada kurulur.</summary>
    private static void Measure(FrameworkElement view)
    {
        var host = new Border { Width = 1400, Height = 860, Child = view };
        host.Measure(new Size(1400, 860));
        host.Arrange(new Rect(0, 0, 1400, 860));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    private static List<string> RunAndCollect(Action action)
    {
        // WPF izleme altyapisi ilk erisimden ONCE kurulmalidir; Refresh()
        // cagrilmazsa kanal hic yazmaz ve dinleyici bos kalir.
        PresentationTraceSources.Refresh();

        var listener = new BindingErrorListener();
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;

        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
        try { UiThread.Run(action); }
        finally
        {
            source.Listeners.Remove(listener);
            source.Switch.Level = previousLevel;
        }
        return listener.Errors;
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Yalnizca gercek COZUMLEME hatalari sayilir; diger izleme
            // satirlari bilgi amaclidir.
            if (message.Contains("path error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Cannot find source", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TwoWay or OneWayToSource binding cannot work",
                    StringComparison.OrdinalIgnoreCase))
                Errors.Add(message.Trim());
        }
    }

    private sealed class OfflineSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateOperatorToken();
        public bool IsAuthenticated => true;
    }

    private sealed class SilentRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryPreferences : IDailyTrackingPreferences
    {
        public bool SoundEnabled { get; set; } = true;
    }

    private sealed class SilentSound : ITrackingSoundPlayer
    {
        public ValueTask PlayAsync(string decision, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
