namespace Yemekhane.Infrastructure.Persistence;

public sealed class LocalDatabaseOptions
{
    public const int DefaultBusyTimeoutSeconds = 5;

    public string? DataDirectory { get; init; }
    public int BusyTimeoutSeconds { get; init; } = DefaultBusyTimeoutSeconds;
}
