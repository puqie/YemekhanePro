using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(LoginService loginService) : ControllerBase
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
}

public sealed record LoginRequest(string? Username, string? Password);
