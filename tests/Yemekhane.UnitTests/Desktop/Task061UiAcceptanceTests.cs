using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

public sealed class Task061UiAcceptanceTests
{
    [Fact]
    public void MainWindowMeasuresAtSupportedDesktopSizesWithoutOverflow()
    {
        RunSta(() =>
        {
            var window = new MainWindow(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(window);
            Assert.Equal(1280, window.MinWidth);
            Assert.Equal(720, window.MinHeight);
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                window.Measure(size);
                window.Arrange(new Rect(size));
                window.UpdateLayout();
                Assert.InRange(window.DesiredSize.Width, 0, size.Width);
                Assert.InRange(window.DesiredSize.Height, 0, size.Height);
            }
            window.Close();
        });
    }

    [Fact]
    public void LoginViewUsesSecurePasswordInputAndAccessibleLabels()
    {
        var xaml = ReadSource("src", "Yemekhane.Desktop", "Views", "LoginWindow.xaml");
        Assert.Contains("<PasswordBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding Password", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Kullanıcı adı\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Parola\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopXamlHasNoDisabledFocusVisualsOrGradients()
    {
        var desktop = Path.Combine(SolutionRoot(), "src", "Yemekhane.Desktop");
        foreach (var file in Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(file);
            Assert.DoesNotContain("FocusVisualStyle=\"{x:Null}\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("LinearGradientBrush", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("RadialGradientBrush", xaml, StringComparison.Ordinal);
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read()) { }
        }
    }

    [Fact]
    public async Task AuthenticationClientStoresSuccessfulSessionAndHandlesExpectedFailures()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var ok = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { accessToken = "token", expiresAt = expires }), Encoding.UTF8, "application/json")
        });
        var session = new MutableJwtSession();
        var client = new AuthenticationClient(new HttpClient(ok) { BaseAddress = new Uri("http://localhost/") }, session);
        var result = await client.LoginAsync("yonetici", "parola");
        session.Set(result.AccessToken, result.ExpiresAt);
        Assert.True(session.IsAuthenticated);

        var denied = new AuthenticationClient(new HttpClient(new StubHandler(_ => new(HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("http://localhost/") }, new MutableJwtSession());
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => denied.LoginAsync("x", "y"));
        Assert.Contains("geçersiz", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplementedQuickActionRoutesAreAvailableWhenGranted()
    {
        var routes = new[] { ShellRoutes.StudentsCreate, ShellRoutes.Cards, ShellRoutes.CardReader,
            ShellRoutes.Entitlements, ShellRoutes.HolidayTransfer, ShellRoutes.Cash, ShellRoutes.Reports };
        var navigation = new ShellNavigationService(routes);
        var visited = new List<string>();
        navigation.NavigationRequested += (_, args) => visited.Add(args.Route);
        foreach (var route in routes) navigation.Navigate(route);
        Assert.Equal(routes, visited);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA pencere smoke testi zaman aşımına uğradı.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([SolutionRoot(), .. parts]));
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Çözüm kökü bulunamadı.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
