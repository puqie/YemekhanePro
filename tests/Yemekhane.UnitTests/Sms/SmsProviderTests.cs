using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

public sealed class SmsProviderTests
{
    [Fact]
    public async Task SuccessMapsJsonAuthAndProviderMessageId()
    {
        CapturedRequest? captured = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            captured = new CapturedRequest(
                request.Method.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("X-Client").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(HttpStatusCode.Accepted, """{"result":{"messageId":"abc-123"}}""");
        });
        var options = ValidOptions();
        options.Method = "PUT";
        options.BearerToken = "secret-token";
        options.Headers["X-Client"] = "client-secret";
        options.RecipientProperty = "recipient";
        options.MessageProperty = "text";
        options.AdditionalJsonProperties["sender"] = "SCHOOL";
        options.ProviderMessageIdJsonPath = "result.messageId";
        var provider = CreateProvider(handler, options);

        var result = await provider.SendAsync(new SmsSendRequest("+905321112233", "Merhaba"));

        Assert.True(result.IsSuccess);
        Assert.Equal("abc-123", result.ProviderMessageId);
        Assert.Equal(202, result.HttpStatusCode);
        Assert.NotNull(captured);
        Assert.Equal("PUT", captured.Method);
        Assert.Equal("https://sms.example.test/send", captured.Uri!.ToString());
        Assert.Equal("Bearer secret-token", captured.Authorization);
        Assert.Equal("client-secret", captured.ClientHeader);
        using var payload = JsonDocument.Parse(captured.Body);
        Assert.Equal("+905321112233", payload.RootElement.GetProperty("recipient").GetString());
        Assert.Equal("Merhaba", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal("SCHOOL", payload.RootElement.GetProperty("sender").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SmsErrorCategory.ProviderRejected)]
    [InlineData(HttpStatusCode.Unauthorized, SmsErrorCategory.Authentication)]
    public async Task ClientErrorsArePermanent(HttpStatusCode statusCode, SmsErrorCategory category)
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))), ValidOptions());

        var result = await provider.SendAsync(new SmsSendRequest("+905321112233", "message"));

        Assert.Equal(SmsSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(category, result.ErrorCategory);
        Assert.Equal((int)statusCode, result.HttpStatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, SmsErrorCategory.ProviderUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, SmsErrorCategory.RateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, SmsErrorCategory.Timeout)]
    public async Task RetryableHttpErrorsAreTransient(HttpStatusCode statusCode, SmsErrorCategory category)
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))), ValidOptions());

        var result = await provider.SendAsync(new SmsSendRequest("+905321112233", "message"));

        Assert.Equal(SmsSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(category, result.ErrorCategory);
    }

    [Fact]
    public async Task ProviderTimeoutIsTransient()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        var options = ValidOptions();
        options.TimeoutSeconds = 1;

        var result = await CreateProvider(handler, options)
            .SendAsync(new SmsSendRequest("+905321112233", "message"));

        Assert.Equal(SmsSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.Timeout, result.ErrorCategory);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider(handler, ValidOptions())
            .SendAsync(new SmsSendRequest("+905321112233", "message"), cancellation.Token));
    }

    [Fact]
    public void MissingProviderConfigurationFailsWhenProviderIsResolved()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var services = new ServiceCollection()
            .AddYemekhaneSms(configuration, new TestEnvironment("Production"))
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<SmsProviderOptions>>().Value);

        Assert.Contains("varsayılan sahte başarı", exception.Message);
    }

    [Fact]
    public void MockProviderIsRejectedInProduction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sms:Provider"] = "Mock" })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddYemekhaneSms(configuration, new TestEnvironment("Production")));

        Assert.Contains("Development veya Test", exception.Message);
    }

    [Fact]
    public void MockProviderRequiresExplicitDevelopmentConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sms:Provider"] = "Mock" })
            .Build();
        using var services = new ServiceCollection()
            .AddYemekhaneSms(configuration, new TestEnvironment("Development"))
            .BuildServiceProvider();

        Assert.IsType<MockSmsProvider>(services.GetRequiredService<ISmsProvider>());
    }

    private static HttpSmsProvider CreateProvider(HttpMessageHandler handler, SmsProviderOptions options)
    {
        var optionValue = Options.Create(options);
        return new HttpSmsProvider(new HttpClient(handler), optionValue, new JsonSmsResponseParser(optionValue));
    }

    private static SmsProviderOptions ValidOptions() => new()
    {
        Provider = "Http",
        Endpoint = "https://sms.example.test/send",
        AllowPrivateNetworks = true,
        ProviderMessageIdJsonPath = null
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed record CapturedRequest(
        string Method,
        Uri? Uri,
        string? Authorization,
        string ClientHeader,
        string Body);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
