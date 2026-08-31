using System.Globalization;
using System.Text;

namespace Yemekhane.Application.Common;

/// <summary>
/// Arama metnini Türkçe kurallarına göre normalleştirir.
/// <para>
/// SQLite'ın <c>LIKE</c> operatörü yalnızca ASCII harflerde büyük/küçük harf duyarsızdır;
/// <c>NOCASE</c> collation da Türkçe harfleri kapsamaz (ölçüldü). Bu yüzden aranabilir metin
/// veritabanına normalleştirilmiş hâliyle ayrıca yazılır ve karşılaştırma o sütun üzerinden yapılır.
/// </para>
/// <para>
/// Noktalı/noktasız <c>i</c> ayrımı arama için bilerek kaldırılır: kullanıcı <c>irmak</c> veya
/// <c>ırmak</c> yazsın, <c>Irmak</c> öğrencisini bulmalıdır. Sonuç fazlalığı, hiç sonuç
/// gelmemesinden iyidir.
/// </para>
/// </summary>
public static class TurkishSearchText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Normalleştirilmiş sütunların azami uzunluğu.</summary>
    public const int MaxLength = 200;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var upper = value.Trim().ToUpper(Turkish);
        var builder = new StringBuilder(upper.Length);
        foreach (var character in upper)
        {
            // 'İ' -> 'I': arama için noktalı ve noktasız i birleştirilir.
            builder.Append(character == 'İ' ? 'I' : character);
        }

        var normalized = builder.ToString();
        return normalized.Length > MaxLength ? normalized[..MaxLength] : normalized;
    }

    /// <summary>Ad ve soyadı tek bir aranabilir alanda birleştirir.</summary>
    public static string NormalizeFullName(string? first, string? last) =>
        Normalize($"{first} {last}");
}
