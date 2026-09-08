namespace Yemekhane.Application.Sms;

/// <summary>
/// Toplu SMS ekraninin varsayilan sablonlari. Okul programi ilk kez acinca sablon listesi
/// bos geliyordu; memur her mesaji sifirdan yazmak zorundaydi. Bu liste yalnizca
/// <c>sms_templates</c> tablosu TAMAMEN bosken (pasif kayit dahil hicbir sablon yokken)
/// tohumlanir: kullanicinin sildigi/duzenledigi sablonlar geri gelmez.
///
/// Degiskenler toplu gonderim sozdizimiyle ({{Ad}}) yazilir; izin verilenler
/// <see cref="SmsTemplateRenderer"/>'daki kumeyle sinirlidir. {{StudentName}} ve
/// {{ParentName}} her alici icin otomatik dolar; {{ExpiryDate}}, {{EntryTime}} ve
/// {{Amount}} SMS ekranindaki "Şablon değişkenleri" kutularindan gelir.
/// </summary>
public static class DefaultSmsTemplates
{
    public static readonly IReadOnlyList<SaveSmsTemplateRequest> All =
    [
        new("Yemek Ücreti Hatırlatma",
            "Sayın {{ParentName}}, {{StudentName}} adlı öğrencinizin yemek ücreti ödemesi beklenmektedir. Son ödeme tarihi: {{ExpiryDate}}. Bilginize."),
        new("Yemek Hakkı Bitiyor",
            "Sayın {{ParentName}}, {{StudentName}} adlı öğrencinizin yemek hakkı {{ExpiryDate}} tarihinde sona erecektir. Yenilemenizi rica ederiz."),
        new("Ödeme Alındı",
            "Sayın {{ParentName}}, {{StudentName}} için {{Amount}} tutarındaki yemek ödemeniz alınmıştır. Teşekkür ederiz."),
        new("Yemekhane Girişi",
            "Sayın {{ParentName}}, {{StudentName}} adlı öğrenciniz bugün saat {{EntryTime}} itibarıyla yemekhaneye giriş yapmıştır."),
        new("Kart Yenilendi",
            "Sayın {{ParentName}}, {{StudentName}} adlı öğrencinizin yemekhane kartı yenilenmiştir. Eski kart artık geçersizdir."),
        new("Genel Bilgilendirme",
            "Sayın {{ParentName}}, {{StudentName}} adlı öğrencinizle ilgili yemekhane bilgilendirmesi için okulumuzla iletişime geçmenizi rica ederiz.")
    ];
}
