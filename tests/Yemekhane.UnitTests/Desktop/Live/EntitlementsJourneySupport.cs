using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Hakedis ve takvim yolculuklarinin ortak yardimcilari: canli SQLite'a dogrudan
/// sorgu (arayuzde gorunen sayi veritabaniyla BIREBIR mi?) ve API'ye ham istek
/// (arayuz ile sunucu ayni sonucu mu veriyor?).
/// </summary>
/// <remarks>
/// DB yolu <c>YP_LIVE_DB</c> ortam degiskeninden okunur; verilmezse
/// <c>YP_SHOT_DIR</c>'in ust klasorundeki <c>yemekhane.db</c> denenir.
/// </remarks>
// Ad EntLiveDb: Raporlar/Ayarlar yolculuklari da "LiveDb" adli (ornek tabanli) bir yardimci ekledi;
// iki tanim ayni ad alaninda catisiyordu. Bu statik surum hakedis/takvim yolculuklarina ozeldir.
internal static class EntLiveDb
{
    public static string? Path
    {
        get
        {
            var explicitPath = Environment.GetEnvironmentVariable("YP_LIVE_DB");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
            var shots = Environment.GetEnvironmentVariable("YP_SHOT_DIR");
            if (string.IsNullOrWhiteSpace(shots)) return null;
            var candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(shots.TrimEnd('\\', '/'))!, "yemekhane.db");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    public static bool Available => Path is not null;

    /// <summary>Tek deger dondururen sorgu (count/sum). Parametreler @p0, @p1... olarak baglanir.</summary>
    public static long Scalar(string sql, params object[] args)
    {
        using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + i, args[i]);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static List<string?[]> Rows(string sql, params object[] args)
    {
        using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + i, args[i]);
        using var reader = command.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }

    public static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

