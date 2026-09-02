using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// CALISAN yerel API'ye baglanip masaustu ViewModel'lerini App.xaml.cs ile
/// birebir ayni sekilde kablolar, MainWindow'u ekran disinda gosterir ve
/// yolculuk (journey) testlerinin gercek veriyle ekran cekmesine izin verir.
/// </summary>
/// <remarks>
/// <para>Butun testler <see cref="Run"/> icinde, STA bir is parcaciginda calisir.
/// ViewModel'lerin async devamlari bu dispatcher'a geri gonderilir; bu yuzden
/// bekleme <see cref="Wait"/> ile (DispatcherFrame) yapilir -- duz
/// <c>GetAwaiter().GetResult()</c> burada KILITLENIR.</para>
/// <para>Tum canli testler <c>YP_LIVE_API</c> ortam degiskeni (orn.
/// <c>http://127.0.0.1:5255/</c>) yoksa sessizce gecer: normal paket kosusu
/// API'ye bagimli degildir.</para>
/// </remarks>
public sealed class LiveUiHarness
{
    public static string? ApiUrl => Environment.GetEnvironmentVariable("YP_LIVE_API");
    public static bool Enabled => !string.IsNullOrWhiteSpace(ApiUrl);
    public static string ShotDir =>
        Environment.GetEnvironmentVariable("YP_SHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "yp-shots");

    public MainWindow Window { get; }
    public HttpClient Http { get; }
    public MutableJwtSession Session { get; }
    public IReadOnlySet<string> Permissions { get; }
    public ShellNavigationService Navigation { get; }
    public DashboardViewModel Dashboard { get; }
    public DailyTrackingViewModel Tracking { get; }
    public StudentsViewModel Students { get; }
    public MealEntitlementsViewModel Entitlements { get; }
    public BulkOperationWizardViewModel EntitlementBulk { get; }
    public CalendarViewModel Calendar { get; }
    public BulkOperationWizardViewModel CalendarBulk { get; }
    public DevicesViewModel Devices { get; }
    public DeviceCardsViewModel DeviceCards { get; }
    public SmsViewModel Sms { get; }
    public CashViewModel Cash { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }
    public StudentImportViewModel StudentImport { get; }

    private readonly List<string> log = [];
    public IReadOnlyList<string> Log => log;
    public void Note(string message) => log.Add(message);

    private LiveUiHarness(string baseUrl)
    {
        var baseUri = new Uri(baseUrl);
        Session = new MutableJwtSession();
        Http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };

        var loginTask = new AuthenticationClient(Http, Session).LoginAsync("admin", "TestParola123!", CancellationToken.None);
        if (!Wait(loginTask, TimeSpan.FromSeconds(60))) throw new InvalidOperationException("Giris zaman asimina ugradi.");
        // LoginAsync yalnizca sonucu dondurur, oturuma yazmaz -- App.xaml.cs de boyle yapar.
        Session.Set(loginTask.Result.AccessToken, loginTask.Result.ExpiresAt);
        Permissions = JwtPermissions.Read(Session.AccessToken);

        var routes = new List<string> { ShellRoutes.Dashboard, ShellRoutes.DailyTracking, ShellRoutes.Students, ShellRoutes.StudentDetail };
        if (Permissions.Contains("students.write")) { routes.Add(ShellRoutes.StudentsCreate); routes.Add(ShellRoutes.StudentImport); }
        if (Permissions.Contains("cards.manage")) { routes.Add(ShellRoutes.Cards); routes.Add(ShellRoutes.CardReader); }
        if (Permissions.Contains("entitlements.manage") || Permissions.Contains("entitlements.bulk")) routes.Add(ShellRoutes.Entitlements);
        if (Permissions.Contains("calendar.manage")) routes.Add(ShellRoutes.HolidayTransfer);
        if (Permissions.Contains("devices.read") || Permissions.Contains("devices.manage")) { routes.Add(ShellRoutes.Devices); routes.Add(ShellRoutes.DeviceCards); }
        if (Permissions.Contains("sms.read") || Permissions.Contains("sms.send") || Permissions.Contains("sms.manage")) routes.Add(ShellRoutes.Sms);
        if (Permissions.Contains("cash.read")) routes.Add(ShellRoutes.Cash);
        if (Permissions.Contains("reports.read")) routes.Add(ShellRoutes.Reports);
        if (Permissions.Contains("settings.read") || Permissions.Contains("settings.manage")) routes.Add(ShellRoutes.Settings);
        Navigation = new ShellNavigationService(routes);

        var realtime = new DashboardRealtimeClient(baseUri, Session);
        Dashboard = new DashboardViewModel(new DashboardApiClient(Http, Session), realtime, Navigation, Session);
        Tracking = new DailyTrackingViewModel(new DailyTrackingApiClient(Http, Session), realtime, new FileDailyTrackingPreferences(), new SystemTrackingSoundPlayer());
        Students = new StudentsViewModel(new StudentApiClient(Http, Session), Navigation, Permissions);
        EntitlementBulk = new BulkOperationWizardViewModel(new BulkOperationApiClient(Http, Session), Permissions);
        CalendarBulk = new BulkOperationWizardViewModel(new BulkOperationApiClient(Http, Session), Permissions);
        Entitlements = new MealEntitlementsViewModel(new MealEntitlementApiClient(Http, Session), Permissions, EntitlementBulk);
        Calendar = new CalendarViewModel(new CalendarApiClient(Http, Session), Permissions, bulkWizard: CalendarBulk);
        Devices = new DevicesViewModel(new DeviceApiClient(Http, Session), realtime, Permissions);
        DeviceCards = new DeviceCardsViewModel(new DeviceCardsApiClient(Http, Session));
        Sms = new SmsViewModel(new SmsApiClient(Http, Session), Permissions);
        Cash = new CashViewModel(new CashApiClient(Http, Session), Permissions, navigation: Navigation);
        Reports = new ReportsViewModel(new ReportApiClient(Http, Session), Permissions);
        Settings = new SettingsViewModel(new SettingsApiClient(Http, Session), Navigation, Permissions);
        StudentImport = new StudentImportViewModel(new StudentImportApiClient(Http, Session), new FileDialogService(), Permissions);

        Window = new MainWindow
        {
            DataContext = Dashboard, DailyTrackingDataContext = Tracking, StudentsDataContext = Students,
            MealEntitlementsDataContext = Entitlements, CalendarDataContext = Calendar, DevicesDataContext = Devices,
            DeviceCardsDataContext = DeviceCards, SmsDataContext = Sms, CashDataContext = Cash, ReportsDataContext = Reports,
            SettingsDataContext = Settings, StudentImportDataContext = StudentImport,
            Width = 1440, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, ShowInTaskbar = false,
        };
        Window.ConfigureShortcuts(Permissions);
        Navigation.NavigationRequested += (_, args) => Window.Navigate(args.Route);
        Window.Show();

        // GlobalSearch/Notification DataContext verilmedi; IsOpen baglanmadigi icin
        // bu katmanlar acik render olup ekrani karartir.
        foreach (var name in new[] { "GlobalSearchHost", "NotificationHost", "ShortcutHelpHost" })
            if (Window.FindName(name) is UIElement layer) layer.Visibility = Visibility.Collapsed;
    }

