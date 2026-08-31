using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Yemekhane.Infrastructure.Sms;

public interface ISmsResponseParser
{
    string? ParseProviderMessageId(string responseBody);
}

public sealed class JsonSmsResponseParser(IOptions<SmsProviderOptions> options) : ISmsResponseParser
{
    public string? ParseProviderMessageId(string responseBody)
    {
        var path = options.Value.ProviderMessageIdJsonPath;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(responseBody)) return null;

        using var document = JsonDocument.Parse(responseBody);
        var current = document.RootElement;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                throw new JsonException($"Yapılandırılmış provider mesaj kimliği yolu bulunamadı: {path}");
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => throw new JsonException("Provider mesaj kimliği metin veya sayı olmalıdır.")
        };
    }
}
