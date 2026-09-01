using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Api.Authorization;

namespace Yemekhane.UnitTests.Api;

public sealed class AuthenticationTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public AuthenticationTests(YemekhaneApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ValidLoginReturnsJwtThatCanCallProtectedEndpoint()
    {
        var username = $"Operator-{Guid.NewGuid():N}";
        var password = "Strong test password 037!";
        var user = await factory.CreateUserAsync(username, password);
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = username.ToLowerInvariant(), Password = password });
        var result = await login.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotNull(result);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.NotEmpty(jwt.Id);
        Assert.Equal("yemekhane-test", jwt.Issuer);
        Assert.Contains("yemekhane-test", jwt.Audiences);
        Assert.Contains(jwt.Claims, claim => claim.Type == ClaimTypes.Role && claim.Value == BuiltInRoles.Operator);
        Assert.Contains(jwt.Claims, claim => claim.Type == Permissions.ClaimType && claim.Value == Permissions.StudentsRead);
        Assert.Contains(jwt.Claims, claim => claim.Type == "security_stamp" && claim.Value == user.SecurityStamp);
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/students")).StatusCode);
    }

    [Fact]
    public async Task UnknownWrongPasswordAndInactiveUserReturnSameUnauthorizedResponse()
    {
        var password = "Strong test password 037!";
        var active = await factory.CreateUserAsync($"active-{Guid.NewGuid():N}", password);
        var inactive = await factory.CreateUserAsync($"inactive-{Guid.NewGuid():N}", password, isActive: false);
        using var client = factory.CreateClient();

        using var unknownResponse = await client.PostAsJsonAsync("/api/auth/login", new { Username = $"missing-{Guid.NewGuid():N}", Password = password });
        using var wrongResponse = await client.PostAsJsonAsync("/api/auth/login", new { active.Username, Password = "wrong password" });
        using var inactiveResponse = await client.PostAsJsonAsync("/api/auth/login", new { inactive.Username, Password = password });
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.Equal(unknownResponse.StatusCode, wrongResponse.StatusCode);
        Assert.Equal(unknownResponse.StatusCode, inactiveResponse.StatusCode);
        Assert.Equal(unknownBody, await wrongResponse.Content.ReadAsStringAsync());
        Assert.Equal(unknownBody, await inactiveResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepeatedFailuresTemporarilyLockAccount()
    {
        var password = "Strong test password 037!";
        var user = await factory.CreateUserAsync($"locked-{Guid.NewGuid():N}", password);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, Password = "wrong password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, Password = password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        await factory.AssertUserLockedOutAsync(user.Id);
    }

    [Fact]
    public async Task InvalidAndExpiredTokensAreRejected()
    {
        using var invalidClient = factory.CreateClient();
        invalidClient.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalidClient.GetAsync("/api/students")).StatusCode);

        using var expiredClient = factory.CreateClient();
        expiredClient.DefaultRequestHeaders.Authorization = new("Bearer", YemekhaneApiFactory.CreateOperatorToken(DateTime.UtcNow.AddMinutes(-2)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expiredClient.GetAsync("/api/students")).StatusCode);
    }

    [Fact]
    public async Task NoDefaultAdminExistsAndStoredPasswordIsHashed()
    {
        var password = "Strong test password 037!";
        var user = await factory.CreateUserAsync($"hash-{Guid.NewGuid():N}", password);
        using var client = factory.CreateClient();

        using var defaultLogin = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "admin" });

        Assert.Equal(HttpStatusCode.Unauthorized, defaultLogin.StatusCode);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal);
        await factory.AssertNoUserAsync("ADMIN");
    }

    [Fact]
    public async Task NormalizedUsernameIsUniqueRegardlessOfCase()
    {
        var username = $"Case-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(username, "Strong test password 037!");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            factory.CreateUserAsync(username.ToUpperInvariant(), "Another strong password 037!"));
    }

    [Theory]
    [InlineData("/api/students")]
    [InlineData("/api/meal-types")]
    [InlineData("/api/organization/classes")]
    [InlineData("/api/imports/students/unknown/errors.csv")]
    public async Task ProtectedEndpointsRejectAnonymousCallers(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessCheckRejectsCallerWithoutDeviceKey()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/access/check", new
        {
            CardNumber = "0001",
            DeviceId = Guid.NewGuid(),
            MealTypeId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessCheckRejectsWrongDeviceKey()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", "yanlis-anahtar");

        var response = await client.PostAsJsonAsync("/api/access/check", new
        {
            CardNumber = "0001",
            DeviceId = Guid.NewGuid(),
            MealTypeId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessCheckAcceptsValidDeviceKey()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", YemekhaneApiFactory.DeviceKey);

        var response = await client.PostAsJsonAsync("/api/access/check", new
        {
            CardNumber = "tanimsiz-kart",
            DeviceId = Guid.NewGuid(),
            MealTypeId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow
        });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpointStaysPublic()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PdfReportRequiresAuthenticationAndReturnsDownloadablePdf()
    {
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/reports/DailyAccess/pdf");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var client = factory.CreateOperatorClient();
        using var response = await client.GetAsync("/api/reports/DailyAccess/pdf");
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.True(response.IsSuccessStatusCode, Encoding.UTF8.GetString(content));
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var pdf = PdfDocument.Open(content);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ExcelReportRequiresAuthenticationAndReturnsDownloadableWorkbook()
    {
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/reports/DailyAccess/excel");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var client = factory.CreateOperatorClient();
        using var response = await client.GetAsync("/api/reports/DailyAccess/excel");
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.True(response.IsSuccessStatusCode, Encoding.UTF8.GetString(content));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var workbook = SpreadsheetDocument.Open(new MemoryStream(content), false);
        var workbookPart = workbook.WorkbookPart ?? throw new InvalidOperationException("Workbook part missing.");
        Assert.Single(workbookPart.Workbook!.Sheets!);
    }

    private sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
}

public sealed class YemekhaneApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid OperatorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string DeviceKey = "test-cihaz-anahtari-0123456789";
    public const string SigningKey = "test-imza-anahtari-en-az-otuz-iki-karakter-olmali!";
    public const string OperatorSecurityStamp = "22222222222222222222222222222222";

    /// <summary>Operatör JWT'si taşıyan istemci üretir.</summary>
    public HttpClient CreateOperatorClient()
    {
        EnsureOperatorAsync().GetAwaiter().GetResult();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateOperatorToken());
        return client;
    }

    /// <summary>
    /// SINIRLI yetkili JWT. Arayuzun pasiflestirdigi bir butonun API tarafinda da
    /// gercekten reddedildigini kanitlamak icin gerekir: UI kontrolu kolaylik,
    /// API kontrolu GUVENLIKTIR.
    /// </summary>
    public static string CreateTokenWith(params string[] permissions)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "yemekhane-test",
            audience: "yemekhane-test",
            claims: new Claim[]
            {
                new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()), new Claim(ClaimTypes.Name, "operator"),
                new Claim(ClaimTypes.Role, BuiltInRoles.Operator), new Claim("security_stamp", OperatorSecurityStamp)
            }.Concat(permissions.Select(permission => new Claim(Permissions.ClaimType, permission))),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// TUM yetkileri tasiyan istemci. Operator jetonu bilerek kisitlidir (RBAC testleri ona dayanir);
    /// cihaz/ayar gibi yonetim ekranlarini surmek icin ayri bir yonetici jetonu gerekir.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        EnsureOperatorAsync().GetAwaiter().GetResult();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateAdminToken());
        return client;
    }

    public static string CreateAdminToken(DateTime? expires = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "yemekhane-test",
            audience: "yemekhane-test",
            claims: new Claim[]
            {
                new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()), new Claim(ClaimTypes.Name, "operator"),
                new Claim(ClaimTypes.Role, BuiltInRoles.Admin), new Claim("security_stamp", OperatorSecurityStamp)
            }.Concat(Permissions.All.Select(permission => new Claim(Permissions.ClaimType, permission))),
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateOperatorToken(DateTime? expires = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "yemekhane-test",
            audience: "yemekhane-test",
            claims: new Claim[]
            {
                new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()), new Claim(ClaimTypes.Name, "operator"),
                new Claim(ClaimTypes.Role, BuiltInRoles.Operator), new Claim("security_stamp", OperatorSecurityStamp)
            }.Concat(new[]
            {
                Permissions.StudentsRead, Permissions.StudentsWrite, Permissions.StudentsDeactivate,
                Permissions.CardsManage, Permissions.EntitlementsManage, Permissions.EntitlementsBulk,
                Permissions.CalendarManage, Permissions.DevicesRead, Permissions.DevicesManage,
                Permissions.DashboardRead, Permissions.NotificationsRead,
                Permissions.ReportsRead, Permissions.ReportsExport,
                Permissions.CashRead, Permissions.CashWrite, Permissions.CashManage, Permissions.SmsRead, Permissions.SmsSend, Permissions.SmsManage,
                Permissions.AccessRead
            }.Select(permission => new Claim(Permissions.ClaimType, permission))),
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<User> CreateUserAsync(string username, string password, bool isActive = true)
    {
        _ = Server;
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            NormalizedUsername = username.Trim().ToUpperInvariant(),
            PasswordHash = string.Empty,
            IsActive = isActive,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        context.Users.Add(user);
        var operatorRoleId = await context.Roles.Where(x => x.NormalizedName == "OPERATOR").Select(x => x.Id).SingleAsync();
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = operatorRoleId });
        await context.SaveChangesAsync();
        return user;
    }

    private async Task EnsureOperatorAsync()
    {
        _ = Server;
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        if (await context.Users.AnyAsync(x => x.Id == OperatorId)) return;
        context.Users.Add(new User
        {
            Id = OperatorId, Username = "operator", NormalizedUsername = "OPERATOR", PasswordHash = "test-only",
            SecurityStamp = OperatorSecurityStamp, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    public async Task AssertUserLockedOutAsync(Guid userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>().Users
            .AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
        Assert.Equal(3, user.FailedLoginAttempts);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
    }

    public async Task AssertNoUserAsync(string normalizedUsername)
    {
        await using var scope = Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>().Users
            .AnyAsync(user => user.NormalizedUsername == normalizedUsername));
    }

    // Paylasimli-onbellekli bellek-ici SQLite veritabani, yalnizca ACIK bir baglanti
    // kaldigi surece yasar. Baska bir testin SqliteConnection.ClearAllPools() cagrisi
    // (havuz temizligi process genelindedir) havuzdaki son baglantiyi kapatirsa veritabani
    // silinir ve bu fabrikayi kullanan test, yazdigi kaydi bulamayarak rastgele kirilir.
    // Bu baglanti fabrika yasadigi surece acik tutularak veritabani garanti altina alinir.
    private readonly SqliteConnection keepAlive;

    public YemekhaneApiFactory()
    {
        connectionString = $"Data Source=file:yemekhane-{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
    }

    private readonly string connectionString;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) keepAlive.Dispose();
    }

    // WebApplicationFactory.DisposeAsync() senkron Dispose(bool) yolunu CAGIRMAZ.
    // Testlerin cogu DisposeAsync kullandigindan, baglanti burada da kapatilmazsa
    // her fabrika bir baglantiyi ve bellek-ici veritabanini surec sonuna kadar sizdirir.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await keepAlive.DisposeAsync();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = connectionString,
            ["Authentication:Jwt:SigningKey"] = SigningKey,
            ["Authentication:Jwt:Issuer"] = "yemekhane-test",
            ["Authentication:Jwt:Audience"] = "yemekhane-test",
            ["Authentication:Jwt:AccessTokenMinutes"] = "15",
            ["Authentication:Lockout:MaxFailedAttempts"] = "3",
            ["Authentication:Lockout:DurationMinutes"] = "15",
            ["Authentication:DeviceKeys:0"] = DeviceKey
        }));
        return base.CreateHost(builder);
    }
}