    /// <summary>Tum ekranlarin InitializeAsync'ini sirayla bekler; hata verenleri Note'a yazar.</summary>
    public void LoadAll()
    {
        var loads = new (string Name, Task Task)[]
        {
            ("Dashboard", Dashboard.InitializeAsync()), ("Günlük Takip", Tracking.InitializeAsync()),
            ("Öğrenciler", Students.InitializeAsync()), ("Hakedişler", Entitlements.InitializeAsync()),
            ("Takvim", Calendar.InitializeAsync()), ("Cihazlar", Devices.InitializeAsync()),
            ("SMS", Sms.InitializeAsync()), ("Kasa", Cash.InitializeAsync()),
            ("Raporlar", Reports.InitializeAsync()), ("Ayarlar", Settings.InitializeAsync()),
            ("Toplu işlem", EntitlementBulk.InitializeAsync()), ("Kart durumları", DeviceCards.InitializeAsync()),
        };
        foreach (var (name, task) in loads)
        {
            if (!Wait(task, TimeSpan.FromSeconds(30))) Note($"ZAMAN AŞIMI: {name}");
            else if (task.IsFaulted) Note($"YÜKLEME HATASI {name}: {task.Exception?.GetBaseException().Message}");
        }
        Pump(8);
    }

    public void Navigate(string route) { Window.Navigate(route); Pump(6); }

