using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Kabuk yolculuklari: global arama, bildirim merkezi, kisayol yardimi, sol menu ve
/// oturum suresi. Hepsi CALISAN yerel API'ye karsi, gercek ViewModel'lerle surulur.
/// </summary>
[Collection("UI")]
public class ShellJourney
{
    /// <summary>
    /// LiveUiHarness GlobalSearch/Notification DataContext'ini vermez ve katmanlari gizler;
    /// burada App.xaml.cs ile ayni sekilde kurulur ve gorunurluk baglamasi geri verilir.
    /// </summary>
    private static (GlobalSearchViewModel Search, NotificationCenterViewModel Notifications, DashboardRealtimeClient Realtime) Wire(LiveUiHarness ui)
    {
        var search = new GlobalSearchViewModel(new GlobalSearchApiClient(ui.Http, ui.Session), ui.Navigation, new MemoryRecentStore());
        ui.Window.GlobalSearchDataContext = search;
        var realtime = new DashboardRealtimeClient(new Uri(LiveUiHarness.ApiUrl!), ui.Session, TimeSpan.FromSeconds(2));
        var notifications = new NotificationCenterViewModel(new NotificationApiClient(ui.Http, ui.Session), realtime, ui.Navigation);
        ui.Window.NotificationDataContext = notifications;
        foreach (var name in new[] { "GlobalSearchHost", "NotificationHost" })
        {
            var layer = (FrameworkElement)ui.Window.FindName(name)!;
            // Harness yerel deger atadi; yerel deger XAML'deki IsOpen baglamasini SILER. Geri kurulur.
            BindingOperations.SetBinding(layer, UIElement.VisibilityProperty,
                new Binding("IsOpen") { Converter = new BooleanToVisibilityConverter() });
        }
        ui.Pump();
        return (search, notifications, realtime);
    }

    private static FrameworkElement Host(LiveUiHarness ui, string name) => (FrameworkElement)ui.Window.FindName(name)!;

    /// <summary>Gercek klavye olayi: PreviewKeyDown pencereden asagi tunel yapar, MainWindow.HandleShortcutKey bunu yakalar.</summary>
    private static void Key(LiveUiHarness ui, Key key, UIElement? target = null)
    {
        var element = target ?? ui.Window;
        var source = PresentationSource.FromVisual((Visual)element)!;
        element.RaiseEvent(new KeyEventArgs(InputManager.Current.PrimaryKeyboardDevice, source, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        ui.Pump();
    }

    private static bool Until(LiveUiHarness ui, Func<bool> condition, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < end) { ui.Delay(100); ui.Pump(2); }
        return condition();
    }

    /// <summary>Harness journey-notes.txt'yi her Run'da ezer; her yolculuk notlarini kendi dosyasina da yazar.</summary>
    internal static void Flush(LiveUiHarness ui, string name)
    {
        System.IO.Directory.CreateDirectory(LiveUiHarness.ShotDir);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(LiveUiHarness.ShotDir, $"notlar-{name}.txt"), ui.Log);
    }

    private static Button NavButton(LiveUiHarness ui, string tag) =>
        ((Panel)ui.Window.FindName("NavigationButtons")!).Children.OfType<Button>().Single(b => (string)b.Tag == tag);

    [Fact]
    public void GlobalArama() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var (search, _, _) = Wire(ui);
        var window = ui.Window;
        var target = (IShortcutCommandTarget)window;
        var box = (TextBox)window.FindName("GlobalSearchBox")!;
        var host = Host(ui, "GlobalSearchHost");

        Assert.Equal(ShortcutCommand.GlobalSearch, ShortcutCommandRouter.Map(new("K", true)));
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        Assert.True(search.IsOpen);
        Assert.Equal(Visibility.Visible, host.Visibility);
        Assert.Equal("Öğrenci, kart, sınıf, tarih veya modül arayın.", search.StatusText);
        ui.Shot("shell-arama-00-bos");

        search.Query = "a";
        Assert.True(Until(ui, () => !search.IsLoading && search.StatusText.Contains("en az 2")), $"1 karakter: {search.StatusText}");

