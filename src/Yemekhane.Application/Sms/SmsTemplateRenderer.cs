using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Sms;

public sealed partial class SmsTemplateRenderer
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly HashSet<string> AllowedVariables = new(StringComparer.Ordinal)
    {
        "ParentName", "StudentName", "ExpiryDate", "EntryTime", "Amount"
    };

    public static string Render(string template, IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var body = ValidateTemplate(template);

        var rendered = PlaceholderRegex().Replace(body, match =>
        {
            var name = match.Groups[1].Value;
            if (!variables.TryGetValue(name, out var value) || value is null)
                throw new RequestValidationException($"'{name}' şablon değişkeni için değer verilmelidir.");
            return Format(name, value);
        });

        if (rendered.Length > 1600)
            throw new RequestValidationException("Oluşturulan SMS metni 1600 karakteri aşamaz.");
        return rendered;
    }

    public static string ValidateTemplate(string template)
    {
        var body = template?.Trim() ?? string.Empty;
        if (body.Length is < 1 or > 1600)
            throw new RequestValidationException("SMS şablon metni 1-1600 karakter olmalıdır.");

        foreach (Match match in PlaceholderRegex().Matches(body))
        {
            var name = match.Groups[1].Value;
            if (!AllowedVariables.Contains(name))
                throw new RequestValidationException($"Bilinmeyen SMS şablon değişkeni: '{name}'.");
        }

        var withoutPlaceholders = PlaceholderRegex().Replace(body, string.Empty);
        if (withoutPlaceholders.Contains("{{", StringComparison.Ordinal) ||
            withoutPlaceholders.Contains("}}", StringComparison.Ordinal))
            throw new RequestValidationException("SMS şablonunda geçersiz placeholder sözdizimi var.");
        return body;
    }

    private static string Format(string name, object value)
    {
        if (value is JsonElement json) value = ConvertJson(name, json);
        var formatted = name switch
        {
            "ParentName" or "StudentName" when value is string text => text.Trim(),
            "ExpiryDate" when value is DateOnly date => date.ToString("dd.MM.yyyy", TurkishCulture),
            "ExpiryDate" when value is DateTime date => date.ToString("dd.MM.yyyy", TurkishCulture),
            "ExpiryDate" when value is DateTimeOffset date => date.ToString("dd.MM.yyyy", TurkishCulture),
            "EntryTime" when value is TimeOnly time => time.ToString("HH:mm", TurkishCulture),
            "EntryTime" when value is DateTime time => time.ToString("HH:mm", TurkishCulture),
            "EntryTime" when value is DateTimeOffset time => time.ToString("HH:mm", TurkishCulture),
            "Amount" when value is decimal amount => amount.ToString("N2", TurkishCulture),
            "Amount" when value is double amount => amount.ToString("N2", TurkishCulture),
            "Amount" when value is float amount => amount.ToString("N2", TurkishCulture),
            "Amount" when value is int amount => amount.ToString("N2", TurkishCulture),
            "Amount" when value is long amount => amount.ToString("N2", TurkishCulture),
            _ => throw new RequestValidationException($"'{name}' şablon değişkeninin değeri geçersiz türde.")
        };
        if (string.IsNullOrWhiteSpace(formatted))
            throw new RequestValidationException($"'{name}' şablon değişkeni boş olamaz.");
        return formatted;
    }

    private static object ConvertJson(string name, JsonElement value) => name switch
    {
        "ParentName" or "StudentName" when value.ValueKind == JsonValueKind.String => value.GetString()!,
        "ExpiryDate" when value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParse(value.GetString(), TurkishCulture, out var date) => date,
        "EntryTime" when value.ValueKind == JsonValueKind.String &&
            TimeOnly.TryParse(value.GetString(), TurkishCulture, out var time) => time,
        "Amount" when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) => amount,
        _ => value
    };

    /// <summary>
    /// Otomatik SMS kurallarinin tek suslu, kucuk harfli degiskenleri: <c>{ad} {soyad} {kalan_gun}</c>.
    /// Elle sablonlardaki <c>{{StudentName}}</c> sozdiziminden AYRI tutulur: eski programin
    /// kullanicisi bu kisa Turkce adlari biliyor; iki sozdizimi birbirini bozmaz
    /// (buyuk harfle baslayan ad bu desene uymaz).
    /// </summary>
    public static IReadOnlyList<string> NamedPlaceholders(string template) =>
        NamedPlaceholderRegex().Matches(template ?? string.Empty).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Adli degiskenleri degerle degistirir. Sozlukte olmayan degisken oldugu gibi birakilir
    /// (kayit aninda dogrulandigi icin buraya ulasmaz); null deger bos metin olur, cunku
    /// otomatik gonderimde eksik alan (sinifsiz ogrenci, aciklamasiz gelir) olagan bir durumdur
    /// ve SMS'i dusurmemelidir. Ardisik bosluklar tek bosluga indirilir ki artik bosluk kalmasin.
    /// </summary>
    public static string RenderNamed(string template, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rendered = NamedPlaceholderRegex().Replace(template ?? string.Empty, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? (value ?? string.Empty).Trim() : match.Value);
        rendered = MultiSpaceRegex().Replace(rendered, " ").Trim();
        if (rendered.Length > 1600) rendered = rendered[..1600];
        return rendered;
    }

    [GeneratedRegex(@"\{([a-z][a-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex NamedPlaceholderRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
