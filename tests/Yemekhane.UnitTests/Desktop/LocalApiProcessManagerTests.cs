using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Yerel API surecinin cokme sonrasi yeniden baslatilmasinda kimlik dogrulama durumunun korunmasini dogrular.
/// </summary>
public sealed class LocalApiProcessManagerTests
{
    [Fact]
    public async Task SigningKeyStaysStableAcrossRestarts()
    {
        // Cokme sonrasi yeniden baslatmada imzalama anahtari degisirse, acik oturumlarin tokenlari
        // dogrulanamaz hale gelir ve kullanici sessizce her istekte 401 alir.
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));

        var first = manager.BuildProcessEnvironment(databaseExists: true);
        var second = manager.BuildProcessEnvironment(databaseExists: true);

        Assert.Equal(first["YEMEKHANE_Authentication__Jwt__SigningKey"],
            second["YEMEKHANE_Authentication__Jwt__SigningKey"]);
        Assert.False(string.IsNullOrWhiteSpace(first["YEMEKHANE_Authentication__Jwt__SigningKey"]));
    }

    [Fact]
    public async Task BootstrapCredentialsAreOfferedOnlyWhenDatabaseIsAbsent()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));

        var withDatabase = manager.BuildProcessEnvironment(databaseExists: true);

        Assert.False(withDatabase.ContainsKey("YEMEKHANE_Authentication__Bootstrap__Enabled"));
        Assert.Null(manager.ConsumeInitialAdminCredentials());
    }

    [Fact]
    public async Task BootstrapCredentialsSurviveRestartUntilTheyAreConsumed()
    {
        // Ilk acilis: veritabani yok, bootstrap parolasi uretilir.
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5099"));
        var first = manager.BuildProcessEnvironment(databaseExists: false);
        var password = first["YEMEKHANE_Authentication__Bootstrap__Password"];

        // API bootstrap tamamlanmadan cokerse dosya olusmus olabilir; yeniden baslatmada
        // ayni parola tekrar gonderilmeli, aksi halde kullaniciya var olmayan bir parola gosterilir.
        var second = manager.BuildProcessEnvironment(databaseExists: true);

        Assert.Equal("true", second["YEMEKHANE_Authentication__Bootstrap__Enabled"]);
        Assert.Equal(password, second["YEMEKHANE_Authentication__Bootstrap__Password"]);
        Assert.Equal(password, manager.ConsumeInitialAdminCredentials()?.Password);
    }
}
