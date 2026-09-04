namespace Yemekhane.Application.Entitlements;

/// <summary>
/// "Kac gun" sayisindan bitis tarihi hesaplar.
///
/// <para>
/// Kullanici hakedis verirken bitis TARIHI degil, GUN SAYISI dusunur: "bu ogrenciye
/// 10 gunluk yemek hakki". Bitis tarihini elle bulmak icin takvime bakip hafta
/// sonlarini saymak gerekiyordu ve yanlis sayilan her gun eksik ya da fazla hak
/// olusturuyordu.
/// </para>
/// <para>
/// Sayim IS GUNU uzerindendir: istenen sayida YEMEK GUNU bulunana kadar ilerlenir,
/// dahil edilmeyen gunler (hafta sonu) atlanir. "10 gun" her zaman 10 hak demektir --
/// takvim gunu sayilsaydi hafta sonuna denk gelen istekte hak sayisi degisirdi.
/// </para>
/// </summary>
public static class WorkingDayRange
{
    /// <summary>
    /// Guvenlik siniri: hicbir gun dahil edilmemisse (cumartesi ve pazar kapali,
    /// baslangic da hafta sonu) dongu sonsuza gider. Bes yillik bir pencere her
    /// gercekci istegi karsilar ve donguyu bitirir.
    /// </summary>
    private const int MaximumScannedDays = 365 * 5;

    /// <summary>Bir gunun hakedis olusturulacak gunlerden olup olmadigi.</summary>
    public static bool IsIncluded(DateOnly date, bool includeSaturday, bool includeSunday) =>
        date.DayOfWeek switch
        {
            DayOfWeek.Saturday => includeSaturday,
            DayOfWeek.Sunday => includeSunday,
            _ => true
        };

    /// <summary>
    /// <paramref name="startsOn"/> tarihinden baslayarak <paramref name="dayCount"/>
    /// adet dahil gun kapsayan araligin BITIS tarihini verir.
    ///
    /// <para>
    /// Baslangic gunu dahil degilse (ornegin pazar, pazar kapaliyken) ileri kaydirilir:
    /// kullanicinin pazar gunu girmesi "hak yok" degil, "ertesi is gununden basla"
    /// anlamina gelir.
    /// </para>
    /// </summary>
    /// <returns>
    /// Bitis tarihi; istenen sayida gun bes yil icinde bulunamazsa <c>null</c>
    /// (hicbir gun dahil edilmemis demektir).
    /// </returns>
    public static DateOnly? EndDateFor(DateOnly startsOn, int dayCount, bool includeSaturday, bool includeSunday)
    {
        if (dayCount < 1) return null;

        var found = 0;
        var cursor = startsOn;
        for (var scanned = 0; scanned < MaximumScannedDays; scanned++)
        {
            if (IsIncluded(cursor, includeSaturday, includeSunday))
            {
                found++;
                // Bitis, sayilan SON dahil gundur; sonraki gune tasinmaz, aksi halde
                // aralik hafta sonunu da kapsayip gereksiz genis gorunurdu.
                if (found == dayCount) return cursor;
            }
            cursor = cursor.AddDays(1);
        }
        return null;
    }

    /// <summary>
    /// Aralikta kac dahil gun bulundugu. Bitis tarihinden gun sayisina donmek icin
    /// (mevcut kayitlarin gosterimi) kullanilir.
    /// </summary>
    public static int CountIncludedDays(DateOnly startsOn, DateOnly endsOn, bool includeSaturday, bool includeSunday)
    {
        if (endsOn < startsOn) return 0;

        var count = 0;
        for (var cursor = startsOn; cursor <= endsOn; cursor = cursor.AddDays(1))
            if (IsIncluded(cursor, includeSaturday, includeSunday)) count++;
        return count;
    }
}
