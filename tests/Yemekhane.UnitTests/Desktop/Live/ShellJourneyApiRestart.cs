using System.Diagnostics;
using System.IO;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Ust bardaki "Canlı" rozeti: API kapatilinca "Çevrimdışı"ya doner, API yeniden
/// basladiginda KENDILIGINDEN tekrar "Canlı" olur. Bu test API'yi gercekten oldurup
/// scripts/uitest-api.sh ile yeniden baslatir; <c>YP_LIVE_DATA_DIR</c> gerekir.
/// Diger yolculuklardan AYRI kosulmali (API'yi ~30 s kesintiye ugratir).
/// </summary>
[Collection("UI")]
public class ShellJourneyApiRestart
{
    [Fact]
    public void CanliRozetiApiYenidenBaslayincaBaglanir()
    {
        var dataDir = Environment.GetEnvironmentVariable("YP_LIVE_DATA_DIR");
        if (!LiveUiHarness.Enabled || string.IsNullOrWhiteSpace(dataDir)) return;
        var port = new Uri(LiveUiHarness.ApiUrl!).Port;
        var root = RepoRoot();

        LiveUiHarness.Run(ui =>
        {
            ui.LoadAll();
            Assert.True(Until(ui, () => ui.Dashboard.ConnectionText == "Canlı", 20000), "baslangicta canli degil: " + ui.Dashboard.ConnectionText);
            Assert.False(ui.Dashboard.IsOffline);
            ui.Shot("shell-canli-01-bagli");

            Run(root, "bash", $"scripts/uitest-api.sh stop {port}");
            Assert.True(Until(ui, () => !Healthy(port), 30000), "API durmadi");
            // WithAutomaticReconnect 0/2/10 s dener, sonra Closed -> "Çevrimdışı".
            Assert.True(Until(ui, () => ui.Dashboard.ConnectionText == "Çevrimdışı", 45000), "API kapaninca rozet: " + ui.Dashboard.ConnectionText);
            Assert.True(ui.Dashboard.IsOffline);
            ui.Shot("shell-canli-02-cevrimdisi");

            Run(root, "bash", $"scripts/uitest-api.sh start \"{dataDir}\" {port}");
            Assert.True(Until(ui, () => Healthy(port), 90000), "API yeniden baslamadi");
            Assert.True(Until(ui, () => ui.Dashboard.ConnectionText == "Canlı", 60000), "API donunce rozet: " + ui.Dashboard.ConnectionText);
            Assert.False(ui.Dashboard.IsOffline);
            ui.Shot("shell-canli-03-tekrar-bagli");
        }, TimeSpan.FromMinutes(6));
    }

    private static bool Until(LiveUiHarness ui, Func<bool> condition, int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < end) { ui.Delay(250); ui.Pump(2); }
        return condition();
    }

    private static void Run(string workingDirectory, string file, string arguments)
    {
        // ShellExecute ile baslatilir: betigin nohup ile ayaga kaldirdigi API sureci aksi halde
        // testhost'un stdout borusunu miras alir ve vstest, API kapanana kadar (yani hic)
        // kosunun bittigini goremez -- "dotnet test" sonsuza kadar asili kaliyordu.
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        { WorkingDirectory = workingDirectory, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        // Cikis kodu kanit sayilmaz (konsolsuz surecte netstat/taskkill farkli davranabilir);
        // sonuc /health ile dogrulanir.
        Assert.True(process.WaitForExit(120000), "betik zaman asimi: " + arguments);
    }

    private static bool Healthy(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { return false; }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "uitest-api.sh"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("depo koku bulunamadi");
    }
}
