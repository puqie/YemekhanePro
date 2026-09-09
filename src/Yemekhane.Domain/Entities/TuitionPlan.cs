namespace Yemekhane.Domain.Entities;

/// <summary>
/// Ucretlendirme bicimi. Okul anasinifinda toplam ucreti taksite boler, bazi sinifta
/// aylik sabit alir, bazi ogrenciden gunluk ucret keser; ucu de ayni plan kaydiyla
/// ifade edilir ki kasa ve ekstre tek yerden okusun.
/// </summary>
public static class TuitionPlanKinds
{
    /// <summary>Toplam ucret + pesinat + N taksit; taksitler onceden satir satir uretilir.</summary>
    public const string Installment = "Installment";
    /// <summary>Aylik sabit ucret; her ay tahakkuk gunu bir taksit satiri uretilir.</summary>
    public const string Monthly = "Monthly";
    /// <summary>Gunluk ucret; taksit uretilmez, gecis aninda bakiyeden bu tutar duser.</summary>
    public const string Daily = "Daily";

    public static readonly IReadOnlyList<string> All = [Installment, Monthly, Daily];

    public static string Label(string? kind) => kind switch
    {
        Monthly => "Aylık sabit ücret",
        Daily => "Günlük ücret",
        _ => "Toplam ücret + taksit"
    };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// Bir sinifin ya da tek bir ogrencinin ucret plani. Sinif plani o siniftaki herkese
/// varsayilan olur; ogrenciye ait plan varsa sinifinkini EZER (kardes indirimi, burslu
/// ogrenci). Iki kapsam ayni tabloda durur: <see cref="ClassId"/> ya da
/// <see cref="StudentId"/> doludur, ikisi birden asla dolu olmaz.
///
/// Tutarlar kurus (long) cinsindendir; kayan nokta yuvarlama hatasi kasa raporunu bozar.
/// </summary>
public sealed class TuitionPlan : Entity
{
    /// <summary>Sinif plani (varsayilan). Ogrenci plani ise null.</summary>
    public Guid? ClassId { get; set; }
    /// <summary>Ogrenciye ozel plan. Sinif plani ise null.</summary>
    public Guid? StudentId { get; set; }
    /// <summary>Installment | Monthly | Daily (bkz. <see cref="TuitionPlanKinds"/>).</summary>
    public required string Kind { get; set; }
    /// <summary>Egitim yili etiketi, orn. "2026-2027"; ayni ogrencinin gecmis yil plani korunur.</summary>
    public required string Period { get; set; }
    /// <summary>Installment: donem toplami. Monthly: aylik tutar. Daily: gunluk tutar.</summary>
    public long AmountCents { get; set; }
    /// <summary>Yalnizca Installment: pesin alinan kisim; taksite bolunmez.</summary>
    public long DownPaymentCents { get; set; }
    /// <summary>Installment: taksit adedi. Monthly: uretilecek ay sayisi. Daily: kullanilmaz.</summary>
    public int InstallmentCount { get; set; }
    /// <summary>Taksitin ayin kacinda tahakkuk edecegi (1-28); Daily'de kullanilmaz.</summary>
    public int DueDayOfMonth { get; set; } = 1;
    /// <summary>Ilk taksitin ayi; sonraki taksitler bunu takip eder.</summary>
    public DateOnly StartsOn { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Taksit satirinin durumu; tutar karsilastirmasiyla hesaplanir, elle set edilmez.</summary>
public static class TuitionInstallmentStatuses
{
    public const string Pending = "Pending";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Paid = "Paid";
    public const string Overdue = "Overdue";
    public const string Cancelled = "Cancelled";

    public static string Label(string? status) => status switch
    {
        Paid => "Ödendi",
        PartiallyPaid => "Kısmi ödendi",
        Overdue => "Gecikmiş",
        Cancelled => "İptal",
        _ => "Bekliyor"
    };
}

/// <summary>
/// Tek bir taksit (ya da aylik tahakkuk). Plan kaydedilirken onceden uretilir ki veli
/// "ne zaman ne kadar odeyecegim" listesini gorsun. Odeme kasadan girilir ve
/// <see cref="PaidCents"/> artar; tahsilatin kendisi IncomeTransaction olarak durur,
/// bu satir yalnizca borcun takibidir.
/// </summary>
public sealed class TuitionInstallment : Entity
{
    public Guid PlanId { get; set; }
    /// <summary>Plan sinifa aitse bile taksit her zaman bir ogrenciye yazilir.</summary>
    public Guid StudentId { get; set; }
    /// <summary>1'den baslar; pesinat 0 numaralidir.</summary>
    public int Sequence { get; set; }
    public DateOnly DueOn { get; set; }
    public long AmountCents { get; set; }
    public long PaidCents { get; set; }
    public bool IsCancelled { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Bir tahsilatin hangi taksite sayildigi. Kasa kaydi (IncomeTransaction) iptal edilirse
/// bu satir da kalkar ve taksit yeniden borclu olur; boylece iptal sessizce borcu
/// kapatmis gibi gorunmez.
/// </summary>
public sealed class TuitionPayment : Entity
{
    public Guid InstallmentId { get; set; }
    public Guid IncomeTransactionId { get; set; }
    public long AmountCents { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    public Guid? CreatedBy { get; set; }
}
