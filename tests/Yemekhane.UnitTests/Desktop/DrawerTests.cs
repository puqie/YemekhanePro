using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
