using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Api.Authentication;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Lisans dosyasiyla parola sifirlama.
///
/// <para>
/// Neden var: parolayi unutan okul programa HIC giremiyordu. Yeniden kurmak da
/// kurtarmiyor cunku bootstrap yalnizca BOS kullanici tablosunda calisir; tek cikis
/// veritabanini silmekti, o da tum verinin kaybi demekti.
/// </para>
/// <para>
/// Kanit olarak saticinin urettigi .lic dosyasi istenir: imzasi ve makine bagi
/// dogrulanir. Boylece okul saticiyi beklemeden kurtulur, ama dosyasi olmayan biri
/// sifirlayamaz.
/// </para>
/// </summary>
public sealed class PasswordResetTests
{
    private const string Username = "admin";
    private const string OldPassword = "EskiParola123!";
    private const string NewPassword = "YeniGuvenliParola456!";

    private static async Task<(YemekhaneDbContext Db, IPasswordHasher<User> Hasher, YemekhaneApiFactory Factory)>
        CreateAsync()
    {
        var factory = new YemekhaneApiFactory();
        _ = factory.Server;
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        await new InitialAdminBootstrapper(db, hasher, TimeProvider.System).BootstrapAsync(new()
        {
            Enabled = true,
            Username = Username,
            Password = OldPassword
        });
        return (db, hasher, factory);
    }

    /// <summary>Bu makineye kilitli, gecerli imzali bir lisans uretir.</summary>
    private static (string Content, string PublicKey, IReadOnlyList<string> Hashes) IssueLicense()
    {
        var pair = LicenseKeyPairFactory.Create();
        IReadOnlyList<string> hashes = ["AA11", "BB22", "CC33"];
        var issuedAt = DateTimeOffset.UtcNow;
        var key = OfflineLicenseKey.Create(issuedAt, pair.PrivateKey);
        var payload = LicenseSignature.BuildPayload(key, [.. hashes], issuedAt, null);
        var license = new StoredLicense(key, "Deneme Okulu", "Standart", [.. hashes], issuedAt,
            ExpiresAt: null, LastValidatedAt: issuedAt,
            LicenseKeyPairFactory.Sign(payload, pair.PrivateKey));
        return (LicenseFile.Write(license), pair.PublicKey, hashes);
    }

    private static PasswordResetService Service(
        YemekhaneDbContext db, IPasswordHasher<User> hasher, string publicKey, IReadOnlyList<string> machineHashes) =>
        new(db, hasher, TimeProvider.System, publicKey, new HardwareFingerprint([.. machineHashes]));

    [Fact]
    public async Task GecerliLisansDosyasiParolayiSifirlar()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        var result = await Service(db, hasher, publicKey, hashes)
            .ResetAsync(content, Username, NewPassword, default);

