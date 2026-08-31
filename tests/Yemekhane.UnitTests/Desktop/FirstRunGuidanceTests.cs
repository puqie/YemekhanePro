using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ilk acilis yonlendirmesi.
///
/// Bootstrap parolasi yalnizca bos veritabaninda uretilir. Mevcut bir kurulumun uzerine
/// gelindiginde parola dolu gelmez -- ve kullanici NEDEN gelmedigini bilemezse ne yapacagini
/// da bilemez. Sessiz davranis, hata mesajindan beterdir.
/// </summary>
public sealed class FirstRunGuidanceTests
{
    [Fact]
    public async Task FreshInstallProducesBootstrapCredentials()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));

        var environment = manager.BuildProcessEnvironment(databaseExists: false);

        Assert.Equal("true", environment["YEMEKHANE_Authentication__Bootstrap__Enabled"]);
        Assert.NotNull(manager.ConsumeInitialAdminCredentials());
    }

    [Fact]
    public async Task ExistingDatabaseIsReportedSoTheUserKnowsWhyThePasswordIsBlank()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));

        manager.BuildProcessEnvironment(databaseExists: true);

        Assert.True(manager.HasExistingDatabase);
        Assert.Null(manager.ConsumeInitialAdminCredentials());
    }

    [Fact]
    public async Task FreshInstallIsNotReportedAsExisting()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));

        manager.BuildProcessEnvironment(databaseExists: false);

        Assert.False(manager.HasExistingDatabase);
    }
}