    /// <summary>Dispatcher'i N tur calistirir; yerlesim ve baglama guncellemelerinin islenmesini saglar.</summary>
    public void Pump(int passes = 4)
    {
        for (var i = 0; i < passes; i++)
        {
            Window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>Bir Task'i dispatcher'i CALISIR TUTARAK bekler. true = tamamlandi.</summary>
    public static bool Wait(Task task, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var completed = false;
        task.ContinueWith(_ => { completed = true; frame.Continue = false; }, TaskScheduler.FromCurrentSynchronizationContext());
        var timer = new DispatcherTimer(timeout, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return completed;
    }

    /// <summary>Belirli sure boyunca dispatcher'i calistirarak bekler (arka plan isleri icin).</summary>
    public void Delay(int milliseconds) => Wait(Task.Delay(milliseconds), TimeSpan.FromMilliseconds(milliseconds + 5000));

    /// <summary>Pencereyi PNG olarak kaydeder; dosya yolunu dondurur.</summary>
    public string Shot(string name)
    {
        Directory.CreateDirectory(ShotDir);
        Pump(3);
        var w = (int)Math.Ceiling(Window.ActualWidth);
        var h = (int)Math.Ceiling(Window.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(ShotDir, name + ".png");
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    public IEnumerable<T> FindAll<T>(DependencyObject? root = null) where T : DependencyObject
    {
        root ??= Window;
        if (root is T hit) yield return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var r in FindAll<T>(VisualTreeHelper.GetChild(root, i)))
                yield return r;
    }

    /// <summary>Basligi verilen TabItem'i tasiyan TabControl'u bulur.</summary>
    public TabControl? TabControlWith(string header) =>
        FindAll<TabControl>().FirstOrDefault(tc => tc.Items.OfType<TabItem>().Any(i => (i.Header as string) == header));

    /// <summary>
    /// Yolculugu STA is parcaciginda, Application ve tema sozlukleri kurulu halde calistirir.
    /// <c>YP_LIVE_API</c> yoksa hicbir sey yapmaz. Yolculuk icinde firlayan istisna testi dusurur.
    /// </summary>
    public static void Run(Action<LiveUiHarness> journey, TimeSpan? timeout = null)
    {
        if (!Enabled) return;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var d in new[] { "DesignSystem", "Drawer", "PageShell" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri($"pack://application:,,,/Yemekhane.Desktop;component/Themes/{d}.xaml") });
                }
                var harness = new LiveUiHarness(ApiUrl!);
                try { journey(harness); }
                finally
                {
                    if (harness.Log.Count > 0)
                        File.WriteAllLines(Path.Combine(ShotDir, "journey-notes.txt"), harness.Log);
                    harness.Window.Close();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(timeout ?? TimeSpan.FromMinutes(10))) throw new TimeoutException("Canli yolculuk zaman asimina ugradi.");
        if (failure is not null) throw new InvalidOperationException("Canli yolculuk basarisiz: " + failure.Message, failure);
    }
}
