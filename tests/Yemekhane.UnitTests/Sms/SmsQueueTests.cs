using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

public sealed class SmsQueueTests
{
    [Fact]
    public async Task SuccessfulMessageIsPersistedBeforeSendAndCompleted()
    {
        await using var fixture = await QueueFixture.CreateAsync(
            new SmsSendResult(SmsSendOutcome.Success, "provider-42"));
        var queued = await fixture.EnqueueAsync("0532 111 22 33", "success");

        Assert.Equal(SmsLogStatuses.Pending, queued.Status);
        await fixture.Dispatcher.RunOnceAsync();

        var sent = await fixture.GetAsync(queued.Id);
        Assert.Equal(SmsLogStatuses.Sent, sent.Status);
        Assert.Equal("+905321112233", sent.Phone);
        Assert.Equal("provider-42", sent.ProviderMessageId);
        Assert.Equal("Test", sent.Provider);
        Assert.NotNull(sent.SentAt);
    }

    [Fact]
    public async Task TransientFailureUsesExponentialRetryThenSucceeds()
    {
        await using var fixture = await QueueFixture.CreateAsync(
            new SmsSendResult(SmsSendOutcome.TransientFailure, ErrorCategory: SmsErrorCategory.Timeout,
                ErrorCode: "secret-must-not-appear", ErrorMessage: "provider secret"),
            new SmsSendResult(SmsSendOutcome.Success, "done"));
        var queued = await fixture.EnqueueAsync("5321112233", "retry");

        await fixture.Dispatcher.RunOnceAsync();
        var retry = await fixture.GetAsync(queued.Id);
        Assert.Equal(SmsLogStatuses.RetryScheduled, retry.Status);
        Assert.Equal("Timeout:secret-must-not-appear", retry.Error);
        Assert.DoesNotContain("provider secret", retry.Error);

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.RunOnceAsync();
        var sent = await fixture.GetAsync(queued.Id);
        Assert.Equal(SmsLogStatuses.Sent, sent.Status);
        Assert.Equal(2, sent.AttemptCount);
    }

    [Fact]
    public async Task PermanentFailureIsNotRetried()
    {
        await using var fixture = await QueueFixture.CreateAsync(
            new SmsSendResult(SmsSendOutcome.PermanentFailure,
                ErrorCategory: SmsErrorCategory.Authentication, ErrorCode: "unauthorized"));
        var queued = await fixture.EnqueueAsync("5321112233", "permanent");

        await fixture.Dispatcher.RunOnceAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Dispatcher.RunOnceAsync();

        var failed = await fixture.GetAsync(queued.Id);
        Assert.Equal(SmsLogStatuses.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyReturnsOriginalLog()
    {
        await using var fixture = await QueueFixture.CreateAsync();

        var first = await fixture.EnqueueAsync("5321112233", "same-key", "first");
        var duplicate = await fixture.EnqueueAsync("5551112233", "same-key", "second");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal("first", duplicate.Message);
        Assert.Equal(1, await fixture.Db.SmsLogs.CountAsync());
    }

    [Fact]
    public async Task StaleSendingMessageIsRecoveredAndClaimed()
    {
        await using var fixture = await QueueFixture.CreateAsync(
            new SmsSendResult(SmsSendOutcome.Success, "recovered"));
        var queued = await fixture.EnqueueAsync("5321112233", "stale");
        await fixture.Db.SmsLogs.Where(x => x.Id == queued.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.Status, SmsLogStatuses.Sending)
            .SetProperty(x => x.SendingStartedAt, fixture.Clock.GetUtcNow() - TimeSpan.FromMinutes(10))
            .SetProperty(x => x.ClaimToken, "dead-worker"));

        await fixture.Dispatcher.RunOnceAsync();

        var recovered = await fixture.GetAsync(queued.Id);
        Assert.Equal(SmsLogStatuses.Sent, recovered.Status);
        Assert.Equal(1, recovered.AttemptCount);
    }

    [Fact]
    public async Task CancellationLeavesClaimForStaleRecovery()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queued = await fixture.EnqueueAsync("5321112233", "cancel");
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.OnSend = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Dispatcher.RunOnceAsync(cancellation.Token));
        Assert.Equal(SmsLogStatuses.Sending, (await fixture.GetAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task HistoryAppliesServerSideFiltersAndPagination()
    {
        await using var fixture = await QueueFixture.CreateAsync(
            new SmsSendResult(SmsSendOutcome.Success, "one"));
        await fixture.EnqueueAsync("5321112233", "history-1");
        await fixture.EnqueueAsync("5551112233", "history-2");
        await fixture.Dispatcher.RunOnceAsync();

        var result = await fixture.Service.ListAsync(new SmsHistoryFilter(
            SmsLogStatuses.Sent, "0532 111 22 33", fixture.Clock.GetUtcNow().AddMinutes(-1),
            fixture.Clock.GetUtcNow().AddMinutes(1), 1, 1));

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("history-1", result.Items[0].IdempotencyKey);
    }

    private sealed class QueueFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private QueueFixture(SqliteConnection connection, YemekhaneDbContext db, ScriptedProvider provider,
            MutableTimeProvider clock, SmsProviderOptions options)
        {
            this.connection = connection;
            Db = db;
            Provider = provider;
            Clock = clock;
            var repository = new EfSmsLogRepository(db, clock);
            Service = new SmsService(repository, new EfSmsTemplateRepository(db));
            Dispatcher = new SmsDispatcher(repository, provider, Options.Create(options), clock,
                new SmsDispatchRunLock());
        }

        public YemekhaneDbContext Db { get; }
        public ScriptedProvider Provider { get; }
        public MutableTimeProvider Clock { get; }
        public SmsService Service { get; }
        public SmsDispatcher Dispatcher { get; }

        public static async Task<QueueFixture> CreateAsync(params SmsSendResult[] results)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var options = new SmsProviderOptions
            {
                Provider = "Test", BatchSize = 25, MaxAttempts = 3, InitialRetrySeconds = 10,
                MaxRetrySeconds = 60, StaleSendingSeconds = 60
            };
            return new QueueFixture(connection, db, new ScriptedProvider(results),
                new MutableTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z", CultureInfo.InvariantCulture)), options);
        }

        public Task<SmsLogDetails> EnqueueAsync(string phone, string key, string message = "message") =>
            Service.EnqueueAsync(new EnqueueSmsRequest(phone, key, message));

        public Task<SmsLog> GetAsync(Guid id) => Db.SmsLogs.AsNoTracking().SingleAsync(x => x.Id == id);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ScriptedProvider(IEnumerable<SmsSendResult> results) : ISmsProvider
    {
        private readonly Queue<SmsSendResult> results = new(results);
        public List<SmsSendRequest> Requests { get; } = [];
        public Action? OnSend { get; set; }

        public Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            OnSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results.Count == 0
                ? new SmsSendResult(SmsSendOutcome.Success, "default") : results.Dequeue());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
