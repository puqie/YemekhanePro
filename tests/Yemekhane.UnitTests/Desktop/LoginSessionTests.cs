using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.Views;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Oturum suresi, giris penceresindeki kilit ipucu ve lisans penceresi.
///
/// API belirteci 15 dakikada dolar ve yenileme ucu yoktur; onceden tek cikis yolu
/// uygulamayi kapatip acmakti. Kilitli hesaba API guvenlik geregi "parola gecersiz"
/// der; kullanici dogru parolayi yazip yine reddedilince nedenini bilemiyordu.
/// </summary>
[Collection("UI")]
public sealed class LoginSessionTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void SessionMonitorFiresOnceAndRearmsAfterRelogin() => UiThread.Run(() =>
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        var session = new MutableJwtSession();
        session.Set("token", clock.Now.AddMinutes(15));
        using var monitor = new SessionMonitor(session, TimeSpan.FromHours(1), clock, Dispatcher.CurrentDispatcher);
        var raised = 0;
        monitor.SessionExpired += (_, _) => raised++;

        monitor.Check();
        Assert.Equal(0, raised);

        clock.Now = clock.Now.AddMinutes(15);
        monitor.Check(); monitor.Check();
        // Tek olay: katman zaten acik, her 15 saniyede bir tekrar acmak gereksiz.
        Assert.Equal(1, raised);

        session.Set("yeni-token", clock.Now.AddMinutes(15));
        monitor.Check();
        Assert.Equal(1, raised);
        clock.Now = clock.Now.AddMinutes(16);
        monitor.Check();
        Assert.Equal(2, raised);
    });

    [Fact]
    public void SessionMonitorTreatsMissingTokenAsExpired() => UiThread.Run(() =>
    {
        using var monitor = new SessionMonitor(new MutableJwtSession(), TimeSpan.FromHours(1), dispatcher: Dispatcher.CurrentDispatcher);
        var raised = 0;
        monitor.SessionExpired += (_, _) => raised++;
        monitor.Check();
        Assert.Equal(1, raised);
    });

    [Fact]
    public void FifthConsecutiveFailureExplainsThePossibleLockout() => UiThread.Run(() =>
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("{\"message\":\"Kullanıcı adı veya parola geçersiz.\"}", Encoding.UTF8, "application/json") });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5555") };
        var window = new LoginWindow(new AuthenticationClient(http, new MutableJwtSession()), hasExistingDatabase: true);
        UiThread.ApplyResources(window);
        var button = FindButton(window, "Giriş yap");
        var password = (PasswordBox)window.FindName("PasswordBox")!;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            password.Password = "yanlis";
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !window.IsBusy && window.HasError);
            Assert.StartsWith("Kullanıcı adı veya parola geçersiz", window.ErrorMessage);
            Assert.Equal(attempt >= LoginWindow.LockoutThreshold, window.ErrorMessage!.Contains(LoginWindow.LockoutHint));
        }
        Assert.Equal(5, handler.Calls);
    });

    /// <summary>
    /// Kurulum seridi + kilit ipuclu hata seridi birlikte gorununce "Giriş yap" dugmesi
    /// pencerenin altina tasiyordu (920x570'te ekran goruntusunde kesik cikti).
    /// </summary>
    [Fact]
    public void LoginButtonStaysVisibleWhenBothBannersAreShown() => UiThread.Run(() =>
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5555") };
        var window = new LoginWindow(new AuthenticationClient(http, new MutableJwtSession()), hasExistingDatabase: true)
        { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        // En kucuk izinli yukseklikte denenir: tasma orada baslar.
        window.Height = window.MinHeight;
        UiThread.ApplyResources(window);
        var button = FindButton(window, "Giriş yap");
        var password = (PasswordBox)window.FindName("PasswordBox")!;
        window.Show();
        try
        {
            PumpUntil(() => window.IsLoaded);
            for (var attempt = 0; attempt < LoginWindow.LockoutThreshold; attempt++)
            {
                password.Password = "yanlis";
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                PumpUntil(() => !window.IsBusy && window.HasError);
            }
            Assert.Contains(LoginWindow.LockoutHint, window.ErrorMessage);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bottom = button.TransformToAncestor(content).Transform(new Point(0, button.ActualHeight)).Y;
            // Ya dugme gorunur alandadir ya da sutun kaydirilabilir (kullanici ona ulasabilir);
            // ikisi de degilse dugme sessizce kesilmis demektir.
            var scroll = FindAll<ScrollViewer>(window).FirstOrDefault(v => v.IsAncestorOf(button));
            Assert.True(bottom <= content.ActualHeight || scroll is { ScrollableHeight: > 0 },
                $"dugme alt kenari {bottom:F0} > pencere {content.ActualHeight:F0} ve kaydirma yok");
            Assert.True(button.ActualHeight > 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ReloginModePrefillsUsernameAndExplainsWhy() => UiThread.Run(() =>
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))) { BaseAddress = new Uri("http://127.0.0.1:5555") };
        var window = new LoginWindow(new AuthenticationClient(http, new MutableJwtSession()), reloginUsername: "memur");
        UiThread.ApplyResources(window);

        Assert.Equal("memur", window.Username);
        Assert.Equal(LoginWindow.ReloginMessage, window.SetupMessage);
        Assert.True(window.HasSetupMessage);
        Assert.Contains("formlar korunur", window.SetupMessage);
    });

    [Fact]
    public void ActivationWindowShowsMachineIdValidatesEmptyKeyAndReportsServerReason() => UiThread.Run(() =>
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://lisans.invalid/") };
        var service = new LicenseService(new FakeStore(), new FakeFingerprintReader(), new HttpLicenseActivationClient(http),
            TimeProvider.System, "test-imza-anahtari");
        var check = service.Check();
        Assert.False(check.IsValid);
        var window = new ActivationWindow(service, check, "ABCD-1234-EF56")
        { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        UiThread.ApplyResources(window);
        var button = FindButton(window, "Etkinleştir");
        var keyBox = (TextBox)window.FindName("LicenseKeyBox")!;
        // Baglamalar pencere yuklenince cozulur (uretimde ShowDialog); ekran disinda gosterilir.
        window.Show();
        PumpUntil(() => window.IsLoaded);
        try
        {
        // Makine kimligi kopyalanabilir: salt okunur TextBox, secilebilir.
        var machineBox = FindAll<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Bilgisayar kimliği");
        Assert.Equal("ABCD-1234-EF56", machineBox.Text);
        Assert.True(machineBox.IsReadOnly);
        Assert.False(window.HasError);
        Assert.False(string.IsNullOrWhiteSpace(window.StatusMessage));

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("Lütfen lisans anahtarınızı girin.", window.ErrorMessage);

        keyBox.Text = "YANLIS-ANAHTAR";
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpUntil(() => !window.IsBusy && window.HasError);
        Assert.Contains("bulunamadi", window.ErrorMessage);
        Assert.True(window.IsEditable);
        }
        finally { window.Close(); }
    });

    private static void PumpUntil(Func<bool> condition, int milliseconds = 10000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < end)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Assert.True(condition(), "kosul saglanmadi");
    }

    private static Button FindButton(DependencyObject root, string content) =>
        FindAll<Button>(root).First(button => (string?)button.Content == content);

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit) yield return hit;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }

    private sealed class FakeStore : ILicenseStore
    {
        public StoredLicense? Current { get; private set; }
        public StoredLicense? Load() => Current;
        public void Save(StoredLicense license) => Current = license;
        public void Clear() => Current = null;
    }

    private sealed class FakeFingerprintReader : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => new([FingerprintHasher.Hash("ANAKART-1"), FingerprintHasher.Hash("DISK-1"), FingerprintHasher.Hash("GUID-1")]);
    }
}
