using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cash;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Cash;

public sealed class EfCashRepository(YemekhaneDbContext dbContext) : ICashRepository
{
    public async Task<CashAggregate> AggregateAsync(DateTimeOffset utcFrom, DateTimeOffset utcToExclusive,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);

        try
        {
            var totals = await ReadTotalsAsync(connection, utcFrom, utcToExclusive, cancellationToken);
            var breakdown = await ReadBreakdownAsync(connection, utcFrom, utcToExclusive, cancellationToken);
            return new CashAggregate(totals.TotalCents / 100m, totals.TransactionCount,
                totals.VoidedCents / 100m, totals.VoidedCount, breakdown);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task<(long TotalCents, int TransactionCount, long VoidedCents, int VoidedCount)>
        ReadTotalsAsync(DbConnection connection, DateTimeOffset utcFrom, DateTimeOffset utcToExclusive,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, utcFrom, utcToExclusive, """
            SELECT
                COALESCE(SUM(CASE WHEN IsVoided = 0 THEN CAST(ROUND(CAST(Amount AS REAL) * 100.0) AS INTEGER) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN IsVoided = 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN IsVoided = 1 THEN CAST(ROUND(CAST(Amount AS REAL) * 100.0) AS INTEGER) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN IsVoided = 1 THEN 1 ELSE 0 END), 0)
            FROM income_transactions
            WHERE julianday(TransactionAt) >= julianday($from)
              AND julianday(TransactionAt) < julianday($to)
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3));
    }

    private static async Task<IReadOnlyList<CashTypeBreakdown>> ReadBreakdownAsync(
        DbConnection connection, DateTimeOffset utcFrom, DateTimeOffset utcToExclusive,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, utcFrom, utcToExclusive, """
            SELECT t.Id, t.Name,
                   SUM(CAST(ROUND(CAST(i.Amount AS REAL) * 100.0) AS INTEGER)),
                   COUNT(*)
            FROM income_transactions AS i
            INNER JOIN income_types AS t ON t.Id = i.IncomeTypeId
            WHERE i.IsVoided = 0
              AND julianday(i.TransactionAt) >= julianday($from)
              AND julianday(i.TransactionAt) < julianday($to)
            GROUP BY t.Id, t.Name
            ORDER BY t.Name COLLATE NOCASE
            """);
        var items = new List<CashTypeBreakdown>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CashTypeBreakdown(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetInt64(2) / 100m, reader.GetInt32(3)));
        }
        return items;
    }

    private static DbCommand CreateCommand(DbConnection connection, DateTimeOffset utcFrom,
        DateTimeOffset utcToExclusive, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameter(command, "$from", utcFrom);
        AddParameter(command, "$to", utcToExclusive);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, DateTimeOffset value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff+00:00", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
    }
}
