using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Settings;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Settings;

namespace Yemekhane.UnitTests.Settings;

public sealed class SettingsServiceTests
{
    [Fact]
    public void ValidationRejectsInvalidTypedValues()
    {
        var request = Request() with { Sync = new("not-a-url", "device", 0, true, null) };
        Assert.Throws<ArgumentException>(() => SettingsValidation.Validate(request));
    }

    [Fact]
    public async Task SaveEncryptsSecretsNeverReturnsPlaintextAndAuditsOnlyKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options); await db.Database.EnsureCreatedAsync();
        var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new TestAuditContext());
        var service = new SettingsService(db, new TestProtector(), audit, TimeProvider.System);
        var request = Request() with
        {
            Sms = Request().Sms with { Secret = "sms-plaintext" },
            Sync = Request().Sync with { Secret = "sync-plaintext" }
        };

        var result = await service.SaveAsync(request);

        Assert.True(result.Settings.Sms.SecretConfigured); Assert.True(result.Settings.Sync.SecretConfigured);
        Assert.DoesNotContain("sms-plaintext", JsonSerializer.Serialize(result));
        Assert.Equal("sms-plaintext", await service.GetSecretAsync(SettingsService.SmsSecretKey));
        var rows = await db.Set<Yemekhane.Domain.Entities.SystemSetting>().Where(x => x.IsSecret).ToListAsync();
        Assert.All(rows, x => Assert.StartsWith("protected:", x.Value));
        var log = await db.AuditLogs.SingleAsync();
        Assert.DoesNotContain("plaintext", log.AfterJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SettingsUpdated", log.Action);
    }

    private static SaveSettingsRequest Request() => new(new("Test Okulu", null, null, null),
        new("https://sms.example/", "Bearer", "user", "OKUL", 30, null),
        new(true, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, "C:\\Backup"),
        new("https://sync.example/", "device-1", 5, true, null), new("Information", 30, null));

    private sealed class TestProtector : ISecretProtector
    { public string Protect(string plaintext) => "protected:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)); public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[10..])); }
    private sealed class TestAuditContext : IAuditContext { public Guid? UserId => Guid.Empty; public string? CorrelationId => "test"; }
}
