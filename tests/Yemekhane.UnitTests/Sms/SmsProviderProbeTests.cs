using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Saglayici secimi ve sinama zinciri: kayitli ayar → options eslemesi (acilis ve canli),
/// yonlendirici (Http | Mutlucell), test SMS'in kuyruksuz gidip ham yaniti aynen dondurmesi,
/// kontor sorgusu, DI kaydi. Okul "test SMS gonderebilmeliyiz, cevap yansimali" dedi.
/// </summary>
public sealed class SmsProviderProbeTests
{
    // ---------------------------------------------------------------- acilis eslemesi

    [Fact]
    public void MutlucellSettingsMapToOptionsWithDefaultGateway()
    {
        var options = new SmsProviderOptions { Provider = "Http", Endpoint = "https://sms.invalid/", RecipientProperty = "to" };
        var settings = new SmsProviderSettings(null, "None", " okul ", "OKUL", 45, true, "Mutlucell");

        Assert.True(SmsStartupOptions.Apply(options, settings, "pwd"));

        Assert.Equal("Mutlucell", options.Provider);
        Assert.Equal(MutlucellGateway.DefaultSendEndpoint, options.Endpoint);
        Assert.Equal("okul", options.Username);
        Assert.Equal("pwd", options.Secret);
        Assert.Equal("OKUL", options.Sender);
        Assert.Equal(45, options.TimeoutSeconds);
        Assert.Equal("to", options.RecipientProperty);
    }

    [Fact]
    public void MutlucellWithoutUsernameLeavesStartupOptionsUntouched()
    {
        var options = new SmsProviderOptions { Provider = "Http", Endpoint = "https://sms.invalid/" };

        Assert.False(SmsStartupOptions.Apply(options, new SmsProviderSettings(null, "None", "", null, 30, false, "Mutlucell"), "pwd"));

        Assert.Equal("Http", options.Provider);
        Assert.Equal("https://sms.invalid/", options.Endpoint);
        Assert.Null(options.Secret);
    }

    [Fact]
    public void HttpSettingsStillNeedAnEndpoint()
    {
        var options = new SmsProviderOptions { Provider = "Http", Endpoint = "https://sms.invalid/" };
        Assert.False(SmsStartupOptions.Apply(options, new SmsProviderSettings("", "Bearer", "u", null, 30, true), "t"));

        Assert.True(SmsStartupOptions.Apply(options, new SmsProviderSettings("https://sms.example/send", "Bearer", "u", "OKUL", 20, true), "t"));
        Assert.Equal("Http", options.Provider);
        Assert.Equal("https://sms.example/send", options.Endpoint);
        Assert.Equal("Bearer", options.AuthType);
        Assert.Equal("t", options.Secret);
    }

    [Fact]
    public void CloneCopiesQueueAndJsonFieldsWithoutSharingDictionaries()
    {
        var source = new SmsProviderOptions { Provider = "Http", BatchSize = 7, ProviderMessageIdJsonPath = "r.id" };
        source.Headers["X-A"] = "1";
        var copy = SmsStartupOptions.Clone(source);
        copy.Headers["X-B"] = "2";

        Assert.Equal(7, copy.BatchSize);
        Assert.Equal("r.id", copy.ProviderMessageIdJsonPath);
        Assert.Equal("1", copy.Headers["X-A"]);
        Assert.False(source.Headers.ContainsKey("X-B"));
    }

    // ---------------------------------------------------------------- yonlendirici / dogrulayici

