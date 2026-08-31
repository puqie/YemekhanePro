using Microsoft.Data.Sqlite;

namespace Yemekhane.Infrastructure.Persistence;

public static class LocalDatabaseConnection
{
    public static string Resolve(string? configuredConnectionString, string? dataDirectoryOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            return configuredConnectionString;

        var dataDirectory = ResolveDataDirectory(dataDirectoryOverride);
        Directory.CreateDirectory(dataDirectory);

        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "yemekhane.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = LocalDatabaseOptions.DefaultBusyTimeoutSeconds
        }.ToString();
    }

    public static string ResolveDataDirectory(string? dataDirectoryOverride = null)
    {
        var directory = string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? ApplicationDataPath.Resolve()
            : Environment.ExpandEnvironmentVariables(dataDirectoryOverride);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Yerel uygulama veri dizini çözümlenemedi.");

        return Path.GetFullPath(directory);
    }
}
