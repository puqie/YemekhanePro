using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yemekhane.Infrastructure.Persistence;

internal sealed class SqliteConnectionPragmaInterceptor(int busyTimeoutSeconds) : DbConnectionInterceptor
{
    private readonly int busyTimeoutMilliseconds = checked(Math.Max(1, busyTimeoutSeconds) * 1000);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Configure(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        ConfigureAsync(connection, cancellationToken);

    private void Configure(DbConnection connection)
    {
        if (connection is not SqliteConnection) return;
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }

    private async Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection) return;
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
