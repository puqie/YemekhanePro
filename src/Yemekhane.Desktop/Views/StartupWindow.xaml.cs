using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Yemekhane.Desktop.Views;

/// <summary>
/// Acilis ekrani.
///
/// <para>
/// NEDEN VAR: ilk acilista yerel API veritabanini olusturur ve gocleri uygular.
/// Yavas diskli bir okul bilgisayarinda bu DAKIKALAR surebiliyor ve o sure boyunca
/// ekranda HICBIR SEY yoktu -- kullanici programin dondugunu saniyordu. Sahada
/// gelen sikayet buydu.
/// </para>
/// <para>
/// Beklemenin uzun surebilecegi ACIKCA yazilir: "birkac dakika surebilir" demek,
/// bos bir ekranda beklemekten cok farklidir.
/// </para>
/// </summary>
public partial class StartupWindow : Window, INotifyPropertyChanged
{
    private string statusText = string.Empty;

    public StartupWindow()
    {
        InitializeComponent();
        DataContext = this;
        StatusText = InitialMessage;
    }

    /// <summary>
    /// Ilk mesaj. Sureyi ONCEDEN soyler: kullanici ne kadar bekleyecegini bilmezse
    /// otuzuncu saniyede programi kapatir ve yarim kalmis bir veritabani birakir.
    /// </summary>
    public const string InitialMessage =
        "Başlatılıyor...\nİlk açılışta veritabanı hazırlanır; eski bilgisayarlarda birkaç dakika sürebilir. Lütfen kapatmayın.";

    public string StatusText
    {
        get => statusText;
        set { statusText = value; Changed(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}
