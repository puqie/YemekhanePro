using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Authentication;

public sealed class JwtOptions
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
}

public sealed class LoginLockoutOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int DurationMinutes { get; set; } = 15;
}

public sealed class InitialAdminBootstrapOptions
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class LoginService
{
    private readonly YemekhaneDbContext dbContext;
    private readonly IPasswordHasher<User> passwordHasher;
    private readonly JwtOptions jwtOptions;
    private readonly LoginLockoutOptions lockoutOptions;
    private readonly TimeProvider timeProvider;
    private readonly User timingUser;

    public LoginService(
        YemekhaneDbContext dbContext,
        IPasswordHasher<User> passwordHasher,
        JwtOptions jwtOptions,
        LoginLockoutOptions lockoutOptions,
        TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.passwordHasher = passwordHasher;
        this.jwtOptions = jwtOptions;
        this.lockoutOptions = lockoutOptions;
        this.timeProvider = timeProvider;
        timingUser = new User
        {
            Id = Guid.Empty,
            Username = string.Empty,
            NormalizedUsername = string.Empty,
            PasswordHash = string.Empty
        };
        timingUser.PasswordHash = passwordHasher.HashPassword(timingUser, "not-a-real-password");
    }

    public async Task<LoginResult?> LoginAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        var normalizedUsername = NormalizeUsername(username);
        var user = normalizedUsername.Length == 0
            ? null
            : await dbContext.Users.SingleOrDefaultAsync(
                candidate => candidate.NormalizedUsername == normalizedUsername, cancellationToken);
        var passwordToVerify = password ?? string.Empty;
        var verification = passwordHasher.VerifyHashedPassword(
            user ?? timingUser, user?.PasswordHash ?? timingUser.PasswordHash, passwordToVerify);
        var now = timeProvider.GetUtcNow();

        if (user is null)
            return null;

        var isLockedOut = user.LockoutEnd is not null && user.LockoutEnd > now;
        if (user.LockoutEnd is not null && !isLockedOut)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }
        if (verification == PasswordVerificationResult.Failed)
        {
            if (user.IsActive && !isLockedOut)
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= lockoutOptions.MaxFailedAttempts)
                    user.LockoutEnd = now.AddMinutes(lockoutOptions.DurationMinutes);
                user.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return null;
        }

        if (!user.IsActive || isLockedOut)
            return null;

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwordHasher.HashPassword(user, passwordToVerify);
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var expiresAt = now.AddMinutes(jwtOptions.AccessTokenMinutes);
        var roles = await (from userRole in dbContext.UserRoles
                           join role in dbContext.Roles on userRole.RoleId equals role.Id
                           where userRole.UserId == user.Id
                           orderby role.Name
                           select role.Name).ToListAsync(cancellationToken);
        var permissions = await (from userRole in dbContext.UserRoles
                                 join rolePermission in dbContext.RolePermissions on userRole.RoleId equals rolePermission.RoleId
                                 join permission in dbContext.Permissions on rolePermission.PermissionId equals permission.Id
                                 where userRole.UserId == user.Id
                                 orderby permission.Code
                                 select permission.Code).Distinct().Take(129).ToListAsync(cancellationToken);
        if (permissions.Count > 128)
            throw new InvalidOperationException("Bir kullanıcı JWT içinde en fazla 128 benzersiz izne sahip olabilir.");

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim("security_stamp", user.SecurityStamp)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(permissions.Select(permission => new Claim(Permissions.ClaimType, permission)));
        var token = new JwtSecurityToken(
            jwtOptions.Issuer,
            jwtOptions.Audience,
            claims,
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new LoginResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static string NormalizeUsername(string? username) => (username ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class InitialAdminBootstrapper(
    YemekhaneDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider)
{
    public async Task BootstrapAsync(InitialAdminBootstrapOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(options.Username) || options.Username.Trim().Length > 128 || options.Password.Length < 12)
            throw new InvalidOperationException(
                "İlk yönetici bootstrap için kullanıcı adı ve en az 12 karakterli parola açıkça sağlanmalıdır.");
        var normalized = LoginService.NormalizeUsername(options.Username);
        // Masaustu istemcisi bootstrap degiskenlerini, API sureci veritabani dosyasini olusturmadan
        // ONCE belirler. Bu yuzden ilk kurulumdan sonraki her yeniden baslatmada (cokme kurtarma,
        // uygulamanin kapatilip acilmasi) ayni degiskenler hala doludur. Burada patlamak API'yi
        // hic baslatmaz: kullanici giris penceresini gorur, "Giris yap" der ve arkada API
        // olmadigi icin hicbir sey olmaz. Zaten kurulmus olan ayni yonetici, yapilacak is
        // kalmadigi anlamina gelir -- sessizce ve basariyla cikilir.
        if (await dbContext.Users.AnyAsync(user => user.NormalizedUsername == normalized, cancellationToken))
            return;
        // Baska bir kullanici varsa bu bir yeniden baslatma degil, yanlis yapilandirmadir:
        // sessizce ikinci bir yonetici hesabi acmak guvenlik acigi olurdu.
        if (await dbContext.Users.AnyAsync(cancellationToken))
            throw new InvalidOperationException(
                "İlk yönetici bootstrap yalnızca boş kullanıcı veritabanında çalışır; oluşturma sonrası Authentication:Bootstrap:Enabled kapatılmalıdır.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = options.Username.Trim(),
            NormalizedUsername = normalized,
            PasswordHash = string.Empty,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = timeProvider.GetUtcNow()
        };
        user.PasswordHash = passwordHasher.HashPassword(user, options.Password);
        dbContext.Users.Add(user);
        var adminRoleId = await dbContext.Roles.Where(x => x.NormalizedName == "ADMIN")
            .Select(x => x.Id).SingleAsync(cancellationToken);
        dbContext.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRoleId });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
