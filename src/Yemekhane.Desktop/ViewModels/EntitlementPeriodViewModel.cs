using System;
using System.Globalization;
using Yemekhane.Application.Entitlements;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Bir ogunun hakedis donemini EKRAN DILINE cevirir: "14 öğün kaldı", "Son gün: 10 Ekim
/// 2026", "5 gün kaldı" / "12 gün önce bitti".
///
/// Veli telefondayken kullanici hesap yapmak zorunda kalmasin diye tum metinler hazir
/// gelir; ekran yalnizca baglar.
/// </summary>
public sealed class EntitlementPeriodViewModel(EntitlementPeriodSummary period)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public Guid StudentId => period.StudentId;
    public string StudentNo => period.StudentNo;
    public string StudentName => period.StudentName;
    public string? ClassName => period.ClassName;
    public string? ParentPhone => period.ParentPhone;
    public string MealName => period.MealName;

    public int RemainingQuantity => period.RemainingQuantity;
    public int DaysLeft => period.DaysLeft;
    public bool IsExpired => period.IsExpired;

    /// <summary>Kutunun ana rakami: veliye soylenecek sayi.</summary>
    public string RemainingText => $"{period.RemainingQuantity:N0} öğün";

    public string UsageText =>
        $"Toplam {period.TotalQuantity:N0} · Kullanılan {period.ConsumedQuantity:N0}";

    /// <summary>Yanan hak varsa gosterilir; yoksa satir hic cikmaz.</summary>
    public bool HasExpiredQuantity => period.ExpiredQuantity > 0;
    public string ExpiredText => $"{period.ExpiredQuantity:N0} öğün kullanılmadan geçti";

    public string PeriodText => $"{Format(period.FirstDate)} – {Format(period.LastDate)}";
    public string LastDateText => Format(period.LastDate);
    public string RenewFromText => $"Yenileme: {Format(period.RenewFrom)} tarihinden itibaren";

    /// <summary>
    /// "5 gün kaldı" / "bugün son gün" / "12 gün önce bitti". Gecmis donemde kullanici
    /// yenilemeyi ATLAMIS demektir; metin bunu acikca soyler.
    /// </summary>
    public string DaysLeftText => period.DaysLeft switch
    {
        < 0 => $"{-period.DaysLeft:N0} gün önce bitti",
        0 => "Bugün son gün",
        _ => $"{period.DaysLeft:N0} gün kaldı"
    };

    /// <summary>Bitmis ya da bir haftadan az kalmis donem ekranda VURGULANIR.</summary>
    public bool IsUrgent => period.DaysLeft <= 7;

    private static string Format(DateOnly date) => date.ToString("d MMMM yyyy", Turkish);
}
