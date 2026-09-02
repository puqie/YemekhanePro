using Microsoft.Data.Sqlite;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Canli yolculuklarin ekranda gordugu sayiyi dogrudan SQLite ile karsilastirmasi icin:
/// API'nin dondurdugu degeri API ile dogrulamak dongusel olurdu, kaynak veri esas alinir.
/// Yol <c>YP_LIVE_DB</c> ortam degiskeninden okunur.
/// </summary>
public sealed class LiveDb : IDisposable
{
    private readonly SqliteConnection connection;
    private LiveDb(SqliteConnection connection) => this.connection = connection;

    public static string? Path => Environment.GetEnvironmentVariable("YP_LIVE_DB");

    public static LiveDb Open()
    {
        var path = Path ?? throw new InvalidOperationException("YP_LIVE_DB tanimli degil.");
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
        connection.Open();
        return new LiveDb(connection);
    }

    public long Count(string sql) => Convert.ToInt64(Scalar(sql) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    public decimal Money(string sql) => Math.Round(Convert.ToDecimal(Scalar(sql) ?? 0m, System.Globalization.CultureInfo.InvariantCulture), 2);

    public string? Text(string sql) => Scalar(sql)?.ToString();

    public object? Scalar(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public int Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    /// <summary>Istanbul gunune gore [start, end] kapali araligini julianday ile ifade eder.</summary>
    public static string Range(string column, string startDay, string endDay) =>
        $"julianday({column}) >= julianday('{startDay}T00:00:00+03:00') AND julianday({column}) < julianday('{endDay}T00:00:00+03:00', '+1 day')";

    public void Dispose() => connection.Dispose();
}
