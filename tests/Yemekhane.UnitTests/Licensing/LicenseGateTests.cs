using Microsoft.Extensions.Configuration;
using Yemekhane.Desktop.Services;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class LicenseGateTests
{
    [Fact]
    public async Task AnUnlicensedInstallationIsNotAllowedToStart()
    {
        // Lisans gecersizse yerel API HIC BASLAMAMALIDIR: veritabani, turnike
        // baglantilari ve zamanlayici servisler ayaga kalkmamalidir.
        var decision = await LicenseGate.EvaluateAsync(
            new FakeLicenseService(new LicenseCheck(LicenseStatus.NotActivated, "Lisans bulunamadi.")));

        Assert.False(decision.Allowed);
        Assert.Equal(LicenseStatus.NotActivated, decision.Check.Status);
    }

    [Fact]
    public async Task AnInvalidLicenseIsNotSentToTheServerBeforeLockingTheApplication()
    {
        // Yerel olarak zaten gecersizse sunucuya sormanin anlami yok; kullanici
        // dogrudan aktivasyon ekranina duser.
        var service = new FakeLicenseService(new LicenseCheck(LicenseStatus.Tampered, "Kurcalanmis."));

        await LicenseGate.EvaluateAsync(service);

        Assert.Equal(0, service.ValidateCallCount);
    }

    [Fact]
    public async Task AValidLicenseIsRefreshedAgainstTheServer()
    {
        var service = new FakeLicenseService(
            new LicenseCheck(LicenseStatus.Valid, "Gecerli."),
            new LicenseCheck(LicenseStatus.Valid, "Gecerli."));

        var decision = await LicenseGate.EvaluateAsync(service);

        Assert.True(decision.Allowed);
        Assert.Equal(1, service.ValidateCallCount);
    }

    [Fact]
    public async Task ARevokedLicenseDiscoveredAtStartupBlocksTheApplication()
    {
        // Yerel dosya gecerli GORUNSE bile sunucu iptal dediyse uygulama acilmaz.
        var service = new FakeLicenseService(
            new LicenseCheck(LicenseStatus.Valid, "Gecerli."),
            new LicenseCheck(LicenseStatus.Revoked, "Iptal edilmis."));

        var decision = await LicenseGate.EvaluateAsync(service);

        Assert.False(decision.Allowed);
        Assert.Equal(LicenseStatus.Revoked, decision.Check.Status);
    }

    [Fact]
    public async Task AnOfflineSchoolStillOpensTheApplication()
    {
        // Okulun interneti kesildiginde yemek dagitimi DURMAMALIDIR.
        var service = new FakeLicenseService(
            new LicenseCheck(LicenseStatus.Valid, "Gecerli.", "23 gun icinde dogrulanmali."),
            new LicenseCheck(LicenseStatus.Valid, "Gecerli.", "23 gun icinde dogrulanmali."));

        var decision = await LicenseGate.EvaluateAsync(service);

        Assert.True(decision.Allowed);
        Assert.NotNull(decision.Check.Warning);
    }

    [Fact]
    public void AMissingSigningSecretIsReportedInsteadOfSilentlyAcceptingEveryLicense()
    {
        // Imza anahtari bos birakilirsa her lisans dosyasi gecerli sayilabilirdi.
        // Bu sessizce gecilmez; yapilandirma hatasi acikca soylenir.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:ActivationUri"] = "https://lisans.ornek.test/api/",
            ["Licensing:SigningSecret"] = ""
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => LicenseGate.CreateService(configuration, Path.GetTempPath()));

        Assert.Contains("SigningSecret", exception.Message);
    }

    [Fact]
    public void AMalformedActivationUriIsReportedAtStartup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:ActivationUri"] = "bu-bir-uri-degil",
            ["Licensing:SigningSecret"] = "gizli"
        }).Build();

        Assert.Throws<InvalidOperationException>(
            () => LicenseGate.CreateService(configuration, Path.GetTempPath()));
    }

    /// <summary>Ilk cagri <see cref="ILicenseService.Check"/>, ikincisi ValidateAsync sonucudur.</summary>
    private sealed class FakeLicenseService(LicenseCheck check, LicenseCheck? validated = null) : ILicenseService
    {
        public int ValidateCallCount { get; private set; }

        public LicenseCheck Check() => check;

        public Task<LicenseCheck> ValidateAsync(CancellationToken cancellationToken = default)
        {
            ValidateCallCount++;
            return Task.FromResult(validated ?? check);
        }

        public Task<LicenseCheck> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(check);
    }
}
