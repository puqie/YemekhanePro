using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Yemekhane.Api.Infrastructure;
using Yemekhane.Application.Students;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Parents;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Access;
using Yemekhane.Infrastructure;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Sf300;
using Yemekhane.Devices.ZkTeco;
using Yemekhane.Infrastructure.Sms;
using Yemekhane.Application.Sms;
using Yemekhane.Application.Income;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Api.Authentication;
using Yemekhane.Domain.Entities;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Dashboard;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Settings;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Notifications;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(Path.Combine(
    Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve(),
    "appsettings.api.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("YEMEKHANE_");
var production = ProductionConfiguration.Validate(builder.Configuration, builder.Environment);
ProductionConfiguration.ConfigureLogging(builder, production.Logging);
ProductionConfiguration.ConfigureNetwork(builder.Services, production.Deployment);
builder.Services.AddSingleton(production.Deployment);
builder.Services.AddSingleton(production.Devices);
builder.Services.AddSingleton(production.Schedulers);
builder.Services.AddSingleton<StartupReadiness>();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 10_500_000);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 2_147_483_648);
builder.Services.AddControllers(options => options.MaxModelBindingCollectionSize = 10_000);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, HttpAuditContext>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<ParentService>();
builder.Services.AddScoped<OrganizationService>();
builder.Services.AddScoped<MealTypeService>();
builder.Services.AddScoped<MealEntitlementService>();
builder.Services.AddScoped<MealTransferService>();
builder.Services.AddScoped<HolidayService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<BusinessDayService>();
builder.Services.AddScoped<LeaveService>();
builder.Services.AddScoped<AccessDecisionService>();
builder.Services.AddScoped<SmsTemplateService>();
builder.Services.AddScoped<BulkSmsService>();
builder.Services.AddSingleton<SmsPreviewTokenProtector>();
builder.Services.AddScoped<IncomeService>();
builder.Services.AddScoped<CashService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DailyTrackingService>();
builder.Services.AddScoped<BulkOperationService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<BulkPreviewTokenProtector>();
builder.Services.Configure<ReportPdfOptions>(builder.Configuration.GetSection("Reports:Pdf"));
builder.Services.AddScoped<IPdfService, ReportPdfService>();
builder.Services.Configure<ReportExcelOptions>(builder.Configuration.GetSection("Reports:Excel"));
builder.Services.AddScoped<IExcelService, ReportExcelService>();
builder.Services.AddScoped<ICsvService, ReportCsvService>();
builder.Services.AddScoped<IAccessDecisionGateway>(provider => provider.GetRequiredService<AccessDecisionService>());
builder.Services.AddScoped<TurnstileService>();
builder.Services.AddSingleton<TurnstileRegistry>();
builder.Services.AddSingleton<ITurnstileResolver>(provider => provider.GetRequiredService<TurnstileRegistry>());
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton(provider => new DeviceManager(
    healthCheckInterval: TimeSpan.FromSeconds(production.Devices.HealthIntervalSeconds),
    deviceRegistry: provider.GetRequiredService<DeviceRegistry>(),
    turnstileRegistry: provider.GetRequiredService<TurnstileRegistry>(),
    operationTimeout: TimeSpan.FromSeconds(production.Devices.OperationTimeoutSeconds),
    timeProvider: provider.GetRequiredService<TimeProvider>()));
// Her SF300 cihazi kendi TCP baglantisini alir: tek bir protokol ornegini paylasmak,
// iki turnikenin ayni soket uzerinden konusmasina ve yanitlarin karismasina yol acardi.
builder.Services.AddSingleton<IDeviceAdapterFactory>(_ => new DeviceAdapterFactory(
    builder.Environment.IsDevelopment(),
    configuration => configuration.DeviceType == "SF300"
        ? new Sf300Protocol(new Sf300TcpTransport(),
            TimeSpan.FromSeconds(production.Devices.OperationTimeoutSeconds))
        : null,
    // ZKTeco SC403 icin SDK baglamasi (zkemkeeper.dll) bu kurulumda YOKTUR: 32-bit COM bileseni
    // makineye ayrica kaydedilmelidir. Null birakmak sessiz basarisizlik degildir; adaptor her
    // komutu ZK_SDK_NOT_CONFIGURED ile reddeder, boylece kart "yuklendi" sanilmaz.
    _ => null));
builder.Services.AddScoped<DeviceAdministrationService>();
builder.Services.AddHostedService<DeviceRuntimePersistenceService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Devices:CardPush").Get<DeviceCardPushOptions>() ?? new DeviceCardPushOptions());
builder.Services.AddHostedService<DeviceCardPushWorker>();
builder.Services.AddYemekhaneRealtime();
builder.Services.AddSingleton(builder.Configuration.GetSection("Calendar:WeekendPolicy").Get<WeekendPolicy>() ?? new WeekendPolicy());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
if (string.Equals(Environment.GetEnvironmentVariable("YEMEKHANE_MANAGED_CHILD"), "1", StringComparison.Ordinal))
    builder.Services.AddHostedService<ParentProcessLifetimeService>();
