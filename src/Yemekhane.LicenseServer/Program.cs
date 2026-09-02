using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yemekhane.LicenseServer.Data;
using Yemekhane.LicenseServer.Services;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------------------------- yapilandirma
// Imza sirri masaustune enjekte edilenle AYNI olmalidir; farkli olursa sunucunun
// imzaladigi lisansi masaustu "kurcalanmis" sayar ve uygulama hic acilmaz.
var signingSecret = builder.Configuration["Licensing:SigningSecret"];
if (string.IsNullOrWhiteSpace(signingSecret))
    throw new InvalidOperationException(
        "Licensing:SigningSecret tanımlı değil. Masaüstü kurulumuna enjekte edilen sır ile AYNI olmalıdır.");

// Yonetim uclarini koruyan belirtec: bos ya da kisa birakilirsa lisans uretme,
// iptal ve makine cozme uclari internete acik kalirdi.
var adminToken = builder.Configuration["Licensing:AdminToken"];
if (string.IsNullOrWhiteSpace(adminToken) || adminToken.Length < 24)
    throw new InvalidOperationException(
        "Licensing:AdminToken en az 24 karakter olmalıdır; yönetim uçları bu belirteçle korunur.");

var dataDirectory = builder.Configuration["Licensing:DataDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContext<LicenseDbContext>(options =>
    options.UseSqlite("Data Source=" + Path.Combine(dataDirectory, "licenses.db")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped(provider => new LicenseServerService(
    provider.GetRequiredService<LicenseDbContext>(),
    provider.GetRequiredService<TimeProvider>(),
    signingSecret));
builder.Services.AddScoped<LicenseAdminService>();

// Aktivasyon uclari internete aciktir: kaba kuvvetle anahtar denemesini yavaslatir.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("activation", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 20;
        limiter.QueueLimit = 0;
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<LicenseDbContext>().Database.EnsureCreated();

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --------------------------------------------------------------------- aktivasyon
// Sozlesme masaustundeki HttpLicenseActivationClient ile BIREBIR aynidir:
//   POST /activate  200 basarili | 404 anahtar yok | 409 baska makinede | 410 iptal/sure doldu
//   POST /validate  200 gecerli  | 410 iptal
app.MapPost("/activate", async (ActivateRequest request, LicenseServerService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey) || request.Fingerprints is not { Length: > 0 })
        return Results.BadRequest(new { message = "Lisans anahtarı ve donanım parmak izi zorunludur." });

    var reply = await service.ActivateAsync(request.LicenseKey, request.Fingerprints, ct);
    return reply.Outcome switch
    {
        ActivateOutcome.NotFound => Results.NotFound(),
        ActivateOutcome.AlreadyBound => Results.Conflict(),
        ActivateOutcome.Revoked or ActivateOutcome.Expired => Results.StatusCode(StatusCodes.Status410Gone),
        _ => Results.Ok(new ActivateResponse(
            reply.License!.CustomerName, reply.License.Edition,
            reply.License.ActivatedAt ?? reply.License.CreatedAt,
            reply.License.ExpiresAt, reply.Signature))
    };
}).RequireRateLimiting("activation");

app.MapPost("/validate", async (ValidateRequest request, LicenseServerService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey) || request.Fingerprints is not { Length: > 0 })
        return Results.BadRequest(new { message = "Lisans anahtarı ve donanım parmak izi zorunludur." });

    var reply = await service.ValidateAsync(request.LicenseKey, request.Fingerprints, ct);
    return reply.Revoked
        ? Results.StatusCode(StatusCodes.Status410Gone)
        : Results.Ok(new ValidateResponse(reply.ExpiresAt, reply.Signature));
}).RequireRateLimiting("activation");

// --------------------------------------------------------------------- yonetim
var admin = app.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
{
    var supplied = context.HttpContext.Request.Headers["X-Admin-Token"].ToString();
    // Sabit zamanli karsilastirma: siradan string esitligi ilk farkli karakterde
    // doner ve belirteci karakter karakter tahmin etmeye kapi aralar.
    var expected = Encoding.UTF8.GetBytes(adminToken);
    var actual = Encoding.UTF8.GetBytes(supplied);
    if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        return Results.Unauthorized();
    return await next(context);
});

admin.MapGet("/licenses", async (string? search, LicenseAdminService service, CancellationToken ct) =>
    Results.Ok(await service.ListAsync(search, ct)));

admin.MapPost("/licenses", async (CreateLicenseRequest request, LicenseAdminService service, CancellationToken ct) =>
{
    try
    {
        var license = await service.CreateAsync(request.CustomerName, request.Edition, request.Years, request.Notes, ct);
        return Results.Ok(license);
    }
    catch (ArgumentException exception) { return Results.BadRequest(new { message = exception.Message }); }
});

admin.MapPost("/licenses/{key}/revoke", async (string key, RevokeRequest? request, LicenseAdminService service, CancellationToken ct) =>
    await service.RevokeAsync(key, request?.Reason, ct) ? Results.Ok() : Results.NotFound());

admin.MapPost("/licenses/{key}/restore", async (string key, LicenseAdminService service, CancellationToken ct) =>
    await service.RestoreAsync(key, ct) ? Results.Ok() : Results.NotFound());

admin.MapPost("/licenses/{key}/extend", async (string key, ExtendRequest request, LicenseAdminService service, CancellationToken ct) =>
{
    try
    {
        var license = await service.ExtendAsync(key, request.Years, ct);
        return license is null ? Results.NotFound() : Results.Ok(license);
    }
    catch (ArgumentException exception) { return Results.BadRequest(new { message = exception.Message }); }
});

admin.MapPost("/licenses/{key}/perpetual", async (string key, LicenseAdminService service, CancellationToken ct) =>
{
    var license = await service.MakePerpetualAsync(key, ct);
    return license is null ? Results.NotFound() : Results.Ok(license);
});

admin.MapPost("/licenses/{key}/release-machine", async (string key, LicenseAdminService service, CancellationToken ct) =>
    await service.ReleaseMachineAsync(key, ct) ? Results.Ok() : Results.NotFound());

app.Run();

internal sealed record ActivateRequest(
    [property: JsonPropertyName("licenseKey")] string LicenseKey,
    [property: JsonPropertyName("fingerprints")] string[] Fingerprints);

internal sealed record ValidateRequest(
    [property: JsonPropertyName("licenseKey")] string LicenseKey,
    [property: JsonPropertyName("fingerprints")] string[] Fingerprints,
    [property: JsonPropertyName("signature")] string? Signature);

internal sealed record ActivateResponse(
    [property: JsonPropertyName("customerName")] string CustomerName,
    [property: JsonPropertyName("edition")] string Edition,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("signature")] string? Signature);

internal sealed record ValidateResponse(
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("signature")] string? Signature);

internal sealed record CreateLicenseRequest(string CustomerName, string Edition, int? Years, string? Notes);
internal sealed record RevokeRequest(string? Reason);
internal sealed record ExtendRequest(int Years);

/// <summary>
/// Ust duzey deyimlerin urettigi Program sinifi varsayilan olarak PUBLIC olur ve
/// Yemekhane.Api'nin ureteni ile ayni tam adi tasir; test projesi ikisine birden
/// referans verince CS0433 (belirsiz tur) alinir. Burada AYNI kismi sinif internal
/// olarak bildirilir; derleyici ikisini birlestirir ve tur disari sizmaz.
/// </summary>
internal partial class Program;
