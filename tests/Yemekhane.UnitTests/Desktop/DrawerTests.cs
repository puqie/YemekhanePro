using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Cekmece davranisinin tutarli oldugunu dogrular.
///
/// Once 15 cekmece vardi ve 6 farkli genislikteydi (390, 430, 440, 470, 650).
/// Hicbirinde Esc ile kapatma veya odak yonetimi yoktu. StudentsView'de dort
/// cekmece ust uste biniyordu; hangisinin ustte oldugu ZIndex sirasina kalmisti.
/// </summary>
[Collection("UI")]
public sealed class DrawerTests
{
    [Fact]
    public void KapaliCekmeceGorunmez() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { IsOpen = false };

            Assert.Equal(Visibility.Collapsed, drawer.Visibility);
        });

    [Fact]
    public void AcikCekmeceGorunur() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { IsOpen = true };

            Assert.Equal(Visibility.Visible, drawer.Visibility);
        });

    [Fact]
    public void VarsayilanGenislikDarOlcudur() =>
        UiThread.Run(() => Assert.Equal(400d, new Drawer().DrawerWidth));

    /// <summary>
    /// Esc tusunun GERCEKTEN klavye yonlendirmesi (input routing) uzerinden
    /// cekmeceyi kapattigini dogrular -- drawer.Close() cagirmak degil.
    ///
    /// Bunun icin cekmece gercek (gorunmez) bir Window icine yerlestirilir;
    /// boylece PresentationSource/HwndSource gercektir ve Keyboard.Focus
    /// gercek bir klavye aygitina baglanir. Esc tusu InputManager.ProcessInput
    /// ile enjekte edilir -- bu, WPF'in fiziksel bir tus basisini isledigi
    /// AYNI yoldur (RoutedEvent tunelleme/kabarma dahil). Boylece test,
    /// Drawer'in KeyDown isleyicisinin gercekten tetiklendigini kanitlar;
    /// yalnizca Close() metodunun var oldugunu degil.
    ///
    /// Kanitlamadigi sey: PART_Close dugmesine tiklama veya PART_Scrim'e
    /// tiklama davranisi (bunlar OnApplyTemplate ile baglanir ve ayri bir
    /// template gerektirir); bu test yalnizca Esc yolunu dogrular.
    /// </summary>
    [Fact]
    public void EscTusuCekmeceyiKapatir() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { IsOpen = true, Focusable = true };
            UiThread.ApplyResources(drawer);

            var window = new Window
            {
                Content = drawer,
                Width = 200,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = true,
                Left = -3000,
                Top = -3000,
            };

            try
            {
                window.Show();
                drawer.Focus();
                Keyboard.Focus(drawer);

                var device = InputManager.Current.PrimaryKeyboardDevice;
                var source = PresentationSource.FromVisual(drawer);
                Assert.NotNull(source);

                var args = new KeyEventArgs(device, source!, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };

                InputManager.Current.ProcessInput(args);

                Assert.False(drawer.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// Hizli ac/kapa/ac yarisi: acilistaki MoveFocus(First) cagrisi
    /// Dispatcher.BeginInvoke ile ERTELENIR. Cagiran taraf ayni UI islemi
    /// icinde cekmeceyi acip kapatip TEKRAR acarsa (orn. bir liste secimi
    /// degisince cekmece kapanip farkli icerikle yeniden aciliyor), ilk
    /// acilistan kalan ERTELENMIS cagri kuyrukta kalir. Guvenlik kontrolu
    /// yalnizca "IsOpen mi" olsaydi bu cagri, ikinci acilista IsOpen zaten
    /// tekrar true oldugu icin MEsRU gorunup GEREKENDEN BIR FAZLA kez
    /// calisirdi.
    ///
    /// Bunu dogrudan olcmek icin internal MoveFocusInvocationCountForTests
    /// sayacina bakilir. Kara-kutu (public API) bir gozlem yeterli degildi:
    /// iki acilisin MoveFocus cagrisi ayni nihai elemani (bu template'te
    /// PART_Close) hedefliyor, ve WPF zaten odakli bir elemana tekrar
    /// odaklanildiginda GotFocus'u TEKRAR RAISE ETMIYOR -- yani "son odak
    /// nerede" veya "GotFocus kac kez dustu" sorulari, cagrinin bir mi iki
    /// mi kez CALISTIGINI ayirt edemiyor (ilk denemelerim bunu ispatladi:
    /// hem duzeltilmis hem duzeltilmemis kod ayni "son durum"u uretiyordu).
    /// internal sayac bu belirsizligi ortadan kaldirir.
    ///
    /// NOT: Ilk denemede "kapali cekmeceye odak kilitlenir mi" senaryosunu
    /// test ettim (kod inceleme talebinin orijinal ifadesi) ve bu REPRODUCE
    /// OLMADI: MoveFocus, WPF'in kendi kurallari geregi Collapsed bir alt
    /// agaca zaten odaklanamiyor (dogrulandi: MoveFocus() false donuyor,
    /// odak oldugu yerde kaliyor -- WPF'in kendi korumasi). Gercek bulgu
    /// "gorunmez alana odak kilitlenmesi" degil, "guncelligini yitirmis
    /// gecikmis bir cagrinin, hala acik olan cekmecede FAZLADAN bir kez
    /// daha calismasi" -- bu test onu dogrudan olcuyor.
    /// </summary>
    [Fact]
    public void GecikmisIlkAcilisCagrisiIkinciAcilistaFazladanCalismaz() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { Content = new TextBox { Focusable = true } };
            ApplyDrawerTheme(drawer);

            var outside = new TextBox { Focusable = true };
            var root = new StackPanel();
            root.Children.Add(outside);
            root.Children.Add(drawer);

            var window = new Window
            {
                Content = root,
                Width = 200,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = true,
                Left = -3000,
                Top = -3000,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                outside.Focus();
                Keyboard.Focus(outside);

                // Ayni UI isleminde ac, kapat, tekrar ac -- ilk acilistan
                // kalan BeginInvoke(MoveFocus) kuyrukta beklerken cekmece
                // zaten ikinci kez acilmis durumda. Iki cagri da (eski VE
                // yeni nesil) kuyrukta -- henuz hicbiri calismadi.
                drawer.IsOpen = true;
                drawer.IsOpen = false;
                drawer.IsOpen = true;
                window.UpdateLayout();

                // Kuyrugu bosalt: yeni (gecerli) neslin cagrisi calismali;
                // eski (gecersiz) neslin cagrisi -duzeltilmisse- kendini
                // iptal etmeli. Duzeltilmemis kodda IKISI de calisir.
                PumpDispatcher();

                Assert.Equal(1, drawer.MoveFocusInvocationCountForTests);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// OnApplyTemplate yeniden calistiginda (template yeniden uygulanirsa)
    /// PART_Close/PART_Scrim'e eski anonim lambda ile bir kez daha abone
    /// olunmamali. Aksi halde CloseCommand, tek bir tiklamada abonelik
    /// sayisi kadar (N kere) calisir -- Close() kendisi IsOpen uzerinde
    /// idempotent oldugu icin bu bugun bulgulari bugune kadar gizli kaldi.
    /// </summary>
    [Fact]
    public void YenidenTemplateUygulamaKapatKomutunuBirdenFazlaCalistirmaz() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { IsOpen = true };
            ApplyDrawerTheme(drawer);

            var executionCount = 0;
            drawer.CloseCommand = new RelayCommand(() => executionCount++);

            drawer.ApplyTemplate();
            // Template'i ikinci kez uygulanmis gibi zorla -- WPF bunu
            // gercek hayatta bir StyleProperty/Template degisiminde yapar;
            // burada dogrudan cagirarak ayni kod yolunu tetikliyoruz.
            drawer.OnApplyTemplate();

            var close = (ButtonBase)drawer.Template.FindName("PART_Close", drawer);
            close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(1, executionCount);
        });

    private static void ApplyDrawerTheme(FrameworkElement element)
    {
        UiThread.ApplyResources(element);
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/Drawer.xaml")
        });
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private sealed class RelayCommand : ICommand
    {
        private readonly Action execute;

        public RelayCommand(Action execute) => this.execute = execute;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
