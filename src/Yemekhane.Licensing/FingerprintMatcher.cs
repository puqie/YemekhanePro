namespace Yemekhane.Licensing;

/// <summary>
/// Iki parmak izinin ayni makineye ait olup olmadigina karar verir.
///
/// KURAL: uc bilesenden IKISI tutuyorsa ayni makine sayilir.
/// Kati eslesme musteriyi disk veya anakart degisiminde magdur eder; tek bilesen ise
/// sanal makineye kopyalanmayi serbest birakir. 2/3 bu dengeyi kurar.
/// </summary>
public static class FingerprintMatcher
{
    /// <summary>Ayni makine sayilmak icin gereken eslesme sayisi.</summary>
    public const int RequiredMatches = 2;

    /// <summary>Lisanstaki hash'lerin bu makineye ait olup olmadigi.</summary>
    public static bool Matches(IReadOnlyList<string> stored, IReadOnlyList<string> current)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);

        return CountMatches(stored, current) >= RequiredMatches;
    }

    /// <summary>Kac bilesenin tuttugu. Tanilama ve testler icin ayri durur.</summary>
    public static int CountMatches(IReadOnlyList<string> stored, IReadOnlyList<string> current)
    {
        var matches = 0;
        var limit = Math.Min(stored.Count, current.Count);
        for (var index = 0; index < limit; index++)
        {
            // Okunamayan bilesen bos dizedir. Iki bos degeri "eslesti" saymak,
            // WMI erisimi olmayan her makineyi birbirine esit yapardi.
            if (string.IsNullOrEmpty(stored[index])) continue;
            if (stored[index] == current[index]) matches++;
        }

        return matches;
    }
}
