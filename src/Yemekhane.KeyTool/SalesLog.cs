using System.Globalization;
using System.IO;
using System.Text;

namespace Yemekhane.KeyTool;

/// <param name="Key">Uretilen lisans anahtari.</param>
/// <param name="Customer">Musteri adi; yalnizca sizin kaydiniz icin, anahtara girmez.</param>
/// <param name="Note">Serbest not (telefon, fatura no, iletisim kisisi).</param>
/// <param name="CreatedAt">Uretim zamani.</param>
public sealed record SaleRecord(string Key, string Customer, string Note, DateTimeOffset CreatedAt);

/// <summary>
/// Satis kaydi. Sunucusuz modda kime ne sattiginizi baska hicbir yer bilmez --
/// bu dosya kaybolursa geri getirilemez.
///
/// CSV secildi: Excel'de acilir, yedeklenmesi kolaydir ve bu arac olmadan da okunur.
/// </summary>
public static class SalesLog
{
    public static string FilePath => Resolve(null);

    /// <summary>
    /// Kayit dosyasinin yolu. <paramref name="root"/> yalnizca TESTLER icindir:
    /// ortam degiskeni degistirmek surec GENELINDE etkilidir ve paralel kosan
    /// testlerin birbirinin dosyasina yazmasina yol acar.
    /// </summary>
    public static string Resolve(string? root) => Path.Combine(
        root ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YemekhanePro", "lisans-satislari.csv");

    public static void Append(SaleRecord record) => Append(record, null);

    public static void Append(SaleRecord record, string? root)
    {
        var path = Resolve(root);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var isNew = !File.Exists(path);

        // UTF-8 BOM: Excel BOM'suz dosyayi ANSI sanip Turkce harfleri bozar.
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(true));
        if (isNew) writer.WriteLine("Anahtar;Musteri;Not;Tarih");
        writer.WriteLine(string.Join(';',
            Escape(record.Key), Escape(record.Customer), Escape(record.Note),
            record.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)));
    }

    public static IReadOnlyList<SaleRecord> Load() => Load(null);

    public static IReadOnlyList<SaleRecord> Load(string? root)
    {
        var path = Resolve(root);
        if (!File.Exists(path)) return [];
        var rows = new List<SaleRecord>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(';');
            if (parts.Length < 4) continue;
            rows.Add(new SaleRecord(Unescape(parts[0]), Unescape(parts[1]), Unescape(parts[2]),
                DateTimeOffset.TryParseExact(parts[3], "dd.MM.yyyy HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                    ? parsed : DateTimeOffset.MinValue));
        }
        // En yeni satis basta: kullanici genellikle son urettigini arar.
        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// Ayirici ve satir sonu, alanin icinde gecerse dosyanin sutun yapisi bozulur;
    /// musteri adina yazilan bir noktali virgul tum kaydi kaydirirdi.
    /// </summary>
    private static string Escape(string? value) => (value ?? string.Empty)
        .Replace(";", ",", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string Unescape(string value) => value.Trim();
}