        Assert.True(result.Succeeded);
        var user = await db.Users.SingleAsync();
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, user.PasswordHash, NewPassword));
    }

    /// <summary>
    /// Eski parola ARTIK CALISMAMALIDIR. Sifirlama yeni parolayi eklerken eskisini
    /// birakirsa, parolayi ele geciren kisi erisimini surdurur.
    /// </summary>
    [Fact]
    public async Task SifirlamaSonrasiEskiParolaCalismaz()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        await Service(db, hasher, publicKey, hashes).ResetAsync(content, Username, NewPassword, default);

        var user = await db.Users.SingleAsync();
        Assert.Equal(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash, OldPassword));
    }

    /// <summary>
    /// SecurityStamp yenilenmelidir: JWT icinde tasinir, sifirlamadan ONCE alinmis
    /// jetonlar aksi halde gecerli kalir ve sifirlama saldirganin oturumunu kapatmaz.
    /// </summary>
    [Fact]
    public async Task SifirlamaGuvenlikDamgasiniYeniler()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();
        var before = (await db.Users.SingleAsync()).SecurityStamp;

        await Service(db, hasher, publicKey, hashes).ResetAsync(content, Username, NewPassword, default);

        db.ChangeTracker.Clear();
        Assert.NotEqual(before, (await db.Users.SingleAsync()).SecurityStamp);
    }

    /// <summary>Kilitli hesap sifirlamayla ACILMALIDIR; yoksa okul yine giremez.</summary>
    [Fact]
    public async Task SifirlamaHesapKilidiniAcar()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();
        var locked = await db.Users.SingleAsync();
        locked.FailedLoginAttempts = 5;
        locked.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();

        await Service(db, hasher, publicKey, hashes).ResetAsync(content, Username, NewPassword, default);

        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync();
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.FailedLoginAttempts);
    }

    /// <summary>
    /// BASKA bir saticinin anahtariyla imzalanmis dosya reddedilmelidir; aksi halde
    /// kendi anahtar cifti olan herkes .lic uretip her kurulumu ele gecirir.
    /// </summary>
    [Fact]
    public async Task BaskaAnahtarlaImzalanmisLisansReddedilir()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, _, hashes) = IssueLicense();
        var yabanci = LicenseKeyPairFactory.Create().PublicKey;

        var result = await Service(db, hasher, yabanci, hashes)
            .ResetAsync(content, Username, NewPassword, default);

        Assert.False(result.Succeeded);
        var user = await db.Users.SingleAsync();
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, user.PasswordHash, OldPassword));
    }

    /// <summary>Baska bilgisayara ait lisans bu makinede sifirlama yapamaz.</summary>
    [Fact]
    public async Task BaskaMakineninLisansiReddedilir()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, _) = IssueLicense();

        var result = await Service(db, hasher, publicKey, ["FF99", "EE88", "DD77"])
            .ResetAsync(content, Username, NewPassword, default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task BozukLisansDosyasiReddedilir()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (_, publicKey, hashes) = IssueLicense();

        var result = await Service(db, hasher, publicKey, hashes)
            .ResetAsync("bu bir lisans dosyasi degil", Username, NewPassword, default);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Kisa parola reddedilmelidir: kullanici kendi parolasini sectigi icin tek
    /// koruma budur.
    /// </summary>
    [Theory]
    [InlineData("kisa")]
    [InlineData("onbirkarak")]
    [InlineData("")]
    public async Task KisaParolaReddedilir(string zayif)
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        var result = await Service(db, hasher, publicKey, hashes)
            .ResetAsync(content, Username, zayif, default);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Olmayan kullanici adi icin sifirlama BASARISIZ olmali ama var olan hesaplari
    /// da bozmamalidir.
    /// </summary>
    [Fact]
    public async Task BilinmeyenKullaniciReddedilir()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        var result = await Service(db, hasher, publicKey, hashes)
            .ResetAsync(content, "boyle-biri-yok", NewPassword, default);

        Assert.False(result.Succeeded);
        var user = await db.Users.SingleAsync();
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, user.PasswordHash, OldPassword));
    }

    /// <summary>
    /// Sifirlama DENETIM KAYDINA yazilmalidir. Yazilmazsa, lisans dosyasina erisen
    /// birinin hesap devralmasi izsiz kalir; bu tasarimda kabul edilen guvenlik
    /// dengesinin karsiligi tam olarak bu kayittir.
    /// </summary>
    [Fact]
    public async Task SifirlamaDenetimKaydiYazar()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        await Service(db, hasher, publicKey, hashes).ResetAsync(content, Username, NewPassword, default);

        var log = await db.Set<AuditLog>().SingleOrDefaultAsync(entry => entry.Action == "PasswordReset");
        Assert.NotNull(log);
        Assert.Contains(Username, log!.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Denetim kaydi yeni parolayi ASLA icermemelidir.</summary>
    [Fact]
    public async Task DenetimKaydiParolaSizdirmaz()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var (content, publicKey, hashes) = IssueLicense();

        await Service(db, hasher, publicKey, hashes).ResetAsync(content, Username, NewPassword, default);

        var log = await db.Set<AuditLog>().SingleAsync(entry => entry.Action == "PasswordReset");
        var all = string.Join(' ', log.Description, log.BeforeJson, log.AfterJson);
        Assert.DoesNotContain(NewPassword, all, StringComparison.Ordinal);
    }
}
