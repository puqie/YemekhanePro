using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Installer;

public sealed class Task062InstallerTests
{
    [Fact]
    public void ManagedApiAcceptsOnlyLocalHttpEndpoint()
    {
        Assert.True(new LocalApiProcessManager(new Uri("http://127.0.0.1:5255/")).IsManagedLocalEndpoint);
        Assert.True(new LocalApiProcessManager(new Uri("http://localhost:5255/")).IsManagedLocalEndpoint);
        Assert.False(new LocalApiProcessManager(new Uri("https://server.example/")).IsManagedLocalEndpoint);
        Assert.False(new LocalApiProcessManager(new Uri("http://192.0.2.10:5255/")).IsManagedLocalEndpoint);
    }

    [Fact]
    public void InstallerContractKeepsDataOutOfProgramFilesPayload()
    {
        var root = FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "Package.wxs"));

        Assert.Contains("MajorUpgrade", source, StringComparison.Ordinal);
        Assert.Contains("ProgramFiles6432Folder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAppDataFolder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("yemekhane.db", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SigningKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBuildIsSelfContainedAndNotTrimmed()
    {
        var script = File.ReadAllText(Path.Combine(FindRoot(), "scripts", "build-installer.ps1"));
        Assert.Contains("--self-contained', 'true'", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishTrimmed=false", script, StringComparison.Ordinal);
        Assert.Contains("installer-validation", script, StringComparison.Ordinal);
        Assert.Contains("NotoSans-OFL.txt", script, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
