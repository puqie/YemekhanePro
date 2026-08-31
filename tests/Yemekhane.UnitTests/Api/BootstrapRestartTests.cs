using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Api.Authentication;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Bootstrap'in yeniden baslatmaya dayanikliligi.
///
/// Masaustu uygulamasi, API surecini baslatmadan ONCE veritabani dosyasinin varligina bakarak
/// bootstrap ortam degiskenlerini kurar. Dosyayi ise API'nin kendisi olusturur. Dolayisiyla ilk
/// kurulumdan sonra API her yeniden baslatildiginda (cokme kurtarma dongusu, kullanicinin
/// uygulamayi kapatip acmasi) ayni degiskenler hala doludur ama kullanici artik mevcuttur.
///
/// Bu durumda bootstrap'in patlamasi API'yi tamamen baslatmaz hale getirir: masaustu giris
/// penceresini gosterir, kullanici "Giris yap" der ve arkada API olmadigi icin HICBIR SEY OLMAZ.
/// Sahada gozlenen belirti tam olarak budur.
/// </summary>
public sealed class BootstrapRestartTests
{
    private static async Task<(YemekhaneDbContext Db, IPasswordHasher<User> Hasher, YemekhaneApiFactory Factory)>
        CreateAsync()
    {
        var factory = new YemekhaneApiFactory();
        _ = factory.Server;
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return (db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), factory);
    }

    private static InitialAdminBootstrapOptions Options() => new()
    {
        Enabled = true,
        Username = "admin",
        Password = "CokGuvenliParola123!"
    };

    [Fact]
    public async Task RestartWithBootstrapStillEnabledDoesNotCrashTheApi()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var bootstrapper = new InitialAdminBootstrapper(db, hasher, TimeProvider.System);

        // Ilk kurulum: yonetici olusur.
        await bootstrapper.BootstrapAsync(Options());
        var afterFirst = await db.Users.CountAsync();

        // Yeniden baslatma: masaustu ayni degiskenleri yine gecirir. API AYAKTA KALMALIDIR.
        await bootstrapper.BootstrapAsync(Options());

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task RestartDoesNotOverwriteTheExistingAdminPassword()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;
        var bootstrapper = new InitialAdminBootstrapper(db, hasher, TimeProvider.System);
        await bootstrapper.BootstrapAsync(Options());
        var original = await db.Users.AsNoTracking().SingleAsync();

        // Kullanici parolasini degistirdikten sonra yeniden baslatma, parolayi kurulum
        // parolasina geri DONDURMEMELIDIR; aksi halde bir yeniden baslatma sessizce
        // hesabi eski parolaya acar.
        var stored = await db.Users.SingleAsync();
        stored.PasswordHash = hasher.HashPassword(stored, "KullanicininYeniParolasi456!");
        await db.SaveChangesAsync();

        await bootstrapper.BootstrapAsync(Options());

        var after = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, after.Id);
        Assert.NotEqual(original.PasswordHash, after.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(after, after.PasswordHash, "KullanicininYeniParolasi456!"));
    }

    [Fact]
    public async Task DifferentUserAlreadyPresentStillRefusesToBootstrap()
    {
        var (db, hasher, factory) = await CreateAsync();
        await using var _ = factory;

        // Baska bir yonetici zaten varsa, bu bir yeniden baslatma degil yanlis yapilandirmadir:
        // sessizce ikinci bir yonetici acmak guvenlik acigidir.
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "mudur", NormalizedUsername = "MUDUR",
            PasswordHash = "x", IsActive = true, SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var bootstrapper = new InitialAdminBootstrapper(db, hasher, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrapper.BootstrapAsync(Options()));
        Assert.Equal(1, await db.Users.CountAsync());
    }
}
