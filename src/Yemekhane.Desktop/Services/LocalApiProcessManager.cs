using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;

namespace Yemekhane.Desktop.Services;

public sealed class LocalApiProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly Uri baseUri;
    private readonly HttpClient healthClient;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim processLock = new(1, 1);
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private Process? process;
    private Task? recoveryTask;
    private bool ownsProcess;
    private InitialAdminCredentials? initialAdmin;

    public LocalApiProcessManager(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        this.baseUri = baseUri;
        healthClient = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(2) };
    }

    public bool IsManagedLocalEndpoint => baseUri.Scheme == Uri.UriSchemeHttp &&
        (IPAddress.TryParse(baseUri.Host, out var address) ? IPAddress.IsLoopback(address) :
             string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bootstrap kimlik bilgilerini bir kez teslim eder. Kurtarma dongusu bu alani arka planda
    /// yazdigi icin atomik takas kullanilir; aksi halde iki is parcacigi arasinda kayip guncelleme olusur.
    /// </summary>
    public InitialAdminCredentials? ConsumeInitialAdminCredentials() =>
        Interlocked.Exchange(ref initialAdmin, null);

    /// <summary>
    /// Alt surece gecirilecek ortam degiskenlerini uretir. Sureci baslatmadan test edilebilmesi icin ayrilmistir.
    /// </summary>
    /// <summary>Mevcut bir veritabaninin uzerine gelinip gelinmedigi.</summary>
    public bool HasExistingDatabase { get; private set; }

    public IReadOnlyDictionary<string, string> BuildProcessEnvironment(bool databaseExists)
    {
        // Mevcut kurulumun uzerine mi gelindi? Bootstrap parolasi yalnizca bos veritabaninda
        // uretilir; kullanici parolanin neden dolu gelmedigini bilmelidir.
        if (databaseExists && initialAdmin is null) HasExistingDatabase = true;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YEMEKHANE_MANAGED_CHILD"] = "1",
            // Anahtar surec omru boyunca sabittir: cokme sonrasi yeniden baslatmada degisirse
            // acik oturumlarin tokenlari dogrulanamaz hale gelir ve kullanici sessizce 401 alir.
            ["YEMEKHANE_Authentication__Jwt__SigningKey"] = signingKey
        };
        // Bootstrap parolasi bir kez uretilir ve kullaniciya gosterilene kadar korunur: API veritabani
        // dosyasini olusturduktan sonra bootstrap tamamlanmadan cokerse yeniden baslatmada dosya var
        // gorunur; parola burada dusurulurse kullaniciya karsiligi olmayan bir parola gosterilir.
        if (!databaseExists && initialAdmin is null)
        {
            initialAdmin = new InitialAdminCredentials("admin", Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
        }
        if (initialAdmin is not null)
        {
            values["YEMEKHANE_Authentication__Bootstrap__Enabled"] = "true";
            values["YEMEKHANE_Authentication__Bootstrap__Username"] = initialAdmin.Username;
            values["YEMEKHANE_Authentication__Bootstrap__Password"] = initialAdmin.Password;
        }
        return values;
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false)) return;
        if (!IsManagedLocalEndpoint)
            throw new InvalidOperationException($"Yapılandırılmış API erişilemiyor: {baseUri}");

        await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
        recoveryTask = RecoverOnCrashAsync(stopping.Token);
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is { HasExited: false }) return;
            process?.Dispose();
            var executable = Path.Combine(AppContext.BaseDirectory, "api", "Yemekhane.Api.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "Yerel API bulunamadı. Geliştirme ortamında API'yi ayrıca başlatın veya installer paketini kullanın.", executable);

            var startInfo = new ProcessStartInfo(executable)
            {
                Arguments = $"--urls \"{baseUri.GetLeftPart(UriPartial.Authority)}\"",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };
            var dataDirectory = Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve();
            foreach (var pair in BuildProcessEnvironment(File.Exists(Path.Combine(dataDirectory, "yemekhane.db"))))
                startInfo.Environment[pair.Key] = pair.Value;
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Yerel API işlemi başlatılamadı.");
            ownsProcess = true;
        }
        finally
        {
            processLock.Release();
        }
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(StartupTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (await IsHealthyAsync(timeout.Token).ConfigureAwait(false)) return;
            if (process is { HasExited: true })
                throw new InvalidOperationException($"Yerel API beklenmedik biçimde kapandı (çıkış kodu {process.ExitCode}).");
            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException($"Yerel API {StartupTimeout.TotalSeconds:0} saniye içinde hazır olmadı: {baseUri}");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await healthClient.GetAsync("health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task RecoverOnCrashAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var current = process;
            if (current is null) return;
            try
            {
                await current.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                await StartProcessAsync(cancellationToken).ConfigureAwait(false);
                await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (recoveryTask is not null)
        {
            try { await recoveryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var current = process;
        if (ownsProcess && current is { HasExited: false })
        {
            try
            {
                await current.StandardInput.WriteLineAsync("shutdown").ConfigureAwait(false);
                await current.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await current.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
                if (!current.HasExited) current.Kill(entireProcessTree: true);
            }
        }
        current?.Dispose();
        healthClient.Dispose();
        processLock.Dispose();
        stopping.Dispose();
    }
}

public sealed record InitialAdminCredentials(string Username, string Password);
