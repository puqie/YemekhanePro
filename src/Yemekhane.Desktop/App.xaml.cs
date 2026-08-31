using System.IO;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Markup;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.Desktop;

public partial class App : System.Windows.Application, IDisposable
{
    private Mutex? singleInstanceMutex;
    private LocalApiProcessManager? localApi;
    private DashboardRealtimeClient? realtimeClient;
    private StudentsViewModel? students;
    private MealEntitlementsViewModel? entitlements;
    private CalendarViewModel? calendar;
    private DevicesViewModel? devices;
    private DeviceCardsViewModel? deviceCards;
    private SmsViewModel? sms;
    private CashViewModel? cash;
    private ReportsViewModel? reports;
    private SettingsViewModel? settings;
    private GlobalSearchViewModel? globalSearch;
    private NotificationCenterViewModel? notifications;

    public App()
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private async void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        // async void icindeki yakalanmamis exception WPF'te uygulamayi HICBIR mesaj gostermeden
        // kapatir. Eksik yapilandirma veya erisilemeyen API'de kullanici bos ekranla kalirdi.
        try
        {
            if (!await StartAsync()) Shutdown();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"Uygulama başlatılamadı:{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "YemekhanePro", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Baslangictan sonraki beklenmeyen hatalarda da sessiz kapanma yerine bilgi verilir.</summary>
    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"Beklenmeyen bir hata oluştu:{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}",
            "YemekhanePro", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    private async Task<bool> StartAsync()
    {
        singleInstanceMutex = new Mutex(true, @"Local\YemekhanePro.Desktop", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("YemekhanePro zaten çalışıyor.", "YemekhanePro",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
            return false;
        }

        AsyncCommand.UnhandledError += (_, exception) => System.Windows.MessageBox.Show(
            $"İşlem tamamlanamadı:{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            "YemekhanePro", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(Path.Combine(Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve(),
                "appsettings.desktop.json"), optional: true)
            .AddEnvironmentVariables("YEMEKHANE_")
            .Build();
        var baseUriValue = configuration["Api:BaseUri"]
            ?? throw new InvalidOperationException("Api:BaseUri yapılandırması bulunamadı.");
        if (!Uri.TryCreate(baseUriValue, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("Api:BaseUri mutlak bir URI olmalıdır.");

        localApi = new LocalApiProcessManager(baseUri);
        await localApi.EnsureReadyAsync();

        var session = new MutableJwtSession();
        var httpClient = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        var login = new LoginWindow(new AuthenticationClient(httpClient, session),
            localApi.ConsumeInitialAdminCredentials(), localApi.HasExistingDatabase);
        if (login.ShowDialog() != true) return false;
        var apiClient = new DashboardApiClient(httpClient, session);
        realtimeClient = new DashboardRealtimeClient(baseUri, session);
        var permissions = JwtPermissions.Read(session.AccessToken);
        var routes = new List<string> { ShellRoutes.Dashboard, ShellRoutes.DailyTracking, ShellRoutes.Students, ShellRoutes.StudentDetail };
        if (permissions.Contains("students.write")) routes.Add(ShellRoutes.StudentsCreate);
        if (permissions.Contains("cards.manage")) { routes.Add(ShellRoutes.Cards); routes.Add(ShellRoutes.CardReader); }
        if (permissions.Contains("entitlements.manage") || permissions.Contains("entitlements.bulk")) routes.Add(ShellRoutes.Entitlements);
        if (permissions.Contains("calendar.manage")) routes.Add(ShellRoutes.HolidayTransfer);
        if (permissions.Contains("devices.read") || permissions.Contains("devices.manage"))
        { routes.Add(ShellRoutes.Devices); routes.Add(ShellRoutes.DeviceCards); }
        if (permissions.Contains("sms.read") || permissions.Contains("sms.send") || permissions.Contains("sms.manage")) routes.Add(ShellRoutes.Sms);
        if (permissions.Contains("cash.read")) routes.Add(ShellRoutes.Cash);
        if (permissions.Contains("reports.read")) routes.Add(ShellRoutes.Reports);
        if (permissions.Contains("settings.read") || permissions.Contains("settings.manage")) routes.Add(ShellRoutes.Settings);
        var navigation = new ShellNavigationService(routes);
        globalSearch = new GlobalSearchViewModel(new GlobalSearchApiClient(httpClient, session), navigation);
        var viewModel = new DashboardViewModel(apiClient, realtimeClient, navigation, session);
        notifications = permissions.Contains("notifications.read")
            ? new NotificationCenterViewModel(new NotificationApiClient(httpClient, session), realtimeClient, navigation)
            : null;
        var tracking = new DailyTrackingViewModel(new DailyTrackingApiClient(httpClient, session), realtimeClient,
            new FileDailyTrackingPreferences(), new SystemTrackingSoundPlayer());
        students = new StudentsViewModel(new StudentApiClient(httpClient, session), navigation, permissions);
        var entitlementBulk = new BulkOperationWizardViewModel(new BulkOperationApiClient(httpClient, session), permissions);
        var calendarBulk = new BulkOperationWizardViewModel(new BulkOperationApiClient(httpClient, session), permissions);
        entitlements = new MealEntitlementsViewModel(new MealEntitlementApiClient(httpClient, session), permissions, entitlementBulk);
        calendar = new CalendarViewModel(new CalendarApiClient(httpClient, session), permissions, bulkWizard: calendarBulk);
        devices = new DevicesViewModel(new DeviceApiClient(httpClient, session), realtimeClient, permissions);
        deviceCards = permissions.Contains("devices.read") || permissions.Contains("devices.manage")
            ? new DeviceCardsViewModel(new DeviceCardsApiClient(httpClient, session))
            : null;
        sms = new SmsViewModel(new SmsApiClient(httpClient, session), permissions);
        cash = new CashViewModel(new CashApiClient(httpClient, session), permissions, navigation: navigation);
        reports = new ReportsViewModel(new ReportApiClient(httpClient, session), permissions);
        settings = new SettingsViewModel(new SettingsApiClient(httpClient, session), navigation, permissions);
        var window = new MainWindow { DataContext = viewModel, DailyTrackingDataContext = tracking,
            StudentsDataContext = students, MealEntitlementsDataContext = entitlements, CalendarDataContext = calendar,
            DevicesDataContext = devices, DeviceCardsDataContext = deviceCards, SmsDataContext = sms, CashDataContext = cash, ReportsDataContext = reports,
             SettingsDataContext = settings, GlobalSearchDataContext = globalSearch, NotificationDataContext = notifications };
        window.ConfigureShortcuts(permissions);
        navigation.NavigationRequested += (_, args) => window.Navigate(args.Route);
        tracking.StudentDetailNavigationRequested += (_, route) => navigation.Navigate(route);
        MainWindow = window;
        window.Show();
        await Task.WhenAll(viewModel.InitializeAsync(), tracking.InitializeAsync(), students.InitializeAsync(), entitlements.InitializeAsync(),
             calendar.InitializeAsync(), devices.InitializeAsync(), sms.InitializeAsync(), cash.InitializeAsync(), reports.InitializeAsync(), settings.InitializeAsync(), entitlementBulk.InitializeAsync(), calendarBulk.InitializeAsync(), notifications?.InitializeAsync() ?? Task.CompletedTask,
             deviceCards?.InitializeAsync() ?? Task.CompletedTask);
        return true;
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (realtimeClient is not null) await realtimeClient.DisposeAsync();
        students?.Dispose();
        devices?.Dispose();
        sms?.Dispose();
        reports?.Dispose();
        globalSearch?.Dispose();
        notifications?.Dispose();
        if (localApi is not null) await localApi.DisposeAsync();
        if (singleInstanceMutex is not null)
        {
            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    public void Dispose()
    {
        realtimeClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