        search.Query = "ali";
        Assert.True(Until(ui, () => search.Results.Count > 0 && !search.IsLoading), "ali sonuc gelmedi");
        ui.Shot("shell-arama-01-ali");
        var students = search.Results.Where(r => r.GroupTitle == "Öğrenciler").ToList();
        Assert.NotEmpty(students);
        ui.Note("Arama 'ali' gruplar: " + string.Join(", ", search.Results.Select(r => r.GroupTitle).Distinct()));
        // Ayni ad-soyadli ogrenciler: alt satir no + sinif/sube + kart tasimali ve satirlar birbirinden farkli olmali.
        Assert.NotEmpty(students.GroupBy(r => r.Title).Where(g => g.Count() > 1));
        foreach (var item in students) Assert.Matches(@"^No \d+ • .+ • Kart (\d+|yok)$", item.Subtitle);
        Assert.Equal(students.Count, students.Select(r => r.Subtitle).Distinct().Count());

        // ↑↓ gercek tuslarla, Enter dogru ogrenciyi acar.
        box.Focus();
        Key(ui, System.Windows.Input.Key.Down, box); Key(ui, System.Windows.Input.Key.Down, box);
        Assert.Equal(2, search.SelectedIndex);
        Key(ui, System.Windows.Input.Key.Up, box);
        Assert.Equal(1, search.SelectedIndex);
        var chosen = Guid.Parse(search.Results[1].Result.RouteParameters["id"]);
        Key(ui, System.Windows.Input.Key.Enter, box);
        Assert.True(Until(ui, () => ui.Students.Details?.Id == chosen, 15000), "Enter secili ogrenciyi acmadi");
        Assert.False(search.IsOpen);
        Assert.Equal(Visibility.Visible, Host(ui, "StudentsHost").Visibility);
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "students")));
        ui.Shot("shell-arama-02-ogrenci-detay");

        // Kart numarasi dogrudan ogrenci.
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        search.Query = "8350010";
        Assert.True(Until(ui, () => search.Results.Count > 0 && !search.IsLoading));
        var byCard = Assert.Single(search.Results);
        Assert.Equal("Öğrenciler", byCard.GroupTitle);
        Assert.Contains("Kart 8350010", byCard.Subtitle);
        ui.Shot("shell-arama-03-kart");

        // Tarih -> takvim.
        search.Query = "02.09.2026";
        Assert.True(Until(ui, () => search.Results.Count > 0 && !search.IsLoading));
        Assert.Equal("Takvim", search.Results[0].GroupTitle);
        Assert.Equal("2 Eylül 2026", search.Results[0].Title);
        Key(ui, System.Windows.Input.Key.Enter, box);
        Assert.True(Until(ui, () => Host(ui, "CalendarHost").Visibility == Visibility.Visible));
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "holiday-transfer")));
        ui.Shot("shell-arama-04-takvim");

        // Modul.
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        search.Query = "kasa";
        Assert.True(Until(ui, () => search.Results.Count > 0 && !search.IsLoading));
        Assert.Equal("Modüller", search.Results[0].GroupTitle);
        Key(ui, System.Windows.Input.Key.Enter, box);
        Assert.True(Until(ui, () => Host(ui, "CashHost").Visibility == Visibility.Visible));
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "cash")));

        // Esc kapatir.
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        Assert.True(search.IsOpen);
        Key(ui, System.Windows.Input.Key.Escape, box);
        Assert.False(search.IsOpen);
        Assert.Equal(Visibility.Collapsed, host.Visibility);

        // Hizli yazim: eski yanit geldiginde sonuc listesi bayat kalmamali.
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        search.Query = "ada"; search.Query = "ad"; search.Query = "ali";
        Assert.True(Until(ui, () => search.Results.Count > 0 && !search.IsLoading));
        ui.Delay(1500); ui.Pump();
        Assert.All(search.Results.Where(r => r.GroupTitle == "Öğrenciler"), r => Assert.DoesNotContain("ADA", r.Title));
        Assert.EndsWith("sonuç", search.StatusText);

        // Bos sorgu son aramalari gosterir.
        search.Query = "";
        Assert.True(Until(ui, () => search.StatusText == "Son aramalar"), "bos sorgu son aramalari gostermedi: " + search.StatusText);
        Assert.All(search.Results, r => Assert.Equal("Son aramalar", r.GroupTitle));
        ui.Shot("shell-arama-05-son-aramalar");
        Flush(ui, "arama");
    });

    [Fact]
    public void BildirimMerkezi() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var (_, notifications, realtime) = Wire(ui);
        var window = ui.Window;
        Assert.True(LiveUiHarness.Wait(realtime.ConnectAsync(), TimeSpan.FromSeconds(20)));
        Assert.True(LiveUiHarness.Wait(notifications.InitializeAsync(), TimeSpan.FromSeconds(20)));
        Assert.Null(notifications.Error);
        var unreadBefore = notifications.UnreadCount;
        var countBefore = notifications.Items.Count;
        ui.Note($"Bildirim baslangic: {countBefore} kayit, {unreadBefore} okunmamis");

        // Bildirim ureten gercek olay: yedek alma (BackupsController -> BackupCreated, rota "settings").
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/backups");
        request.Headers.Authorization = new("Bearer", ui.Session.AccessToken);
        var send = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(send, TimeSpan.FromSeconds(60)));
        Assert.True(send.Result.IsSuccessStatusCode, "yedek alinamadi: " + send.Result.StatusCode);

        // Arka planda cihaz hatasi bildirimleri de gelebilir (simulator olmayan cihazlar surekli
        // yeniden baglanmayi dener); bu yuzden tam esitlik degil, en az +1 ve yedek kaydinin gelisi aranir.
        Assert.True(Until(ui, () => notifications.UnreadCount >= unreadBefore + 1
            && notifications.Items.Any(i => i.Type == "BackupCreated" && i.ReadAt is null), 15000), $"rozet artmadi: {notifications.UnreadCount}");
        Assert.True(notifications.Items.Count >= countBefore + 1);
        Assert.True(notifications.HasUnread);
        ui.Note($"Bildirim: yedek sonrasi {notifications.Items.Count} kayit, {notifications.UnreadCount} okunmamis; ilk='{notifications.Items[0].Title}'");
        var badge = (TextBlock)ui.FindAll<TextBlock>((Button)window.FindName("NotificationButton")!).Last();
        Assert.Equal(notifications.UnreadCount.ToString(), badge.Text);

        notifications.ToggleCommand.Execute(null);
        Assert.True(Until(ui, () => notifications.IsOpen && !notifications.IsLoading));
        Assert.Equal(Visibility.Visible, Host(ui, "NotificationHost").Visibility);
        ui.Shot("shell-bildirim-01-liste");

        // Cihaz hatasi bildirimi "devices/{id}" rotasi tasir: tiklaninca Cihazlar ekrani acilmali.
        var deviceError = notifications.Items.FirstOrDefault(i => i.RelatedRoute?.StartsWith("devices/") == true);
        if (deviceError is null) ui.Note("UYARI: cihaz hatasi bildirimi yok, devices/{id} rotasi bu kosuda surulemedi");
        else
        {
            notifications.OpenCommand.Execute(deviceError); ui.Delay(1500); ui.Pump();
            Assert.False(notifications.IsOpen);
            Assert.Equal(Visibility.Visible, Host(ui, "DevicesHost").Visibility);
            Assert.Equal(Visibility.Collapsed, Host(ui, "DashboardHost").Visibility);
            Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "devices")));
            ui.Shot("shell-bildirim-02-cihaz-rotasi");
        }

        // Yedek bildirimi Ayarlar'a gider.
        var backup = notifications.Items.First(i => i.Type == "BackupCreated");
        notifications.OpenCommand.Execute(backup); ui.Delay(1500); ui.Pump();
        Assert.Equal(Visibility.Visible, Host(ui, "SettingsHost").Visibility);
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "settings")));
        Assert.NotNull(notifications.Items.First(i => i.Id == backup.Id).ReadAt);

        // Tumunu okundu isaretle -> rozet sifir.
        notifications.ToggleCommand.Execute(null);
        Assert.True(Until(ui, () => notifications.IsOpen && !notifications.IsLoading));
        if (notifications.UnreadCount > 0)
        {
            notifications.MarkAllReadCommand.Execute(null);
            Assert.True(Until(ui, () => notifications.UnreadCount == 0), "tumunu okundu isaretle rozeti sifirlamadi");
        }
        Assert.False(notifications.HasUnread);
        Assert.Equal(Visibility.Collapsed, badge.Parent is Border b ? b.Visibility : Visibility.Visible);
        ui.Shot("shell-bildirim-03-okundu");

        // Cevrimdisi: canli baglanti dusunce uyari metni gorunur.
        Assert.True(LiveUiHarness.Wait(realtime.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10)));
        Assert.True(Until(ui, () => notifications.IsOffline), "cevrimdisi bayragi kalkmadi");
        ui.Shot("shell-bildirim-04-cevrimdisi");
        Flush(ui, "bildirim");
    });

    [Fact]
    public void KisayolYardimiVeSolMenu() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var (search, _, _) = Wire(ui);
        var window = ui.Window;
        var target = (IShortcutCommandTarget)window;
        var help = Host(ui, "ShortcutHelpHost");

        // F1 acar, Esc kapatir.
        Key(ui, System.Windows.Input.Key.F1);
        Assert.Equal(Visibility.Visible, help.Visibility);
        var items = ((ItemsControl)window.FindName("ShortcutHelpList")!).ItemsSource.Cast<ShortcutHelpItem>().ToList();
        Assert.Equal(10, items.Count);
        ui.Shot("shell-kisayol-01-yardim");
        foreach (var item in items) ui.Note($"Kisayol {item.Gesture}: {item.Description} -> {item.Status} (etkin={item.IsEnabled})");
        Key(ui, System.Windows.Input.Key.Escape);
        Assert.Equal(Visibility.Collapsed, help.Visibility);

        // Listelenen her kisayol bir komuta eslesir.
        foreach (var item in items)
        {
            var control = item.Gesture.StartsWith("Ctrl+");
            var key = control ? item.Gesture[5..] : item.Gesture;
            Assert.NotNull(ShortcutCommandRouter.Map(new(key, control)));
        }

        // F2 Ogrenciler + arama odagi.
        Key(ui, System.Windows.Input.Key.F2);
        Assert.Equal(Visibility.Visible, Host(ui, "StudentsHost").Visibility);
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "students")));
        ui.Pump(4);
        var focused = Keyboard.FocusedElement as FrameworkElement;
        ui.Note($"F2 sonrasi odak: {focused?.GetType().Name} '{focused?.Name}'");

        // F3 kart okuma modali.
        Key(ui, System.Windows.Input.Key.F3);
        Assert.True(Until(ui, () => ui.Students.IsCardWorkflowOpen), "F3 kart okuma akisini acmadi");
        ui.Shot("shell-kisayol-02-kart-okuma");
        Key(ui, System.Windows.Input.Key.Escape);
        Assert.False(ui.Students.IsCardWorkflowOpen);

        // F4 Gunluk Takip.
        Key(ui, System.Windows.Input.Key.F4);
        Assert.Equal(Visibility.Visible, Host(ui, "DailyTrackingHost").Visibility);
        Assert.True(NavigationSelection.GetIsSelected(NavButton(ui, "daily-tracking")));

        // F5 gecerli gorunumu yeniler (Ogrenciler'de arama komutu).
        window.Navigate(ShellRoutes.Students); ui.Pump();
        Assert.True(target.CanExecute(ShortcutCommand.Refresh));
        Key(ui, System.Windows.Input.Key.F5);
        Assert.True(Until(ui, () => !ui.Students.IsLoading));
        Assert.True(ui.Students.Students.Count > 0);

        // Ctrl+P / Ctrl+E: yardimdaki durum gercek CanExecute ile ayni olmali (Raporlar'da).
        window.Navigate(ShellRoutes.Reports); ui.Pump();
        target.Execute(ShortcutCommand.Help); ui.Pump();
        var reportItems = ((ItemsControl)window.FindName("ShortcutHelpList")!).ItemsSource.Cast<ShortcutHelpItem>().ToList();
        Assert.Equal(target.CanExecute(ShortcutCommand.ExportPdf), reportItems.Single(i => i.Gesture == "Ctrl+P").IsEnabled);
        Assert.Equal(target.CanExecute(ShortcutCommand.ExportExcel), reportItems.Single(i => i.Gesture == "Ctrl+E").IsEnabled);
        ui.Note($"Raporlar'da Ctrl+P etkin={target.CanExecute(ShortcutCommand.ExportPdf)}, durum='{reportItems.Single(i => i.Gesture == "Ctrl+P").Status}'");
        Key(ui, System.Windows.Input.Key.Escape);

        // Ctrl+K palet; Enter secim yokken sorgu bos -> hicbir sey; Esc kapatir.
        target.Execute(ShortcutCommand.GlobalSearch); ui.Pump();
        Assert.True(search.IsOpen);
        Assert.True(target.CanExecute(ShortcutCommand.Activate));
        Key(ui, System.Windows.Input.Key.Escape, (TextBox)window.FindName("GlobalSearchBox")!);
        Assert.False(search.IsOpen);
        Assert.False(target.CanExecute(ShortcutCommand.Activate));

        // Metin kutusunda yazarken Ctrl+E / Ctrl+P disa aktarma tetiklememeli (yonlendirici guvenligi).
        var router = new ShortcutCommandRouter(target);
        Assert.False(router.TryExecute(new("E", true), new(false, IsTextInput: true, false)));
        Assert.False(router.TryExecute(new("P", true), new(false, IsTextInput: true, false)));

        // Sol menu: 13 oge, her biri dogru ekrana gider ve yalnizca o oge secili olur.
        var buttons = ((Panel)window.FindName("NavigationButtons")!).Children.OfType<Button>().ToList();
        Assert.Equal(13, buttons.Count);
        var hosts = new Dictionary<string, string>
        {
            ["dashboard"] = "DashboardHost", ["daily-tracking"] = "DailyTrackingHost", ["students"] = "StudentsHost",
            ["cash"] = "CashHost", ["entitlements"] = "MealEntitlementsHost", ["holiday-transfer"] = "CalendarHost",
            ["student-import"] = "StudentImportHost", ["devices"] = "DevicesHost", ["device-cards"] = "DeviceCardsHost",
            ["sms"] = "SmsHost", ["reports"] = "ReportsHost", ["settings"] = "SettingsHost", ["definitions"] = "DefinitionsHost",
        };
        foreach (var button in buttons)
        {
            var tag = (string)button.Tag;
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.Command.CanExecute(null), tag);
            button.Command.Execute(null); ui.Pump();
            Assert.Equal(Visibility.Visible, Host(ui, hosts[tag]).Visibility);
            Assert.Single(buttons.Where(NavigationSelection.GetIsSelected));
            Assert.True(NavigationSelection.GetIsSelected(button), tag);
        }
        ui.Shot("shell-menu-01-ayarlar-secili");

        // Izin kisitli kullanici: rotalar gizli, gizli rotaya Navigate acik Turkce hata verir (uygulama cokmez).
        var restricted = new ShellNavigationService([ShellRoutes.Dashboard, ShellRoutes.DailyTracking, ShellRoutes.Students, ShellRoutes.StudentDetail]);
        Assert.False(restricted.IsAvailable(ShellRoutes.Cash));
        var error = Assert.Throws<InvalidOperationException>(() => restricted.Navigate(ShellRoutes.Cash));
        Assert.Contains("henüz kullanıma açık değil", error.Message);
        var restrictedDashboard = new DashboardViewModel(new DashboardApiClient(ui.Http, ui.Session),
            new DashboardRealtimeClient(new Uri(LiveUiHarness.ApiUrl!), ui.Session), restricted, ui.Session);
        Assert.False(restrictedDashboard.CanNavigateCash);
        Assert.False(restrictedDashboard.CanNavigateReports);
        Assert.False(restrictedDashboard.CanNavigateSettings);

        // users-roles ayrilmis rota: hicbir yerde kayitli degil, Ayarlar dugmesi gizli.
        Assert.False(ui.Navigation.IsAvailable(ShellRoutes.UsersRoles));
        Assert.False(ui.Settings.CanNavigateUsers);
        Assert.False(ui.Settings.NavigateUsersCommand.CanExecute(null));
        Flush(ui, "kisayol-menu");
    });

    [Fact]
    public void OturumSuresiDolunca() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        var window = ui.Window;

        // Dolu bir form acikken oturum dusuyor.
        window.Navigate(ShellRoutes.StudentsCreate); ui.Pump();
        Assert.True(ui.Students.IsFormOpen, "yeni ogrenci formu acilmadi");
        ui.Students.FormFirstName = "Deneme";
        var token = ui.Session.AccessToken!;
        ui.Session.Set(token, DateTimeOffset.UtcNow.AddSeconds(-1));

        using var monitor = new SessionMonitor(ui.Session, TimeSpan.FromMilliseconds(200));
        var raised = 0;
        monitor.SessionExpired += (_, _) => { raised++; window.ShowSessionExpired(); };
        monitor.Start();
        Assert.True(Until(ui, () => raised == 1), "oturum suresi dolunca olay tetiklenmedi");
        Assert.True(window.IsSessionExpiredVisible, "oturum katmani gorunmedi");
        ui.Shot("shell-oturum-01-suresi-doldu");

        // Ekranlar ne gosteriyor?
        // RefreshCommand.CanExecute oturum dusunce false olur (Yenile dugmesi sessizce pasiflesir); yukleme dogrudan cagrilir.
        ui.Note($"Oturum dolunca Dashboard Yenile calistirilabilir mi: {ui.Dashboard.RefreshCommand.CanExecute(null)}");
        Assert.True(LiveUiHarness.Wait(ui.Dashboard.LoadAsync(), TimeSpan.FromSeconds(20)));
        Assert.True(ui.Dashboard.LoginRequired, $"dashboard LoginRequired degil: hata='{ui.Dashboard.ErrorMessage}' loading={ui.Dashboard.IsLoading}");
        ui.Students.SearchCommand.Execute(null); ui.Delay(1500); ui.Pump();
        ui.Cash.RefreshCommand.Execute(null); ui.Delay(1500); ui.Pump();
        ui.Devices.RefreshCommand.Execute(null); ui.Delay(1500); ui.Pump();
        ui.Note($"Oturum dolunca Dashboard.LoginRequired={ui.Dashboard.LoginRequired}; Ogrenciler='{ui.Students.ErrorMessage}'; Kasa='{ui.Cash.ErrorMessage}'; Cihazlar='{ui.Devices.ErrorMessage}'");
        Assert.Contains("oturum", ui.Students.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oturum", ui.Cash.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        // Kalici katman: ekran degistirilse de ustte kalir, olay ikinci kez tetiklenmez.
        ui.Delay(600); ui.Pump();
        Assert.Equal(1, raised);
        Assert.True(window.IsSessionExpiredVisible, "katman kapanmamali");

        // Yeniden giris: gercek API ile yeni belirtec, katman kapanir, form verisi yerinde.
        var login = new AuthenticationClient(ui.Http, ui.Session).LoginAsync("admin", "TestParola123!");
        Assert.True(LiveUiHarness.Wait(login, TimeSpan.FromSeconds(30)));
        ui.Session.Set(login.Result.AccessToken, login.Result.ExpiresAt);
        window.HideSessionExpired();
        Assert.True(ui.Dashboard.RefreshCommand.CanExecute(null), "yeniden giristen sonra Yenile pasif kaldi");
        ui.Dashboard.RefreshCommand.Execute(null);
        Assert.True(Until(ui, () => !ui.Dashboard.LoginRequired && ui.Dashboard.ShowContent, 15000), "yeniden giristen sonra dashboard acilmadi");
        Assert.True(ui.Students.IsFormOpen, "form kapanmis");
        Assert.Equal("Deneme", ui.Students.FormFirstName);
        Assert.False(window.IsSessionExpiredVisible, "katman kapanmadi");
        // Monitor yeniden silahlanir: bir sonraki dususte tekrar tetikler.
        ui.Delay(600); ui.Pump();
        Assert.Equal(1, raised);
        ui.Session.Set(ui.Session.AccessToken!, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.True(Until(ui, () => raised == 2), "monitor yeniden silahlanmadi");
        ui.Session.Set(login.Result.AccessToken, login.Result.ExpiresAt);
        window.HideSessionExpired();
        window.Navigate(ShellRoutes.Students); ui.Pump();
        ui.Shot("shell-oturum-02-yeniden-giris-form-korundu");
        Flush(ui, "oturum");
    });

    /// <summary>
    /// Giris penceresi gercek API'ye karsi: bos alan, yanlis parola, 5 yanlis -> kilit ipucu,
    /// dogru parola -> DialogResult=true. Kilit sonrasi kullanici satiri sqlite ile sifirlanir.
    /// </summary>
    [Fact]
    public void GirisPenceresiGercekApi()
    {
        if (!LiveUiHarness.Enabled) return;
        var dbPath = Environment.GetEnvironmentVariable("YP_LIVE_DB");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var d in new[] { "DesignSystem", "Drawer", "PageShell" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri($"pack://application:,,,/Yemekhane.Desktop;component/Themes/{d}.xaml") });
                    app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                }
                var http = new HttpClient { BaseAddress = new Uri(LiveUiHarness.ApiUrl!), Timeout = TimeSpan.FromSeconds(30) };
                var session = new MutableJwtSession();
                var window = new LoginWindow(new AuthenticationClient(http, session), initialAdmin: null, hasExistingDatabase: true)
                { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                var notes = new List<string>();
                var shots = new Func<string, string>(name => Shot(window, name));
                var button = FindButton(window, "Giriş yap");
                var password = (PasswordBox)window.FindName("PasswordBox")!;
                bool? result = null;
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        await Task.Yield();
                        Assert.Equal("admin", window.Username);
                        Assert.True(window.HasSetupMessage);
                        Assert.Equal('●', password.PasswordChar);
                        shots("giris-00-acilis");

                        // Bos parola.
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("Kullanıcı adı ve parola zorunludur.", window.ErrorMessage);
                        shots("giris-01-bos");

                        // Bos kullanici adi.
                        window.Username = ""; password.Password = "x";
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("Kullanıcı adı ve parola zorunludur.", window.ErrorMessage);
                        window.Username = "admin";

                        // Yanlis parola x5 -> Turkce mesaj, besincide kilit ipucu.
                        for (var attempt = 1; attempt <= 5; attempt++)
                        {
                            password.Password = "yanlis-parola-" + attempt;
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await UntilAsync(() => !window.IsBusy && window.HasError);
                            notes.Add($"Deneme {attempt}: {window.ErrorMessage}");
                            Assert.StartsWith("Kullanıcı adı veya parola geçersiz", window.ErrorMessage);
                            Assert.Equal(attempt >= LoginWindow.LockoutThreshold, window.ErrorMessage!.Contains("kilitlenmiş olabilir"));
                            Assert.Equal(0, password.Password.Length);
                        }
                        shots("giris-02-kilit-ipucu");

                        // Enter tusu = Giris (IsDefault): dogru parola ama hesap kilitli -> yine reddedilir.
                        password.Password = "TestParola123!";
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await UntilAsync(() => !window.IsBusy && window.HasError);
                        notes.Add("Kilitliyken dogru parola: " + window.ErrorMessage);
                        Assert.Contains("kilitlenmiş olabilir", window.ErrorMessage);

                        // Kilidi sqlite ile kaldir (API her giriste satiri yeniden okur).
                        ResetLockout(dbPath!);
                        password.Password = "TestParola123!";
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await UntilAsync(() => result is not null || (!window.IsBusy && window.HasError));
                        Assert.Null(window.ErrorMessage);
                    }
                    catch (Exception ex) { failure = ex; window.DialogResult = false; }
                };
                window.Closed += (_, _) => result = window.DialogResult;
                result = window.ShowDialog();
                if (failure is null)
                {
                    Assert.True(result, "dogru parola DialogResult=true vermedi");
                    Assert.True(session.IsAuthenticated);
                    System.IO.File.AppendAllLines(System.IO.Path.Combine(LiveUiHarness.ShotDir, "journey-notes.txt"), notes);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "giris yolculugu zaman asimi");
        if (failure is not null) throw new InvalidOperationException("Giris yolculugu basarisiz: " + failure.Message, failure);
    }

    private static async Task UntilAsync(Func<bool> condition, int milliseconds = 15000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(50);
        Assert.True(condition(), "kosul saglanmadi");
    }

    private static void ResetLockout(string dbPath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET LockoutEnd = NULL, FailedLoginAttempts = 0 WHERE NormalizedUsername = 'ADMIN'";
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static Button FindButton(DependencyObject root, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button && (string?)button.Content == content) return button;
            var nested = FindButtonOrNull(child, content);
            if (nested is not null) return nested;
        }
        throw new InvalidOperationException("dugme yok: " + content);
    }

    private static Button? FindButtonOrNull(DependencyObject root, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button && (string?)button.Content == content) return button;
            var nested = FindButtonOrNull(child, content);
            if (nested is not null) return nested;
        }
        return null;
    }

    internal static string Shot(Window window, string name)
    {
        System.IO.Directory.CreateDirectory(LiveUiHarness.ShotDir);
        window.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        var path = System.IO.Path.Combine(LiveUiHarness.ShotDir, name + ".png");
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed class MemoryRecentStore : IRecentSearchStore
    {
        private readonly List<RecentSearchEntry> values = [];
        public IReadOnlyList<RecentSearchEntry> Load() => values.Take(FileRecentSearchStore.Limit).ToArray();
        public void Add(RecentSearchEntry entry) { values.RemoveAll(v => v.Result.Title == entry.Result.Title); values.Insert(0, entry); }
    }
}
