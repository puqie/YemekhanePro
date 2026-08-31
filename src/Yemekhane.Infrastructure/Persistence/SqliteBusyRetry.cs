using Microsoft.Data.Sqlite;

namespace Yemekhane.Infrastructure.Persistence;

internal static class SqliteBusyRetry
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)
    ];

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, Action? reset,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception exception) when (IsBusy(exception) && attempt < Delays.Length)
            {
                reset?.Invoke();
                await Task.Delay(Delays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool IsBusy(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        _ when exception.InnerException is not null => IsBusy(exception.InnerException),
        _ => false
    };
}
