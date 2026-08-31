using Yemekhane.Application.Sms;

namespace Yemekhane.Infrastructure.Sms;

public sealed class MockSmsProvider : ISmsProvider
{
    public Task<SmsSendResult> SendAsync(
        SmsSendRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SmsSendResult(SmsSendOutcome.Success, $"mock-{Guid.NewGuid():N}"));
    }
}
