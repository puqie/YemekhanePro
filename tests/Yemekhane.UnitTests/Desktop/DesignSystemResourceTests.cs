using System.Windows;
using System.Windows.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Tema kaynaklarinin gercekten tanimli oldugunu dogrular.
///
/// Eksik bir StaticResource anahtari XAML derlenmesinde degil, sayfa
/// olusturulurken patlar. Bu test eksigi derleme zamanina cekiyor.
/// </summary>
[Collection("UI")]
public sealed class DesignSystemResourceTests
{
    [Theory]
    [InlineData("NavItem")]
    [InlineData("NavGroupTitle")]
    [InlineData("BadgeSuccess")]
    [InlineData("BadgeNeutral")]
    [InlineData("BadgeDanger")]
    [InlineData("IdentityText")]
    [InlineData("SidebarBrush")]
    [InlineData("SidebarHoverBrush")]
    [InlineData("ScrimBrush")]
    [InlineData("ScrimStrongBrush")]
    [InlineData("FieldCompact")]
    [InlineData("FieldCompactCombo")]
    [InlineData("FieldCompactDate")]
    public void ResourceKeyIsDefined(string key) =>
        UiThread.Run(() =>
        {
            var element = new Border();
            UiThread.ApplyResources(element);

            Assert.True(element.Resources.Contains(key), $"'{key}' tema kaynagi tanimli degil.");
        });
}
