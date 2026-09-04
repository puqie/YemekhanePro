namespace Yemekhane.Application.Calendar;

/// <summary>
/// Tatile denk gelen haklarin hangi gunlere aktarilacagini planlar.
///
/// <para>
/// SORUN: cok gunlu bir tatilde (ornegin bes gun) her gunun hakki AYNI "sonraki is
/// gunune" tasiniyordu. Sonuc, o tek gune yigilmis bes ogun hakkiydi: ogrenci bir
/// gun bes ogun hakkina sahip gorunuyor, izleyen gunler bos kaliyordu. Oysa bes
/// gunluk tatilin karsiligi BES AYRI GUNE dagilmis bes haktir.
/// </para>
/// <para>
/// KURAL: her hak, kendisinden sonraki ilk BOS is gunune gider. Gun doluysa (o
/// ogrenci icin ayni ogunde zaten hak varsa) bir sonrakine bakilir; zincir bos bir
/// gun bulunana kadar devam eder ve tatil gunlerini atlar. Boylece bes gunu olan
/// bes gun, uc gunu olan uc gun alir.
/// </para>
/// <para>
/// Zincirin UZUNLUGU sinirlandirilmaz (kullanici karari): art arda gelen tatiller
/// ve dolu gunler ne kadar surerse sursun hak kaybolmaz, ileriye tasinir. Yalnizca
/// <see cref="BusinessDayService"/> icindeki on yillik tarama siniri gecerlidir --
/// o da sonsuz donguyu onlemek icindir.
/// </para>
/// </summary>
public static class TransferTargetPlanner
{
    /// <param name="sourceDates">
    /// Aktarilacak haklarin ozgun tarihleri. Sirasi onemlidir: erken tarihli hak
    /// once yerlesir, boylece tatilin ilk gunu en yakin bos gune gider.
    /// </param>
    /// <param name="isOccupied">
    /// Verilen gunde bu ogrenci-ogun icin ZATEN hak var mi. Planlayici kendi
    /// yerlestirdiklerini de dolu sayar; bu fonksiyon yalnizca ONCEDEN var olanlari
    /// bildirir.
    /// </param>
    /// <param name="nextBusinessDay">
    /// Verilen tarihten SONRAKI ilk is gunu. Tatil ve hafta sonu atlama burada olur.
    /// </param>
    /// <returns>
    /// Her kaynak tarih icin hedef tarih, kaynak sirasiyla. Bir kaynak icin hedef
    /// bulunamazsa (tarama siniri) o kaynak listede YER ALMAZ; cagiran bunu
    /// kullaniciya bildirmelidir -- sessizce dusurmek hak kaybi demektir.
    /// </returns>
    public static async Task<IReadOnlyList<(DateOnly Source, DateOnly Target)>> PlanAsync(
        IEnumerable<DateOnly> sourceDates,
        Func<DateOnly, CancellationToken, Task<bool>> isOccupied,
        Func<DateOnly, CancellationToken, Task<DateOnly?>> nextBusinessDay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDates);
        ArgumentNullException.ThrowIfNull(isOccupied);
        ArgumentNullException.ThrowIfNull(nextBusinessDay);

        var plan = new List<(DateOnly Source, DateOnly Target)>();
        // Bu calistirmada YERLESTIRILEN gunler. Olmasaydi ayni tatilin iki gunu ayni
        // hedefe yigilirdi -- duzeltilmek istenen hatanin ta kendisi.
        var claimed = new HashSet<DateOnly>();

        foreach (var source in sourceDates.OrderBy(x => x))
        {
            var cursor = source;
            while (true)
            {
                var candidate = await nextBusinessDay(cursor, cancellationToken).ConfigureAwait(false);
                // Tarama siniri asildi: bu hak icin hedef yok. Sessizce atlanir ve
                // cagiran eksik satiri gorup kullaniciya bildirir.
                if (candidate is null) break;

                if (!claimed.Contains(candidate.Value)
                    && !await isOccupied(candidate.Value, cancellationToken).ConfigureAwait(false))
                {
                    claimed.Add(candidate.Value);
                    plan.Add((source, candidate.Value));
                    break;
                }
                // Gun dolu: zincir devam eder. Bir sonraki aramaya BU adaydan devam
                // edilir, kaynaktan degil; aksi halde ayni dolu gun sonsuza dek
                // yeniden bulunurdu.
                cursor = candidate.Value;
            }
        }

        return plan;
    }
}
