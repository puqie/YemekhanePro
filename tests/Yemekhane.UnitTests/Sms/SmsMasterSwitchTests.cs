using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Settings;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// SMS komple kapatilip geri acilabilmeli. Kapaliyken HICBIR mesaj gonderilmez ama kuyruk
/// SILINMEZ: kurallar ve elle gonderim yazmaya devam eder, anahtar acilinca bekleyenler
/// kaldigi yerden gider. Kapatmak veri kaybettirmemeli.
/// </summary>
public sealed class SmsMasterSwitchTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public SmsMasterSwitchTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private SettingsService Settings() => new(db, new PassThroughProtector(),
        new Yemekhane.Application.Audit.AuditService(
            new Yemekhane.Infrastructure.Audit.EfAuditRepository(db, TimeProvider.System),
            new Yemekhane.Infrastructure.Audit.SystemAuditContext()), TimeProvider.System);

    private async Task SetEnabledAsync(bool enabled)
    {
        var current = await Settings().GetAsync();
        await Settings().SaveAsync(new SaveSettingsRequest(
            new(current.School.Name, current.School.Address, current.School.Contact, current.School.LogoPath),
            new(current.Sms.Endpoint, current.Sms.AuthType, current.Sms.Username, current.Sms.Sender,
                current.Sms.TimeoutSeconds, null, current.Sms.Provider, enabled),
            new(current.Backup.Enabled, current.Backup.Frequency, current.Backup.WeeklyDay, current.Backup.Time,
                current.Backup.RetentionCount, current.Backup.Path),
            new(current.Sync.Endpoint, current.Sync.DeviceId, current.Sync.IntervalMinutes, current.Sync.Enabled, null),
            new(current.Logs.Level, current.Logs.RetentionDays, current.Logs.Path)));
    }

    private async Task<Guid> QueueAsync(string phone = "+905321234567")
    {
        var repository = new EfSmsLogRepository(db, TimeProvider.System);
        var log = await repository.EnqueueAsync(phone, "Deneme mesajı", Guid.NewGuid().ToString("N"), null, null, default);
        return log.Id;
    }

    private SmsDispatcher Dispatcher(RecordingProvider provider) =>
        new(new EfSmsLogRepository(db, TimeProvider.System), provider,
            Options.Create(new SmsProviderOptions { Provider = "Mock", BatchSize = 10 }),
            TimeProvider.System, new SmsDispatchRunLock(), null, Settings());

    /// <summary>Varsayilan ACIK: ayar satiri olmayan mevcut kurulumlarda SMS calismaya devam eder.</summary>
    [Fact]
    public async Task SmsIsEnabledByDefault()
    {
        var document = await Settings().GetAsync();

        Assert.True(document.Sms.Enabled);
    }

    [Fact]
    public async Task DisabledSwitchSendsNothingAndKeepsTheQueue()
    {
        await QueueAsync();
        await SetEnabledAsync(false);
        var provider = new RecordingProvider();

        var sent = await Dispatcher(provider).RunOnceAsync();

        Assert.Equal(0, sent);
        Assert.Empty(provider.Sent);
        // KUYRUK SILINMEZ: mesaj hala bekliyor.
        Assert.Equal(1, await db.SmsLogs.CountAsync(x => x.Status == "Pending"));
    }

    /// <summary>Yeniden acilinca bekleyenler kaldigi yerden gider.</summary>
    [Fact]
    public async Task ReEnablingSendsThePendingMessages()
    {
        await QueueAsync();
        await SetEnabledAsync(false);
        var provider = new RecordingProvider();
        Assert.Equal(0, await Dispatcher(provider).RunOnceAsync());

        await SetEnabledAsync(true);
        var sent = await Dispatcher(provider).RunOnceAsync();

        Assert.Equal(1, sent);
        Assert.Single(provider.Sent);
        Assert.Equal("Deneme mesajı", provider.Sent[0].Message);
    }

    /// <summary>Kapaliyken kuyruga YAZMAK serbesttir; yalnizca gonderim durur.</summary>
    [Fact]
    public async Task QueueingStillWorksWhileDisabled()
    {
        await SetEnabledAsync(false);

        await QueueAsync("+905321234567");
        await QueueAsync("+905331234567");

        Assert.Equal(2, await db.SmsLogs.CountAsync());
    }

    /// <summary>Ayar servisi verilmezse (eski cagrilar, testler) gonderim durmaz.</summary>
    [Fact]
    public async Task WithoutSettingsTheDispatcherStillSends()
    {
        await QueueAsync();
        var provider = new RecordingProvider();
        var dispatcher = new SmsDispatcher(new EfSmsLogRepository(db, TimeProvider.System), provider,
            Options.Create(new SmsProviderOptions { Provider = "Mock", BatchSize = 10 }),
            TimeProvider.System, new SmsDispatchRunLock());

        Assert.Equal(1, await dispatcher.RunOnceAsync());
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class RecordingProvider : ISmsProvider
    {
        public List<SmsSendRequest> Sent { get; } = [];

        public Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(new SmsSendResult(SmsSendOutcome.Success, "mock-" + Sent.Count));
        }
    }
}
