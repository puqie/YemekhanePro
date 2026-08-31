using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Yemekhane.Infrastructure.Persistence;

public sealed record LocalDatabaseHealthResult(
    bool IsHealthy,
    string IntegrityResult,
    string JournalMode,
    DateTimeOffset CheckedAt);

public sealed class LocalDatabaseHealth
{
    public LocalDatabaseHealthResult? LastResult { get; internal set; }
}

public sealed class LocalDatabaseInitializer(YemekhaneDbContext dbContext, LocalDatabaseHealth health)
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    ];

    public async Task<LocalDatabaseHealthResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteWithBusyRetryAsync(async () =>
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await ExecuteScalarAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }, cancellationToken);

        var journalMode = await ExecuteScalarAsync("PRAGMA journal_mode;", cancellationToken);
        var integrity = await ExecuteScalarAsync("PRAGMA integrity_check;", cancellationToken);
        var result = new LocalDatabaseHealthResult(
            string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase),
            integrity,
            journalMode,
            DateTimeOffset.UtcNow);
        health.LastResult = result;

        if (!result.IsHealthy)
            throw new InvalidOperationException($"Yerel veritabanı bütünlük kontrolü başarısız: {integrity}");

        return result;
    }

    private async Task<string> ExecuteScalarAsync(string commandText, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await dbContext.Database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await ExecuteWithBusyRetryAsync(
            () => command.ExecuteScalarAsync(cancellationToken), cancellationToken);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteWithBusyRetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteWithBusyRetryAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    private static async Task<T> ExecuteWithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (IsBusy(exception) && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static bool IsBusy(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        _ when exception.InnerException is not null => IsBusy(exception.InnerException),
        _ => false
    };
}