    [Fact]
    public async Task RouterPicksProviderByOptionsAtSendTime()
    {
        var options = new SmsProviderOptions { Provider = "Http", Endpoint = "https://h.example/send", Username = "u", Secret = "s", AllowPrivateNetworks = true };
        var hits = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            hits.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Host == "h.example" ? "{\"id\":\"1\"}" : "$2", Encoding.UTF8, "text/plain") });
        });
        var wrapped = Options.Create(options);
        var router = new SmsProviderRouter(wrapped, new HttpSmsProvider(new HttpClient(handler, false), wrapped, new JsonSmsResponseParser(wrapped)),
            new MutlucellSmsProvider(new HttpClient(handler, false), wrapped));

        await router.SendAsync(new SmsSendRequest("905321112233", "a"));
        options.Provider = "Mutlucell"; options.Endpoint = "https://m.example/sndblkex";
        await router.SendAsync(new SmsSendRequest("905321112233", "b"));

        Assert.Equal(["h.example", "m.example"], hits);
    }

    [Fact]
    public void ValidatorAcceptsMutlucellWithoutEndpointAndRejectsUnknownProvider()
    {
        var validator = new SmsProviderOptionsValidator();

        Assert.True(validator.Validate(null, new SmsProviderOptions { Provider = "Mutlucell" }).Succeeded);
        Assert.True(validator.Validate(null, new SmsProviderOptions { Provider = "mutlucell", Endpoint = "https://sms.invalid/" }).Succeeded);
        Assert.False(validator.Validate(null, new SmsProviderOptions { Provider = "Mutlucell", Endpoint = "ftp://x" }).Succeeded);
        Assert.False(validator.Validate(null, new SmsProviderOptions { Provider = "Mutlucell", TimeoutSeconds = 0 }).Succeeded);
        var unknown = validator.Validate(null, new SmsProviderOptions { Provider = "Netgsm" });
        Assert.False(unknown.Succeeded);
        Assert.Contains("Mutlucell", unknown.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationResolvesRouterAndProbeInProduction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sms:Provider"] = "Mutlucell" })
            .Build();
        using var services = new ServiceCollection()
            .AddSingleton<ISettingsService>(new FakeSettings(new SmsProviderSettings(null, "None", "okul", null, 30, true, "Mutlucell"), "pwd"))
            .AddSingleton(TimeProvider.System)
            .AddYemekhaneSms(configuration, new TestEnvironment("Production"))
            .BuildServiceProvider();

        Assert.IsType<SmsProviderRouter>(services.GetRequiredService<ISmsProvider>());
        using var scope = services.CreateScope();
        Assert.IsType<LiveSmsProviderProbe>(scope.ServiceProvider.GetRequiredService<ISmsProviderProbe>());
    }

    // ---------------------------------------------------------------- canli sinama

    [Fact]
    public async Task TestSmsUsesSavedSettingsAndReflectsTheRawGatewayReply()
    {
        string? body = null; HttpRequestMessage? captured = null;
        var factory = new StubFactory(async (request, cancellationToken) =>
        {
            captured = request; body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("$88512", Encoding.UTF8, "text/plain") };
        });
        var settings = new FakeSettings(new SmsProviderSettings(null, "None", "okul", "OKUL", 30, true, "Mutlucell"), "pwd");
        var audit = new RecordingAudit();
        var probe = new LiveSmsProviderProbe(settings, factory, BaseOptions(), new FixedClock(new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero)), audit);

        var result = await probe.SendTestAsync(new SmsTestRequest("0532 111 22 33"));

        Assert.True(result.Success);
        Assert.Equal("Mutlucell", result.Provider);
        // Uygulama numarayi E.164 ("+90...") tutar; XML'de Mutlucell'in bekledigi "90..." gider.
        Assert.Equal("+905321112233", result.Phone);
        Assert.Equal("88512", result.ProviderMessageId);
        Assert.Equal("$88512", result.RawResponse);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(MutlucellGateway.DefaultSendEndpoint, captured!.RequestUri!.ToString());
        Assert.Contains("pwd=\"pwd\"", body, StringComparison.Ordinal);
        Assert.Contains("<nums>905321112233</nums>", body, StringComparison.Ordinal);
        Assert.Contains("YemekhanePro test mesajı 08.09.2026 12:00", body, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("SmsTestSent", entry.Action);
        Assert.Contains("+905321112233", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTestSmsCarriesProviderMessageCodeAndRawBody()
    {
        var factory = new StubFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("23", Encoding.UTF8, "text/plain") }));
        var settings = new FakeSettings(new SmsProviderSettings(null, "None", "okul", null, 30, true, "Mutlucell"), "yanlis");
        var probe = new LiveSmsProviderProbe(settings, factory, BaseOptions(), TimeProvider.System);

        var result = await probe.SendTestAsync(new SmsTestRequest("05321112233", "Selam"));

        Assert.False(result.Success);
        Assert.Equal("mutlucell_23", result.ErrorCode);
        Assert.Equal("Authentication", result.ErrorCategory);
        Assert.Contains("(23)", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("23", result.RawResponse);
    }

    [Fact]
    public async Task GenericHttpTestReflectsJsonBodyAndErrorBody()
    {
        var factory = new StubFactory((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid sender\"}", Encoding.UTF8, "application/json")
        }));
        var settings = new FakeSettings(new SmsProviderSettings("https://10.0.0.5/send", "Bearer", "u", "OKUL", 30, true, "Http"), "token");
        var probe = new LiveSmsProviderProbe(settings, factory, BaseOptions(allowPrivate: true), TimeProvider.System);

        var result = await probe.SendTestAsync(new SmsTestRequest("05321112233"));

        Assert.False(result.Success);
        Assert.Equal("Http", result.Provider);
        Assert.Equal("http_400", result.ErrorCode);
        Assert.Contains("HTTP 400", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("{\"error\":\"invalid sender\"}", result.RawResponse);
    }

    [Fact]
    public async Task UnconfiguredProviderDoesNotCallAnythingAndExplains()
    {
        var calls = 0;
        var factory = new StubFactory((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var probe = new LiveSmsProviderProbe(new FakeSettings(new SmsProviderSettings(null, "None", null, null, 30, false), null), factory, BaseOptions(), TimeProvider.System);

        var result = await probe.SendTestAsync(new SmsTestRequest("05321112233"));

        Assert.Equal(0, calls);
        Assert.False(result.Success);
        Assert.Equal("not_configured", result.ErrorCode);
        Assert.Contains("Ayarlar → SMS", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPhoneIsRejectedBeforeSending()
    {
        var probe = new LiveSmsProviderProbe(new FakeSettings(new SmsProviderSettings(null, "None", "okul", null, 30, true, "Mutlucell"), "pwd"),
            new StubFactory((_, _) => throw new InvalidOperationException("çağrılmamalı")), BaseOptions(), TimeProvider.System);

        await Assert.ThrowsAsync<RequestValidationException>(() => probe.SendTestAsync(new SmsTestRequest("0212 555 66 77")));
    }

    [Fact]
    public async Task CreditQueryOnlyForMutlucell()
    {
        var factory = new StubFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("$15234", Encoding.UTF8, "text/plain") }));
        var mutlucell = new LiveSmsProviderProbe(new FakeSettings(new SmsProviderSettings(null, "None", "okul", null, 30, true, "Mutlucell"), "pwd"), factory, BaseOptions(), TimeProvider.System);
        var http = new LiveSmsProviderProbe(new FakeSettings(new SmsProviderSettings("https://10.0.0.5/send", "None", null, null, 30, false, "Http"), null), factory, BaseOptions(allowPrivate: true), TimeProvider.System);

        var credit = await mutlucell.QueryCreditAsync();
        var refused = await http.QueryCreditAsync();

        Assert.True(credit.Success);
        Assert.Equal(15234m, credit.Credit);
        Assert.False(refused.Success);
        Assert.Contains("yalnızca Mutlucell", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- yardimcilar

    private static IOptions<SmsProviderOptions> BaseOptions(bool allowPrivate = false) =>
        Options.Create(new SmsProviderOptions { Provider = "Http", Endpoint = "https://sms.invalid/", AllowPrivateNetworks = allowPrivate, RecipientProperty = "to", MessageProperty = "message" });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Record(AuditEntry entry) => Entries.Add(entry);
        public Task<PagedResult<AuditLogDetails>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSettings(SmsProviderSettings sms, string? secret) : ISettingsService
    {
        public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsDocument(
            new("Okul", null, null, null), sms, new(false, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null),
            new(null, null, 5, false, false, new SyncStatus("Idle", 0, 0, null, null)), new("Information", 30, null), new(0, [], 0, []), false));
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(secret);
        public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<ApplicationLogItem>> LogsAsync(ApplicationLogQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SyncStatus> SyncStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SyncRequeueAsync(Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(send), disposeHandler: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