builder.Services.AddHttpClient("outbound-secure")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("search", context => RateLimitPartition.GetSlidingWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    options.AddPolicy("expensive", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddScoped<SettingsSyncRunner>();
builder.Services.AddHostedService<SettingsSyncBackgroundWorker>();
var jwtSection = builder.Configuration.GetSection("Authentication:Jwt");
var signingKey = jwtSection["SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException(
        "Authentication:Jwt:SigningKey tanımlı ve en az 32 bayt olmalıdır. Üretim ortamında gizli yönetimi (user-secrets / ortam değişkeni) kullanın.");
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) || string.IsNullOrWhiteSpace(jwtOptions.Audience))
    throw new InvalidOperationException("Authentication:Jwt:Issuer ve Audience boş olamaz.");
if (jwtOptions.AccessTokenMinutes is < 1 or > 60)
    throw new InvalidOperationException("Authentication:Jwt:AccessTokenMinutes 1 ile 60 arasında olmalıdır.");
jwtOptions.SigningKey = signingKey;
var lockoutOptions = builder.Configuration.GetSection("Authentication:Lockout").Get<LoginLockoutOptions>()
    ?? new LoginLockoutOptions();
if (lockoutOptions.MaxFailedAttempts is < 1 or > 20 || lockoutOptions.DurationMinutes is < 1 or > 1440)
    throw new InvalidOperationException("Authentication:Lockout ayarları geçersiz.");
var deviceKeys = builder.Configuration.GetSection("Authentication:DeviceKeys").Get<string[]>() ?? [];

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(lockoutOptions);
builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    options.IterationCount = 210_000;
});
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<InitialAdminBootstrapper>();
// Parola sifirlama: kanit olarak .lic dosyasi istenir ve imzasi BURADA dogrulanir.
// Acik anahtar ile makinenin parmak izi masaustunden ortam degiskeniyle gelir;
// istek govdesinden ALINMAZ, aksi halde saldirgan lisanstaki hash'leri kopyalayip
// makine kontrolunu anlamsiz kilardi.
builder.Services.AddScoped(services => new PasswordResetService(
    services.GetRequiredService<YemekhaneDbContext>(),
    services.GetRequiredService<IPasswordHasher<User>>(),
    services.GetRequiredService<TimeProvider>(),
    builder.Configuration["Licensing:PublicKey"],
    new Yemekhane.Licensing.HardwareFingerprint(
        (builder.Configuration["Licensing:MachineFingerprint"] ?? string.Empty)
            .Split('|', StringSplitOptions.None))));
