using System.Diagnostics;
using System.IO;
using System.Text;

namespace Yemekhane.KeyTool;

/// <summary>
/// Kurulum exesini uretir.
///
/// <para>
/// Neden burada: elle uretim su adimlari istiyordu -- acik anahtari kopyala, ortam
/// degiskenine yaz, PowerShell ac, betigi dogru parametrelerle cagir. Her adim
/// yanlis yapilabilirdi ve en tehlikelisi sessizdi: yanlis (ya da eksik) anahtarla
/// uretilen kurulum, ancak musteri lisansi yukleyip "calismiyor" dediginde ortaya
/// cikardi. Anahtar zaten bu programda oldugu icin kopyalamaya hic gerek yok.
/// </para>
/// </summary>
public static class InstallerBuilder
{
    /// <param name="Succeeded">Uretim basarili mi.</param>
    /// <param name="OutputPath">Uretilen kurulum exesi; basarisizsa null.</param>
    /// <param name="Log">Betigin tam ciktisi. Hata halinde kullaniciya gosterilir.</param>
    public sealed record Result(bool Succeeded, string? OutputPath, string Log);

    /// <summary>
    /// Depo kokunu bulur. Arac hem <c>dotnet run</c> ile kaynak agacindan hem de
    /// yayinlanmis klasorden calisabilir; betik yoksa null doner ve cagiran
    /// kullaniciya "depo bulunamadi" der -- sessizce basarisiz olmaz.
    /// </summary>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "build-installer.ps1"))
                && File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// Kurulum dosyasi adinin surumden onceki sabit kismi.
    ///
    /// <para>
    /// TEK KAYNAK: ad hem burada hem <c>Yemekhane.Bundle.wixproj</c> (OutputName)
    /// hem de <c>build-installer.ps1</c> icinde geciyor. Uc taraf dizeyi bagimsiz
    /// tekrar ettiginde biri yeniden adlandirilip otekiler geride kalabiliyor ve
    /// bu SESSIZ bir hata: kurulum uretilir, arac dosyayi bulamaz ve "basarisiz"
    /// der. C# tarafi artik bu sabiti paylasir; betik ve wixproj ile uyum
    /// <c>KurulumDosyasiAdiBetikWixprojVeAracArasindaAyni</c> testiyle baglanir.
    /// </para>
    /// </summary>
    public const string OutputNameStem = "YemekhaneProKurulum";

    /// <summary>Verilen surumun uretilecegi hedef dosya yolu.</summary>
    public static string OutputPathFor(string repositoryRoot, string version) =>
        Path.Combine(repositoryRoot, "artifacts", "installer", $"{OutputNameStem}-{version}.exe");

    /// <summary>Surum numarasi <c>1.2.3</c> bicimine uymali; betik de bunu dayatir.</summary>
    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$");

    /// <summary>
    /// Kurulumu uretir. Uzun surer (birkac dakika), bu yuzden cagiran arka planda
    /// calistirmalidir.
    /// </summary>
    /// <param name="publicKey">
    /// Kuruluma gomulecek ACIK anahtar. Ozel anahtar buraya ASLA verilmez; betik
    /// zaten reddeder ama cagiran da ozel anahtari gecirmemelidir.
    /// </param>
    public static async Task<Result> BuildAsync(
        string repositoryRoot, string version, string publicKey, CancellationToken cancellationToken)
    {
        var script = Path.Combine(repositoryRoot, "scripts", "build-installer.ps1");
        if (!File.Exists(script))
        {
            return new Result(false, null, $"Kurulum betigi bulunamadi: {script}");
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(version);
        // Acik anahtar KOMUT SATIRINDAN gecirilir, ortam degiskeninden degil: ortam
        // degiskeni surec agacindaki her cocuga sizar ve unutulup kalir.
        startInfo.ArgumentList.Add("-LicensingPublicKey");
        startInfo.ArgumentList.Add(publicKey);

        var log = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, args) => { if (args.Data is not null) log.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) log.AppendLine(args.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Result(false, null, "PowerShell calistirilamadi: " + exception.Message);
        }

        var output = OutputPathFor(repositoryRoot, version);
        // Cikis kodu 0 olsa BILE dosyanin varligi dogrulanir: betigin sessizce erken
        // donmesi durumunda "basarili" demek, olmayan bir exe'yi musteriye gondermeye
        // calismakla sonuclanirdi.
        var succeeded = process.ExitCode == 0 && File.Exists(output);
        return new Result(succeeded, succeeded ? output : null, log.ToString());
    }
}
