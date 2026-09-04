using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Parola sifirlama formunun karar mantigi.
///
/// <para>
/// Bu kurallar CODE-BEHIND'DA DURSAYDI hicbir test goremezdi. Ayni bosluk daha once
/// <c>MakeFileClick</c> icinde yasandi: imzalama dali asimetrik moda gore
/// guncellendi ama on kosul eski HMAC sirrini istemeye devam etti ve 2000+ testin
/// hicbiri fark etmedi. Mantik bu yuzden ayri bir sinifa cikarildi.
/// </para>
/// </summary>
public sealed class PasswordResetFormTests
{
    private const string License = "{\"LicenseKey\":\"YMK-TEST\"}";
    private const string Strong = "YeniGuvenliParola456!";

    [Fact]
    public void EksiksizFormGonderilebilir()
    {
        var state = PasswordResetForm.Evaluate(License, "admin", Strong, Strong);

        Assert.True(state.CanSubmit);
        Assert.Empty(state.Hint);
    }

    /// <summary>Lisans dosyasi kanittir; secilmeden sifirlama istenemez.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LisansDosyasiSecilmedenGonderilemez(string? license)
    {
        var state = PasswordResetForm.Evaluate(license, "admin", Strong, Strong);

        Assert.False(state.CanSubmit);
        Assert.Contains("lisans", state.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KullaniciAdiBosBirakilamaz(string? username)
    {
        var state = PasswordResetForm.Evaluate(License, username, Strong, Strong);

        Assert.False(state.CanSubmit);
        Assert.Contains("kullanıcı", state.Hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arayuzdeki alt sinir SUNUCUDAKI ile ayni olmalidir: arayuz daha gevsek olsaydi
    /// kullanici formu doldurup sunucudan ret alirdi.
    /// </summary>
    [Fact]
    public void ArayuzAltSiniriSunucuylaAynidir() =>
        Assert.Equal(
            Yemekhane.Api.Authentication.PasswordResetService.MinimumPasswordLength,
            PasswordResetForm.MinimumPasswordLength);

    [Theory]
    [InlineData("kisa")]
    [InlineData("onbirkarak")]
    [InlineData("")]
    public void KisaParolaGonderilemez(string weak)
    {
        var state = PasswordResetForm.Evaluate(License, "admin", weak, weak);

        Assert.False(state.CanSubmit);
        Assert.Contains("12", state.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TutmayanParolalarGonderilemez()
    {
        var state = PasswordResetForm.Evaluate(License, "admin", Strong, Strong + "X");

        Assert.False(state.CanSubmit);
        Assert.Contains("aynı değil", state.Hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tekrar alani HENUZ BOSKEN "parolalar ayni degil" denmemelidir: kullanici daha
    /// yazmaya baslamamisken hata gostermek yanlis alarmdir ve formu bozuk gosterir.
    /// </summary>
    [Fact]
    public void TekrarAlaniBoskenYanlisAlarmVerilmez()
    {
        var state = PasswordResetForm.Evaluate(License, "admin", Strong, string.Empty);

        Assert.False(state.CanSubmit);
        Assert.Empty(state.Hint);
    }
}