builder.Services.AddScoped<RbacSeeder>();
builder.Services.AddScoped<RbacService>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userIdValue = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var stamp = context.Principal?.FindFirst("security_stamp")?.Value;
                if (!Guid.TryParse(userIdValue, out var userId) || string.IsNullOrEmpty(stamp))
                {
                    context.Fail("Geçersiz kullanıcı güvenlik damgası.");
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<YemekhaneDbContext>();
                if (!await db.Users.AnyAsync(x => x.Id == userId && x.IsActive && x.SecurityStamp == stamp,
                        context.HttpContext.RequestAborted))
                    context.Fail("Kullanıcı oturumu artık geçerli değil.");
            }
        };
    })
    .AddScheme<DeviceKeyAuthenticationOptions, DeviceKeyAuthenticationHandler>(
        DeviceKeyAuthenticationHandler.SchemeName, options => options.DeviceKeys = deviceKeys);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Device", policy => policy
        .AddAuthenticationSchemes(DeviceKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

var localDatabaseOptions = builder.Configuration.GetSection("LocalDatabase").Get<LocalDatabaseOptions>()
    ?? new LocalDatabaseOptions();
var databaseConnectionString = LocalDatabaseConnection.Resolve(
    builder.Configuration.GetConnectionString("Database"),
    localDatabaseOptions.DataDirectory);
builder.Services.AddYemekhaneInfrastructure(databaseConnectionString, localDatabaseOptions.BusyTimeoutSeconds);
builder.Services.AddSingleton(provider => new StartupDatabaseGuard(databaseConnectionString,
    provider.GetRequiredService<ILogger<StartupDatabaseGuard>>()));
var backupOptions = builder.Configuration.GetSection("Backup").Get<BackupOptions>() ?? new BackupOptions();
var backupSettings = new Dictionary<string, string?>(StringComparer.Ordinal)
{
    ["LocalDatabase:BusyTimeoutSeconds"] = localDatabaseOptions.BusyTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ["Calendar:WeekendPolicy:SaturdayIsWorking"] = builder.Configuration["Calendar:WeekendPolicy:SaturdayIsWorking"],
    ["Calendar:WeekendPolicy:SundayIsWorking"] = builder.Configuration["Calendar:WeekendPolicy:SundayIsWorking"]
};
builder.Services.AddYemekhaneBackup(databaseConnectionString, backupOptions, backupSettings);
builder.Services.AddYemekhaneSms(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<NotificationRetentionWorker>();

var app = builder.Build();

if (production.Deployment.ForwardedHeadersEnabled) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test") && production.Deployment.Mode == "Remote") app.UseHsts();
app.UseMiddleware<CorrelationMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test") &&
        !context.Request.IsHttps && context.Connection.RemoteIpAddress is { } remoteAddress &&
        !System.Net.IPAddress.IsLoopback(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { Message = "Uzak API erişimi HTTPS gerektirir." });
        return;
    }
    var isLargeUpload = context.Request.Path.StartsWithSegments("/api/backups") ||
        context.Request.Path.StartsWithSegments("/api/settings/backup");
    if (!isLargeUpload && context.Request.ContentLength is > 10_500_000)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { Message = "İstek gövdesi izin verilen boyutu aşıyor." });
        return;
    }
    await next();
});
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseCors(ProductionConfiguration.CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapYemekhaneRealtime();
app.MapGet("/health/live", () => Results.Ok(new { Status = "Healthy" })).AllowAnonymous();
app.MapGet("/health/ready", (StartupReadiness readiness) => readiness.IsReady
    ? Results.Ok(new { Status = "Ready" })
    : Results.Json(new { Status = "Starting" }, statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
app.MapGet("/health", async (LocalDatabaseHealth database, ISettingsService settings, StartupReadiness readiness, CancellationToken cancellationToken) => Results.Ok(new
{
    Status = readiness.IsReady ? "Healthy" : "Starting",
    Timestamp = DateTimeOffset.UtcNow,
    Database = database.LastResult?.IsHealthy == true ? "Available" : "Unavailable",
    LocalApi = "Available",
    Cloud = (await settings.SyncStatusAsync(cancellationToken)).State
})).AllowAnonymous();

await using (var scope = app.Services.CreateAsyncScope())
{
    try
    {
        await using var migrationLock = await scope.ServiceProvider.GetRequiredService<StartupDatabaseGuard>()
            .AcquireAsync(app.Lifetime.ApplicationStopping);
        await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await scope.ServiceProvider.GetRequiredService<RbacSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
        var startupDb = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var hasBackupSettings = await startupDb.Set<SystemSetting>().AnyAsync(x => x.Key.StartsWith("Backup."));
        var hasSmsSettings = await startupDb.Set<SystemSetting>().AnyAsync(x => x.Key.StartsWith("Sms."));
        if (hasBackupSettings || hasSmsSettings)
        {
            var persistedSettings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync();
            if (hasBackupSettings)
            {
                backupOptions.Directory = persistedSettings.Backup.Path;
                backupOptions.ScheduleEnabled = persistedSettings.Backup.Enabled;
                backupOptions.Schedule = Enum.Parse<BackupScheduleFrequency>(persistedSettings.Backup.Frequency);
                backupOptions.WeeklyDay = persistedSettings.Backup.WeeklyDay;
                backupOptions.Time = persistedSettings.Backup.Time;
                backupOptions.RetentionCount = persistedSettings.Backup.RetentionCount;
            }
            if (hasSmsSettings && !string.IsNullOrWhiteSpace(persistedSettings.Sms.Endpoint))
            {
                var smsOptions = scope.ServiceProvider.GetRequiredService<IOptions<SmsProviderOptions>>().Value;
                smsOptions.Provider = "Http";
                smsOptions.Endpoint = persistedSettings.Sms.Endpoint;
                smsOptions.AuthType = persistedSettings.Sms.AuthType;
                smsOptions.Username = persistedSettings.Sms.Username;
                smsOptions.Sender = persistedSettings.Sms.Sender;
                smsOptions.TimeoutSeconds = persistedSettings.Sms.TimeoutSeconds;
                smsOptions.Secret = await scope.ServiceProvider.GetRequiredService<ISettingsService>()
                    .GetSecretAsync(Yemekhane.Infrastructure.Settings.SettingsService.SmsSecretKey);
            }
        }
        await scope.ServiceProvider.GetRequiredService<InitialAdminBootstrapper>().BootstrapAsync(
            builder.Configuration.GetSection("Authentication:Bootstrap").Get<InitialAdminBootstrapOptions>()
            ?? new InitialAdminBootstrapOptions(), app.Lifetime.ApplicationStopping);
        app.Services.GetRequiredService<StartupReadiness>().IsReady = true;
    }
    catch (Exception exception)
    {
        Log.Fatal(exception, "Veritabanı migration/başlangıç işlemi tamamlanamadı; veri güvenliği için API başlatılmadı.");
        throw new InvalidOperationException("YemekhanePro veritabanı hazırlanamadı. Migration güvenlik yedeğini ve uygulama logunu kontrol edin.", exception);
    }
}

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    Log.Fatal(args.ExceptionObject as Exception, "Yakalanmamış süreç hatası; veritabanı yazımları durduruluyor.");
TaskScheduler.UnobservedTaskException += (_, args) =>
{
    Log.Error(args.Exception, "Gözlemlenmemiş task hatası.");
    args.SetObserved();
};
try { app.Run(); }
catch (Exception exception) { Log.Fatal(exception, "API beklenmedik biçimde sonlandı."); throw; }
finally { await Log.CloseAndFlushAsync(); }

public partial class Program;
