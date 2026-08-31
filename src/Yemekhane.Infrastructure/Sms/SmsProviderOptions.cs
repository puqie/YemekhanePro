namespace Yemekhane.Infrastructure.Sms;

public sealed class SmsProviderOptions
{
    public const string SectionName = "Sms";

    public string Provider { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool AllowHttp { get; set; }
    public bool AllowPrivateNetworks { get; set; }
    public string Method { get; set; } = "POST";
    public string? BearerToken { get; set; }
    public string AuthType { get; set; } = "None";
    public string? Username { get; set; }
    public string? Sender { get; set; }
    public string? Secret { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string RecipientProperty { get; set; } = "to";
    public string MessageProperty { get; set; } = "message";
    public Dictionary<string, string> AdditionalJsonProperties { get; set; } = new(StringComparer.Ordinal);
    public string? ProviderMessageIdJsonPath { get; set; } = "id";
    public int TimeoutSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 25;
    public int MaxAttempts { get; set; } = 5;
    public int InitialRetrySeconds { get; set; } = 30;
    public int MaxRetrySeconds { get; set; } = 3600;
    public int StaleSendingSeconds { get; set; } = 300;
    public int DispatchIntervalSeconds { get; set; } = 10;
}
