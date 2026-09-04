using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Licensing;

namespace Yemekhane.Api.Authentication;

/// <param name="Succeeded">Sifirlama yapildi mi.</param>
/// <param name="Message">Kullaniciya gosterilecek SOMUT Turkce sebep.</param>
public sealed record PasswordResetResult(bool Succeeded, string Message);

/// <summary>
/// Lisans dosyasiyla parola sifirlama.
///
/// <para>
/// NEDEN VAR: parolasini unutan okul programa hic giremiyordu. Yeniden kurmak da
/// kurtarmiyordu -- <see cref="InitialAdminBootstrapper"/> yalnizca BOS kullanici
/// tablosunda calisir. Geriye tek yol veritabanini silmek kaliyordu, o da tum
/// verinin kaybi demekti.
/// </para>
/// <para>
/// KANIT olarak saticinin urettigi <c>.lic</c> dosyasi istenir. Dosyanin imzasi
/// kuruluma gomulu ACIK ANAHTARLA dogrulanir ve makine bagina bakilir; boylece
/// okul saticiyi beklemeden kurtulur ama dosyasi olmayan biri sifirlayamaz.
/// </para>
/// <para>
/// DOGRULAMA BURADA YAPILIR, masaustune guvenilmez: API localhost'ta dinler ve
/// dogrudan istek atan biri masaustunu tamamen atlayabilir. "Masaustu zaten
/// dogruladi" varsayimi, korumayi hicbir sey yapmayan bir suse cevirirdi.
/// </para>
/// <para>
/// KABUL EDILEN DENGE: <c>.lic</c> dosyasina VE makineye erisen biri her hesabin
/// parolasini degistirebilir. Bu bilincli bir secimdir (o kisi zaten makinenin
/// basindadir), karsiligi ise her sifirlamanin DENETIM KAYDINA yazilmasidir --
/// kayit olmadan sessiz hesap devralmasi izsiz kalirdi.
/// </para>
/// </summary>
public sealed class PasswordResetService(
    YemekhaneDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider,
    string? publicKey,
    HardwareFingerprint fingerprint)
{
    /// <summary>
    /// En kisa parola uzunlugu. Kullanici parolasini KENDI sectigi icin tek koruma
    /// budur; bootstrap'in urettigi parolalarda boyle bir risk yoktu.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    private const string InvalidLicenseMessage =
        "Lisans dosyası bu bilgisayar için geçerli değil. Satıcınızın bu bilgisayar için ürettiği .lic dosyasını seçin.";

    public async Task<PasswordResetResult> ResetAsync(
        string? licenseFileContent,
        string? username,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        // 1) PAROLA once denetlenir: gecersiz parolayla gelen istek, lisans gecerli
        //    olsa bile hicbir sey degistirmemelidir.
        var password = newPassword ?? string.Empty;
        if (password.Length < MinimumPasswordLength)
            return new(false, $"Yeni parola en az {MinimumPasswordLength} karakter olmalıdır.");

        // 2) LISANS dogrulanir. Imza gecersizse ya da dosya baska bir makineye
        //    aitse burada durulur.
        if (!IsLicenseValid(licenseFileContent))
            return new(false, InvalidLicenseMessage);

        // 3) KULLANICI bulunur.
        var normalized = LoginService.NormalizeUsername(username);
        var user = normalized.Length == 0
            ? null
            : await dbContext.Users.SingleOrDefaultAsync(
                candidate => candidate.NormalizedUsername == normalized, cancellationToken);
        if (user is null)
            return new(false, "Bu kullanıcı adına sahip bir hesap bulunamadı.");

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        // Guvenlik damgasi JWT icinde tasinir: yenilenmezse sifirlamadan ONCE alinmis
        // jetonlar gecerli kalir ve sifirlama saldirganin oturumunu KAPATMAZ.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        // Hesap kilitliyse acilir; yoksa okul dogru parolayi bilse de giremezdi.
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = now;

        // Denetim kaydi AYNI SaveChanges icinde yazilir: ayri kaydedilseydi, arada
        // olusan bir hata parolayi degistirip kaydi dusurebilirdi.
        //
        // Parolanin kendisi ve hash'i KAYDA GIRMEZ; yalnizca hangi hesabin ne zaman
        // sifirlandigi yazilir.
        dbContext.Set<AuditLog>().Add(new AuditLog
        {
            UserId = user.Id,
            Timestamp = now,
            Action = "PasswordReset",
            EntityName = nameof(User),
            EntityId = user.Id.ToString(),
            Description = $"{user.Username} hesabının parolası lisans dosyası doğrulanarak sıfırlandı.",
            AffectedRecords = 1
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, "Parola sıfırlandı. Yeni parolanızla giriş yapabilirsiniz.");
    }

    /// <summary>
    /// Lisans dosyasini dogrular: once imza, sonra makine bagi.
    ///
    /// Sira onemlidir -- kurcalanmis bir dosyadaki parmak izlerine guvenip onlarla
    /// karar vermek, kontrolun tamamini anlamsiz kilardi.
    /// </summary>
    private bool IsLicenseValid(string? fileContent)
    {
        var license = LicenseFile.Read(fileContent);
        if (license is null) return false;

        // Acik anahtar yoksa dogrulama YAPILAMAZ. Boyle bir kurulumda sifirlamayi
        // serbest birakmak, korumayi tamamen kaldirmak olurdu.
        if (string.IsNullOrEmpty(publicKey)) return false;
        if (!LicenseSignature.VerifyWithPublicKey(license, publicKey)) return false;

        if (!fingerprint.IsUsable) return false;
        return FingerprintMatcher.Matches(license.FingerprintHashes, fingerprint.Hashes);
    }
}
