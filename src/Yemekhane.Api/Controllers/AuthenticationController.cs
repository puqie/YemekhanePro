using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(
    LoginService loginService,
    PasswordResetService passwordResetService) : ControllerBase
{
    private const string InvalidCredentialsMessage = "Kullanıcı adı veya parola geçersiz.";

    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResult>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await loginService.LoginAsync(request.Username, request.Password, cancellationToken);
        return result is null
            ? Unauthorized(new { Message = InvalidCredentialsMessage })
            : Ok(result);
    }

    /// <summary>
    /// Lisans dosyasiyla parola sifirlar.
    ///
    /// <para>
    /// <c>AllowAnonymous</c> ZORUNLUDUR: bu ucu kullanan kisi tam olarak giris
    /// YAPAMAYAN kisidir. Yetki yerine saticinin urettigi .lic dosyasi kanit sayilir
    /// ve imzasi <see cref="PasswordResetService"/> icinde dogrulanir.
    /// </para>
    /// <para>
    /// Hiz siniri "login" ile ayni: sifirlama denemesi de parola tahminine yakin bir
    /// yuzey acar, sinirsiz birakmak kaba kuvvet denemelerine kapi olurdu.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> ResetPassword(
        PasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await passwordResetService.ResetAsync(
            request.LicenseFileContent, request.Username, request.NewPassword, cancellationToken);

        // Basarisizlikta 400 doner: 401 "kimlik dogrula" anlamina gelirdi, oysa
        // burada dogrulanacak bir oturum yok -- sunulan kanit ya da parola gecersiz.
        return result.Succeeded
            ? Ok(new { result.Message })
            : BadRequest(new { result.Message });
    }
}

public sealed record LoginRequest(string? Username, string? Password);

/// <param name="LicenseFileContent">Saticinin urettigi .lic dosyasinin tam icerigi.</param>
/// <param name="Username">Parolasi sifirlanacak hesabin kullanici adi.</param>
/// <param name="NewPassword">Okulun belirledigi yeni parola.</param>
public sealed record PasswordResetRequest(
    string? LicenseFileContent, string? Username, string? NewPassword);
