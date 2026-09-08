namespace Yemekhane.Desktop.Services;

/// <summary>
/// Yazilimcinin iletisim bilgisi; okul bir sorun yasadiginda kime ulasacagini programin her
/// penceresinde (giris, lisans, parola sifirlama, ana pencere, F1 yardimi, Ayarlar) gorur.
/// Tek kaynaktan baglanir; AI kilavuzu const oldugu icin ayni degerler orada da yazilidir ve
/// DeveloperContactTests ikisinin ayni kalmasini kilitler.
/// </summary>
public static class DeveloperContact
{
    public const string Website = "puyi.com.tr";
    public const string Phone = "0552 999 96 96";
    public const string Phone2 = "0507 609 66 91";
    public const string Label = "Yazılım desteği";
    public const string Line = Label + ": " + Website + " • " + Phone + " • " + Phone2;
}
