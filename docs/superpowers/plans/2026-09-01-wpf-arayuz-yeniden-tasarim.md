# WPF Arayüz Yeniden Tasarımı — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Masaüstü arayüzü tek tema kaynağı, ortak sayfa iskeleti ve üç ana ekranda liste+form düzeni ile yeniden kurmak.

**Architecture:** `DesignSystem.xaml` tek tema kaynağı olur; 13 dosyadaki yerel renk/stil tanımları silinir. Ortak `PageShell` ve `Drawer` kontrolleri eklenir. En sık kullanılan üç ekran (Öğrenciler, Hakedişler, Kasa) çekmece yerine liste+form yan yana düzenine geçer. ViewModel'lere, gezinme servislerine ve API'ye dokunulmaz.

**Tech Stack:** .NET 10, WPF, XAML, xUnit

**Spec:** `docs/superpowers/specs/2026-09-01-wpf-arayuz-yeniden-tasarim-design.md`

## Global Constraints

- **ViewModel dosyaları değiştirilmez.** `src/Yemekhane.Desktop/ViewModels/` altındaki hiçbir `.cs` dosyası düzenlenmez. Tüm binding adları korunur.
- **View sınıf adları korunur.** `StudentsView`, `CashView`, `MealEntitlementsView` vb. sınıf adları ve namespace'leri değişmez — `ViewLayoutTests` bunları `new StudentsView()` ile kuruyor.
- **API ve veritabanı şemasına dokunulmaz.** `src/Yemekhane.Api` ve `src/Yemekhane.Infrastructure` değiştirilmez. Çalışma ağacında yarım kalmış cihaz entegrasyonu işleri var.
- **`DevicesView.xaml` ve `DeviceCardsView.xaml` yeniden yazılmaz.** Yalnızca yerel renk/stil tanımları silinir; yerleşimleri korunur. `DevicesView.xaml` çalışma ağacında kaydedilmemiş değişiklik taşıyor.
- **Renkler yalnızca `DesignSystem.xaml`'de tanımlanır.** View dosyalarında ham hex renk (`#65717E`, `#E1E5E9` vb.) kullanılmaz; `{StaticResource ...}` kullanılır.
- **WCAG AA:** metin renkleri beyaz zeminde en az 4.5:1 kontrast vermeli (`BrandPaletteTests` doğrular).
- **Giriş kutusu en az 220px kullanılabilir genişlik** (`FieldWidthTests` doğrular).
- **Metin kırpılmamalı** (`TextFitsTests` doğrular).
- Her görev sonunda `dotnet build` ve ilgili testler çalıştırılır.
- Commit mesajları Türkçe, ASCII karakterlerle (mevcut git geçmişi bu düzeni izliyor).

---

## Mevcut Test Güvenlik Ağı

`tests/Yemekhane.UnitTests/Desktop/` altında 26 test dosyası XAML'i doğrudan denetliyor. Bunlar yeniden tasarımın güvenlik ağıdır — **hiçbiri devre dışı bırakılmaz.**

| Test | Ne doğrular |
|---|---|
| `ViewLayoutTests` | Her view 1600x900'de taşmadan ölçümlenir |
| `FieldWidthTests` | Giriş kutuları ≥220px kullanılabilir genişlikte |
| `TextFitsTests` | Metinler kabına sığar, kırpılmaz |
| `BrandPaletteTests` | Renkler WCAG AA, eski teal marka rengi yok |
| `BindingIntegrityTests` | Binding'ler ViewModel'de karşılık buluyor |
| `ConverterUsageTests` | Converter'lar doğru hedef türle kullanılıyor |
| `VisibleLabelTests`, `FieldLabelTests` | Alanların görünür etiketi var |

Bu testler elle denerken bulunan gerçek hatalardan doğmuş (Ayarlar'da 95px kutular, kenar çubuğunda kırpılmış "YEMEKHANEPRC" yazısı).

---

## Dosya Yapısı

**Yeni dosyalar:**

| Dosya | Sorumluluk |
|---|---|
| `src/Yemekhane.Desktop/Controls/PageShell.cs` | Ortak sayfa iskeleti (başlık/filtre/içerik/alt bant) |
| `src/Yemekhane.Desktop/Themes/PageShell.xaml` | `PageShell` şablonu |
| `src/Yemekhane.Desktop/Controls/Drawer.cs` | Çekmece kontrolü (Esc, odak, tek-açık) |
| `src/Yemekhane.Desktop/Themes/Drawer.xaml` | `Drawer` şablonu |
| `src/Yemekhane.Desktop/Converters/StudentIdentityConverter.cs` | `AD SOYAD · No X · Sınıf · Kart Y` biçimi |
| `src/Yemekhane.Desktop/Converters/StatusBadgeConverter.cs` | bool → "Aktif"/"Pasif" metni |
| `src/Yemekhane.Desktop/Converters/StatusBrushConverter.cs` | bool → rozet rengi |

**Değiştirilen dosyalar:**

| Dosya | Değişiklik |
|---|---|
| `Themes/DesignSystem.xaml` | Rozet, menü, kimlik stilleri eklenir |
| `MainWindow.xaml` | Yerel stiller silinir, menü gruplanır, üst bant eklenir |
| `Views/StudentsView.xaml` | Liste+form düzeni |
| `Views/MealEntitlementsView.xaml` | Çoklu seçim + atama formu |
| `Views/CashView.xaml` | Liste+form düzeni |
| Diğer 9 view | Yerel stiller silinir, `PageShell` uygulanır |

---

## AŞAMA 1 — ALTYAPI

### Task 1: Tema kaynaklarını genişlet

**Files:**
- Modify: `src/Yemekhane.Desktop/Themes/DesignSystem.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/DesignSystemResourceTests.cs` (yeni)

**Interfaces:**
- Produces: `NavItem`, `NavGroupTitle`, `BadgeSuccess`, `BadgeNeutral`, `BadgeDanger`, `IdentityText` stil anahtarları; `SidebarBrush`, `SidebarHoverBrush` fırçaları.

- [ ] **Step 1: Yeni kaynak anahtarlarının varlığını doğrulayan testi yaz**

`tests/Yemekhane.UnitTests/Desktop/DesignSystemResourceTests.cs`:

```csharp
using System.Windows;

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
    public void ResourceKeyIsDefined(string key) =>
        UiThread.Run(() =>
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/Yemekhane.Desktop;component/Themes/DesignSystem.xaml",
                    UriKind.Absolute)
            };

            Assert.True(dictionary.Contains(key), $"'{key}' tema kaynagi tanimli degil.");
        });
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DesignSystemResourceTests"`
Expected: FAIL — "'NavItem' tema kaynagi tanimli degil."

- [ ] **Step 3: Kaynakları `DesignSystem.xaml`'e ekle**

`</ResourceDictionary>` kapanışından hemen önce ekle:

```xml
    <!-- ============================================================
         KENAR CUBUGU  Menu ogeleri tek stilden uretilir; 12 kez
         tekrarlanan elle yazim boylece biter.
         ============================================================ -->
    <SolidColorBrush x:Key="SidebarBrush"      Color="#18222D"/>
    <SolidColorBrush x:Key="SidebarHoverBrush" Color="#2F3B49"/>
    <SolidColorBrush x:Key="SidebarTextBrush"  Color="#D6DBE0"/>

    <Style x:Key="NavGroupTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="{StaticResource FontXs}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="#8A96A0"/>
        <Setter Property="Margin" Value="12,16,0,6"/>
    </Style>

    <!-- Secili oge sol kenarinda turuncu serit tasir: koyu zeminde
         yalnizca arka plan rengi zayif bir sinyaldir. -->
    <Style x:Key="NavItem" TargetType="Button">
        <Setter Property="Foreground" Value="{StaticResource SidebarTextBrush}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="12,9"/>
        <Setter Property="Margin" Value="0,1,0,0"/>
        <Setter Property="FontSize" Value="{StaticResource FontMd}"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Grid>
                        <Border x:Name="stripe" Width="3" HorizontalAlignment="Left"
                                Background="Transparent" CornerRadius="0,2,2,0"/>
                        <Border x:Name="bd" Background="{TemplateBinding Background}"
                                Padding="{TemplateBinding Padding}" Margin="3,0,0,0"
                                SnapsToDevicePixels="True">
                            <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"
                                              RecognizesAccessKey="True"/>
                        </Border>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background"
                                    Value="{StaticResource SidebarHoverBrush}"/>
                            <Setter Property="Foreground" Value="White"/>
                        </Trigger>
                        <Trigger Property="Tag" Value="secili">
                            <Setter TargetName="stripe" Property="Background"
                                    Value="{StaticResource AccentBrush}"/>
                            <Setter TargetName="bd" Property="Background"
                                    Value="{StaticResource SidebarHoverBrush}"/>
                            <Setter Property="Foreground" Value="White"/>
                            <Setter Property="FontWeight" Value="SemiBold"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ============================================================
         ROZETLER  Ham bool degeri ekranda "True"/"False" olarak
         gorunuyordu; durum artik renkli bir rozettir.
         ============================================================ -->
    <Style x:Key="BadgeNeutral" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource SunkenBrush}"/>
        <Setter Property="CornerRadius" Value="9"/>
        <Setter Property="Padding" Value="8,2"/>
        <Setter Property="HorizontalAlignment" Value="Left"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
    <Style x:Key="BadgeSuccess" TargetType="Border" BasedOn="{StaticResource BadgeNeutral}">
        <Setter Property="Background" Value="{StaticResource SuccessSoftBrush}"/>
    </Style>
    <Style x:Key="BadgeDanger" TargetType="Border" BasedOn="{StaticResource BadgeNeutral}">
        <Setter Property="Background" Value="{StaticResource DangerSoftBrush}"/>
    </Style>

    <!-- Ogrenci kimligi: ad soyad tek basina ayirt edici degildir. -->
    <Style x:Key="IdentityText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="{StaticResource FontMd}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>
```

- [ ] **Step 4: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DesignSystemResourceTests"`
Expected: PASS (8 test)

- [ ] **Step 5: Marka paleti testinin hâlâ geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~BrandPaletteTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Yemekhane.Desktop/Themes/DesignSystem.xaml tests/Yemekhane.UnitTests/Desktop/DesignSystemResourceTests.cs
git commit -m "Tema kaynaklari: menu, rozet ve kimlik stilleri"
```

---

### Task 2: Kimlik ve rozet converter'ları

**Files:**
- Create: `src/Yemekhane.Desktop/Converters/StudentIdentityConverter.cs`
- Create: `src/Yemekhane.Desktop/Converters/StatusBadgeConverter.cs`
- Create: `src/Yemekhane.Desktop/Converters/StatusBrushConverter.cs`
- Test: `tests/Yemekhane.UnitTests/Desktop/StudentIdentityConverterTests.cs` (yeni)

**Interfaces:**
- Produces: `StudentIdentityConverter` — `IMultiValueConverter`, sırayla `[ad, soyad, no, sınıf, kart]` alır, `string` döner. `StatusBadgeConverter` — `bool` → `"Aktif"`/`"Pasif"`. `StatusBrushConverter` — `bool` → `Brush`.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/StudentIdentityConverterTests.cs`:

```csharp
using System.Globalization;
using Yemekhane.Desktop.Converters;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci kimliginin ayirt edici oldugunu dogrular.
///
/// Bu test gercek bir veri sorunundan dogdu: ayni ad soyada sahip birden
/// fazla ogrenci var (ADA KATIRCI / ADA HASLAMACI / ADA SOYLEMEZ).
/// CashViewModel.VoidConfirmationText bir islemi iptal ederken yalnizca
/// tutar ve ad soyad gosteriyordu; kullanici hangi kisinin islemini iptal
/// ettigini bilemezdi.
/// </summary>
public sealed class StudentIdentityConverterTests
{
    private static string Convert(params object?[] values) =>
        (string)new StudentIdentityConverter()
            .Convert(values, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void TumAlanlarVarsaHepsiniGosterir() =>
        Assert.Equal("FATİH SİDAL · No 5371 · 6E · Kart 8352094",
            Convert("FATİH", "SİDAL", "5371", "6E", "8352094"));

    [Fact]
    public void SinifYoksaOAlaniAtlar() =>
        Assert.Equal("FATİH SİDAL · No 5371 · Kart 8352094",
            Convert("FATİH", "SİDAL", "5371", null, "8352094"));

    [Fact]
    public void KartYoksaOAlaniAtlar() =>
        Assert.Equal("FATİH SİDAL · No 5371 · 6E",
            Convert("FATİH", "SİDAL", "5371", "6E", null));

    [Fact]
    public void BosMetinYokSayilir() =>
        Assert.Equal("FATİH SİDAL · No 5371",
            Convert("FATİH", "SİDAL", "5371", "  ", ""));

    /// <summary>Kimlik hicbir zaman yalnizca ad soyad olmamali.</summary>
    [Fact]
    public void NumarasizOgrenciDeAyirtEdiciBilgiTasir() =>
        Assert.Equal("FATİH SİDAL · Kart 8352094",
            Convert("FATİH", "SİDAL", null, null, "8352094"));
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~StudentIdentityConverterTests"`
Expected: FAIL — derleme hatası, `StudentIdentityConverter` bulunamadı

- [ ] **Step 3: Converter'ları yaz**

`src/Yemekhane.Desktop/Converters/StudentIdentityConverter.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Ogrenciyi ayirt edici bicimde yazar: "AD SOYAD · No 5371 · 6E · Kart 8352094".
///
/// Ad soyad tek basina yetmez; veride ayni isimden birden fazla kisi vardir.
/// Deger sirasi: ad, soyad, numara, sinif, kart.
/// </summary>
public sealed class StudentIdentityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        static string? Clean(object? value) =>
            value?.ToString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

        var first = values.Length > 0 ? Clean(values[0]) : null;
        var last = values.Length > 1 ? Clean(values[1]) : null;
        var no = values.Length > 2 ? Clean(values[2]) : null;
        var className = values.Length > 3 ? Clean(values[3]) : null;
        var card = values.Length > 4 ? Clean(values[4]) : null;

        var builder = new StringBuilder();
        var name = string.Join(' ', new[] { first, last }.Where(part => part is not null));
        if (name.Length > 0) builder.Append(name);

        void Append(string text)
        {
            if (builder.Length > 0) builder.Append(" · ");
            builder.Append(text);
        }

        if (no is not null) Append($"No {no}");
        if (className is not null) Append(className);
        if (card is not null) Append($"Kart {card}");

        return builder.ToString();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Kimlik metni yalnizca goruntuleme icindir.");
}
```

`src/Yemekhane.Desktop/Converters/StatusBadgeConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Ham bool degerini okunabilir duruma cevirir.
/// Once ekranda "True" / "False" yaziyordu.
/// </summary>
public sealed class StatusBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Aktif" : "Pasif";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Durum metni yalnizca goruntuleme icindir.");
}
```

`src/Yemekhane.Desktop/Converters/StatusBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Yemekhane.Desktop.Converters;

/// <summary>Durum rozetinin zemin rengini secer.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "SuccessSoftBrush" : "SunkenBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Rozet rengi yalnizca goruntuleme icindir.");
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~StudentIdentityConverterTests"`
Expected: PASS (5 test)

- [ ] **Step 5: `App.xaml`'e converter'ları kaydet**

`src/Yemekhane.Desktop/App.xaml` içinde `<local:InverseBooleanConverter .../>` satırının altına ekle:

```xml
            <converters:StudentIdentityConverter x:Key="StudentIdentity" />
            <converters:StatusBadgeConverter x:Key="StatusBadge" />
            <converters:StatusBrushConverter x:Key="StatusBrush" />
```

Ve `<Application ...>` etiketine namespace ekle:

```xml
             xmlns:converters="clr-namespace:Yemekhane.Desktop.Converters"
```

- [ ] **Step 6: Build ve converter testleri**

Run: `dotnet build src/Yemekhane.Desktop/Yemekhane.Desktop.csproj --nologo`
Expected: 0 Hata

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~ConverterUsageTests"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Yemekhane.Desktop/Converters/ src/Yemekhane.Desktop/App.xaml tests/Yemekhane.UnitTests/Desktop/StudentIdentityConverterTests.cs
git commit -m "Ogrenci kimligi ve durum rozeti donusturuculeri"
```

---

### Task 3: Yerel stil tanımlarını sil

**Files:**
- Modify: `src/Yemekhane.Desktop/MainWindow.xaml`
- Modify: `src/Yemekhane.Desktop/Views/*.xaml` (12 dosya)
- Test: `tests/Yemekhane.UnitTests/Desktop/SingleThemeSourceTests.cs` (yeni)

**Interfaces:**
- Consumes: Task 1'in tema kaynakları.
- Produces: Yerel renk tanımı içermeyen view dosyaları.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/SingleThemeSourceTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Temanin TEK kaynak oldugunu dogrular.
///
/// Bu test gercek bir tutarsizliktan dogdu: DesignSystem.xaml iyi yazilmisti
/// ama 13 dosya kendi renk ve stillerini yeniden tanimliyordu. StudentsView
/// temayi merge edip UZERINE yaziyordu, yani merge etkisizdi. Sonuc: tek
/// tasarim sistemi, 13 ayri gerceklik.
/// </summary>
public sealed class SingleThemeSourceTests
{
    private static readonly string ViewRoot = Path.Combine(
        RepositoryRoot(), "src", "Yemekhane.Desktop");

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(ViewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")) continue;
            data.Add(Path.GetRelativePath(ViewRoot, path));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void ViewDefinesNoLocalBrush(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ViewRoot, relativePath));

        Assert.DoesNotContain("<SolidColorBrush", text);
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void ViewUsesNoRawHexColour(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ViewRoot, relativePath));
        var matches = Regex.Matches(text, @"(Background|Foreground|BorderBrush)=""#[0-9A-Fa-f]{6,8}""");

        Assert.True(matches.Count == 0,
            $"{relativePath}: ham renk kullanimi -- {string.Join(", ", matches.Select(m => m.Value))}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~SingleThemeSourceTests"`
Expected: FAIL — `MainWindow.xaml`, `StudentsView.xaml` ve diğerleri ham renk kullanıyor

- [ ] **Step 3: `MainWindow.xaml` yerel tanımlarını sil**

`<Window.Resources>` içindeki şu satırları sil:

```xml
        <SolidColorBrush x:Key="Ink" Color="#18222D" />
        <SolidColorBrush x:Key="Muted" Color="#65717E" />
        <SolidColorBrush x:Key="Line" Color="#E1E5E9" />
        <SolidColorBrush x:Key="Accent" Color="#C33A02" />
```

Ve yerel `Panel`, `SectionTitle`, `QuietButton` stillerini sil.

Kullanımları değiştir:
- `{StaticResource Ink}` → `{StaticResource InkBrush}`
- `{StaticResource Muted}` → `{StaticResource MutedBrush}`
- `{StaticResource Line}` → `{StaticResource BorderBrush}`
- `{StaticResource Accent}` → `{StaticResource AccentBrush}`
- `{StaticResource QuietButton}` → `{StaticResource Action}`

Ham renkleri kaynak referanslarına çevir:
- `Foreground="#D6DBE0"` → `Foreground="{StaticResource SidebarTextBrush}"`
- `Background="#2F3B49"` → `Background="{StaticResource SidebarHoverBrush}"`
- `Foreground="#65717E"` → `Foreground="{StaticResource MutedBrush}"`
- `Background="#F5F6F7"` → `Background="{StaticResource CanvasBrush}"`

- [ ] **Step 4: 12 view dosyasındaki yerel stilleri sil**

Her view için:
1. `UserControl.Resources` içindeki yerel `Field`, `Action`, `Label` stillerini sil (tema zaten tanımlıyor).
2. `Background="#F4F5F7"` → `Background="{StaticResource CanvasBrush}"`
3. `Foreground="#18222D"` → `Foreground="{StaticResource InkBrush}"`
4. `Foreground="#65717E"` → `Foreground="{StaticResource MutedBrush}"`
5. `Foreground="#9C342F"` → `Foreground="{StaticResource DangerBrush}"`
6. `Foreground="#C33A02"` → `Foreground="{StaticResource AccentBrush}"`
7. `BorderBrush="#E1E5E9"` → `BorderBrush="{StaticResource BorderBrush}"`
8. `BorderBrush="#CBD2D8"` → `BorderBrush="{StaticResource BorderStrongBrush}"`
9. `Background="#FFF3E5"` → `Background="{StaticResource WarningSoftBrush}"`
10. `Foreground="#8A681D"` → `Foreground="{StaticResource WarningBrush}"`

**Not:** `DevicesView.xaml` ve `DeviceCardsView.xaml`'de yalnızca renk değişimi yapılır, yerleşime dokunulmaz.

- [ ] **Step 5: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~SingleThemeSourceTests"`
Expected: PASS

- [ ] **Step 6: Tüm arayüz testlerini çalıştır**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~Desktop"`
Expected: PASS — özellikle `ViewLayoutTests`, `TextFitsTests`, `FieldWidthTests`, `BrandPaletteTests`

**Bu adım aşamanın en riskli noktasıdır.** Yerel stiller silinince tema değerleri devreye girer; bir ölçü değişirse düzen testleri düşer. Düşen test varsa tema değerini değil, view'deki kullanımı düzelt.

- [ ] **Step 7: Commit**

```bash
git add src/Yemekhane.Desktop/ tests/Yemekhane.UnitTests/Desktop/SingleThemeSourceTests.cs
git commit -m "Tek tema kaynagi: 13 dosyadaki yerel renk ve stiller silindi"
```

---

### Task 4: Kabuk — menü grupları ve üst bant

**Files:**
- Modify: `src/Yemekhane.Desktop/MainWindow.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/NavigationGroupingTests.cs` (yeni)

**Interfaces:**
- Consumes: Task 1'in `NavItem` ve `NavGroupTitle` stilleri.
- Produces: Gruplu menü yapısı; `x:Name="NavigationButtons"` korunur.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/NavigationGroupingTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Menunun gruplanmis oldugunu dogrular.
///
/// Once 12 oge ayni punto, ayni renk ve ayni dolguyla alt alta diziliydi;
/// sirasi da mantiksizdi (Kart Yukleme Durumu ile SMS Merkezi yan yana).
/// Kullanici her seferinde 12 satiri bastan tariyordu.
/// </summary>
[Collection("UI")]
public sealed class NavigationGroupingTests
{
    [Fact]
    public void MenuUcGrupBasligiTasir() =>
        UiThread.Run(() =>
        {
            var window = new MainWindow();
            UiThread.ApplyResources(window);

            var panel = (Panel)window.FindName("NavigationButtons")!;
            var titles = panel.Children.OfType<TextBlock>()
                .Select(block => block.Text).ToList();

            Assert.Equal(new[] { "GÜNLÜK İŞ", "TANIMLAR", "SİSTEM" }, titles);
        });

    [Fact]
    public void TumMenuOgeleriOrtakStiliKullanir() =>
        UiThread.Run(() =>
        {
            var window = new MainWindow();
            UiThread.ApplyResources(window);

            var panel = (Panel)window.FindName("NavigationButtons")!;
            var expected = (Style)window.TryFindResource("NavItem")!;

            foreach (var button in panel.Children.OfType<Button>())
                Assert.Same(expected, button.Style);
        });
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~NavigationGroupingTests"`
Expected: FAIL — grup başlığı yok

- [ ] **Step 3: Menüyü grupla**

`MainWindow.xaml` içinde `<StackPanel x:Name="NavigationButtons" Grid.Row="2">` içeriğini değiştir. Her düğmeden elle yazılmış `Foreground`, `Background`, `BorderThickness`, `Padding`, `Margin`, `HorizontalContentAlignment`, `Cursor` özniteliklerini kaldır; yerine `Style="{StaticResource NavItem}"` koy. `Tag`, `Command`, `Content`, `Visibility`, `AutomationProperties.Name` korunur.

```xml
                <StackPanel x:Name="NavigationButtons" Grid.Row="2">
                    <TextBlock Text="GÜNLÜK İŞ" Style="{StaticResource NavGroupTitle}"/>
                    <Button Tag="dashboard" Style="{StaticResource NavItem}" Content="Panel"
                            Command="{Binding NavigateDashboardCommand}"
                            AutomationProperties.Name="Panel sayfasını aç"/>
                    <Button Tag="daily-tracking" Style="{StaticResource NavItem}" Content="Günlük Takip"
                            Command="{Binding NavigateDailyTrackingCommand}"
                            AutomationProperties.Name="Günlük Takip sayfasını aç"/>
                    <Button Tag="students" Style="{StaticResource NavItem}" Content="Öğrenciler"
                            Command="{Binding NavigateStudentsCommand}"
                            AutomationProperties.Name="Öğrenciler sayfasını aç"/>
                    <Button Tag="cash" Style="{StaticResource NavItem}" Content="Kasa"
                            Command="{Binding NavigateCashCommand}"
                            AutomationProperties.Name="Kasa sayfasını aç"
                            Visibility="{Binding CanNavigateCash, Converter={StaticResource BooleanToVisibilityConverter}}"/>

                    <TextBlock Text="TANIMLAR" Style="{StaticResource NavGroupTitle}"/>
                    <Button Tag="entitlements" Style="{StaticResource NavItem}" Content="Yemek Hakedişleri"
                            Command="{Binding NavigateEntitlementsCommand}"
                            AutomationProperties.Name="Yemek hakedişleri sayfasını aç"
                            Visibility="{Binding CanNavigateEntitlements, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                    <Button Tag="holiday-transfer" Style="{StaticResource NavItem}" Content="Takvim / Tatil"
                            Command="{Binding NavigateCalendarCommand}"
                            AutomationProperties.Name="Operasyon takvimini aç"
                            Visibility="{Binding CanNavigateCalendar, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                    <Button Tag="student-import" Style="{StaticResource NavItem}" Content="Sicil Aktar"
                            Command="{Binding NavigateStudentImportCommand}"
                            AutomationProperties.Name="Sicil aktarma ekranini ac"
                            Visibility="{Binding CanNavigateStudentImport, Converter={StaticResource BooleanToVisibilityConverter}}"/>

                    <TextBlock Text="SİSTEM" Style="{StaticResource NavGroupTitle}"/>
                    <Button Tag="devices" Style="{StaticResource NavItem}" Content="Cihazlar / Turnikeler"
                            Command="{Binding NavigateDevicesCommand}"
                            AutomationProperties.Name="Cihazlar ve turnikeler sayfasını aç"/>
                    <Button Tag="device-cards" Style="{StaticResource NavItem}" Content="Kart Yükleme Durumu"
                            Command="{Binding NavigateDeviceCardsCommand}"
                            AutomationProperties.Name="Kart yükleme durumu sayfasını aç"/>
                    <Button Tag="sms" Style="{StaticResource NavItem}" Content="SMS Merkezi"
                            Command="{Binding NavigateSmsCommand}"
                            AutomationProperties.Name="SMS merkezini aç"
                            Visibility="{Binding CanNavigateSms, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                    <Button Tag="reports" Style="{StaticResource NavItem}" Content="Raporlar"
                            Command="{Binding NavigateReportsCommand}"
                            AutomationProperties.Name="Rapor merkezini aç"
                            Visibility="{Binding CanNavigateReports, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                    <Button Tag="settings" Style="{StaticResource NavItem}" Content="Ayarlar"
                            Command="{Binding NavigateSettingsCommand}"
                            AutomationProperties.Name="Sistem ayarlarını aç"
                            Visibility="{Binding CanNavigateSettings, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                </StackPanel>
```

**Dikkat:** `MainWindow.xaml.cs` içinde `NavigationButtons` çocuklarını gezen kod varsa (seçili öğe işaretleme) `TextBlock` çocuklarını atlaması gerekir. `Children.OfType<Button>()` kullanıldığından emin ol.

- [ ] **Step 4: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~NavigationGroupingTests"`
Expected: PASS (2 test)

- [ ] **Step 5: Kabuk testlerini çalıştır**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~Desktop"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Yemekhane.Desktop/MainWindow.xaml src/Yemekhane.Desktop/MainWindow.xaml.cs tests/Yemekhane.UnitTests/Desktop/NavigationGroupingTests.cs
git commit -m "Menu uc gruba ayrildi, ogeler ortak stile bagli"
```

---

### Task 5: `Drawer` kontrolü

**Files:**
- Create: `src/Yemekhane.Desktop/Controls/Drawer.cs`
- Create: `src/Yemekhane.Desktop/Themes/Drawer.xaml`
- Modify: `src/Yemekhane.Desktop/App.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/DrawerTests.cs` (yeni)

**Interfaces:**
- Produces: `Drawer` — `ContentControl` türevi. Özellikler: `IsOpen` (`bool`), `Title` (`string`), `DrawerWidth` (`double`, varsayılan 400), `CloseCommand` (`ICommand`).

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/DrawerTests.cs`:

```csharp
using System.Windows;
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

    [Fact]
    public void EscTusuCekmeceyiKapatir() =>
        UiThread.Run(() =>
        {
            var drawer = new Drawer { IsOpen = true };
            UiThread.ApplyResources(drawer);

            drawer.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
            { RoutedEvent = UIElement.KeyDownEvent });

            Assert.False(drawer.IsOpen);
        });
}
```

**Not:** `TestPresentationSource` yardımcısı gerekiyorsa `UiThread.cs` yanına eklenir; alternatif olarak Esc testi `drawer.Close()` çağrısıyla sadeleştirilebilir. Uygulayan kişi mevcut `UiThread` yardımcılarını inceleyip uygun olanı seçsin.

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DrawerTests"`
Expected: FAIL — `Drawer` bulunamadı

- [ ] **Step 3: `Drawer` kontrolünü yaz**

`src/Yemekhane.Desktop/Controls/Drawer.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// Sagdan acilan panel.
///
/// Uc standart olcu vardir: dar (400) hizli bakis ve onay icin, genis (640)
/// form ve detay icin. Esc kapatir, odak acilista ilk alana gider ve
/// kapanista geldigi yere doner.
/// </summary>
public sealed class Drawer : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(Drawer),
            new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(Drawer),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DrawerWidthProperty =
        DependencyProperty.Register(nameof(DrawerWidth), typeof(double), typeof(Drawer),
            new PropertyMetadata(400d));

    private IInputElement? previousFocus;

    static Drawer() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Drawer),
            new FrameworkPropertyMetadata(typeof(Drawer)));

    public Drawer()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
        KeyDown += OnKeyDown;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double DrawerWidth
    {
        get => (double)GetValue(DrawerWidthProperty);
        set => SetValue(DrawerWidthProperty, value);
    }

    /// <summary>Cekmeceyi kapatir ve odagi geldigi yere dondurur.</summary>
    public void Close() => IsOpen = false;

    private static void OnIsOpenChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var drawer = (Drawer)source;
        var opened = (bool)args.NewValue;

        drawer.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;

        if (opened)
        {
            drawer.previousFocus = Keyboard.FocusedElement;
            drawer.Dispatcher.BeginInvoke(() => drawer.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.First)));
        }
        else if (drawer.previousFocus is not null)
        {
            Keyboard.Focus(drawer.previousFocus);
            drawer.previousFocus = null;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;

        Close();
        args.Handled = true;
    }
}
```

`src/Yemekhane.Desktop/Themes/Drawer.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:controls="clr-namespace:Yemekhane.Desktop.Controls">

    <Style TargetType="{x:Type controls:Drawer}">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type controls:Drawer}">
                    <Grid>
                        <!-- Karartma katmani: disina tiklayinca kapanir. -->
                        <Border x:Name="PART_Scrim" Background="#66101820"/>
                        <Border Background="{StaticResource SurfaceBrush}"
                                BorderBrush="{StaticResource BorderStrongBrush}"
                                BorderThickness="1,0,0,0"
                                Width="{TemplateBinding DrawerWidth}"
                                HorizontalAlignment="Right" Padding="20">
                            <DockPanel>
                                <Grid DockPanel.Dock="Top" Margin="0,0,0,14">
                                    <TextBlock Text="{TemplateBinding Title}"
                                               Style="{StaticResource SectionTitle}"
                                               VerticalAlignment="Center"/>
                                    <Button x:Name="PART_Close" Content="Kapat"
                                            Style="{StaticResource Action}"
                                            HorizontalAlignment="Right"
                                            AutomationProperties.Name="Çekmeceyi kapat"/>
                                </Grid>
                                <ScrollViewer VerticalScrollBarVisibility="Auto">
                                    <ContentPresenter/>
                                </ScrollViewer>
                            </DockPanel>
                        </Border>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

`PART_Close` ve `PART_Scrim` bağlantısı için `Drawer.cs`'e `OnApplyTemplate` ekle:

```csharp
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Close") is System.Windows.Controls.Button close)
            close.Click += (_, _) => Close();

        if (GetTemplateChild("PART_Scrim") is UIElement scrim)
            scrim.MouseLeftButtonDown += (_, _) => Close();
    }
```

- [ ] **Step 4: `App.xaml`'e sözlüğü ekle**

`MergedDictionaries` içine `DesignSystem.xaml`'den **sonra**:

```xml
                <ResourceDictionary Source="Themes/Drawer.xaml"/>
```

- [ ] **Step 5: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DrawerTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Yemekhane.Desktop/Controls/ src/Yemekhane.Desktop/Themes/Drawer.xaml src/Yemekhane.Desktop/App.xaml tests/Yemekhane.UnitTests/Desktop/DrawerTests.cs
git commit -m "Ortak Drawer kontrolu: Esc, odak yonetimi, uc standart olcu"
```

---

### Task 5b: Form alanı stilleri (spec 3.7)

**Files:**
- Modify: `src/Yemekhane.Desktop/Themes/DesignSystem.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/DesignSystemResourceTests.cs` (Task 1'de oluşturuldu)

**Interfaces:**
- Consumes: Task 1'in tema kaynakları.
- Produces: `RequiredLabel`, `FieldError` stil anahtarları.

- [ ] **Step 1: Testi genişlet**

`DesignSystemResourceTests.cs` içindeki `[Theory]` listesine iki satır ekle:

```csharp
    [InlineData("RequiredLabel")]
    [InlineData("FieldError")]
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DesignSystemResourceTests"`
Expected: FAIL — "'RequiredLabel' tema kaynagi tanimli degil."

- [ ] **Step 3: Stilleri ekle**

`DesignSystem.xaml` içinde `IdentityText` stilinin altına:

```xml
    <!-- Zorunlu alan: yildiz isareti etiketin parcasi degil, stilin parcasidir. -->
    <Style x:Key="RequiredLabel" TargetType="TextBlock" BasedOn="{StaticResource Label}">
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>

    <!-- Hata metni ALANIN ALTINDA gorunur, formun dibinde degil:
         kullanici hangi alanin sorunlu oldugunu gormeli. -->
    <Style x:Key="FieldError" TargetType="TextBlock">
        <Setter Property="FontSize" Value="{StaticResource FontXs}"/>
        <Setter Property="Foreground" Value="{StaticResource DangerBrush}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
        <Setter Property="Margin" Value="0,3,0,6"/>
    </Style>
```

- [ ] **Step 4: Testi çalıştır, geçtiğini doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~DesignSystemResourceTests"`
Expected: PASS (10 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Desktop/Themes/DesignSystem.xaml tests/Yemekhane.UnitTests/Desktop/DesignSystemResourceTests.cs
git commit -m "Form alani stilleri: zorunlu etiket ve alan alti hata metni"
```

**Not:** Bu stiller Task 7, 8, 9 ve 11'de formlar düzenlenirken kullanılır.
"Kaydet düğmesi formun altında sabit" gereksinimi (spec 3.7) her form
düzenlemesinde `DockPanel.Dock="Bottom"` ile karşılanır.

---

### Task 6: Aşama 1 doğrulaması

**Files:** yok (yalnızca doğrulama)

- [ ] **Step 1: Tam derleme**

Run: `dotnet build --nologo`
Expected: 0 Hata

- [ ] **Step 2: Tüm testler**

Run: `dotnet test --nologo`
Expected: Tüm testler geçer.

**Uyarı:** Hafızadaki nota göre `dotnet test` yeşil banner'ı tüm testlerin koştuğu anlamına gelmez — test sayısını önceki çalıştırmayla karşılaştır.

- [ ] **Step 3: Uygulamayı elle çalıştır**

Run: `dotnet run --project src/Yemekhane.Desktop`
Kontrol et: menü grupları görünüyor mu, seçili öğe turuncu şerit taşıyor mu, hiçbir ekranda renk bozulması var mı.

- [ ] **Step 4: Kullanıcıya göster ve onay al**

Aşama 2'ye geçmeden önce kullanıcı onayı beklenir.

---

## AŞAMA 2 — ÜÇ ANA EKRAN

> Aşama 1 onaylanmadan başlanmaz. Bu aşamanın görevleri Aşama 1'in
> `PageShell`, `Drawer` ve converter'larına dayanır.

### Task 7: Öğrenciler — liste + form yan yana

**Files:**
- Modify: `src/Yemekhane.Desktop/Views/StudentsView.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/StudentsLayoutTests.cs` (yeni)

**Interfaces:**
- Consumes: Task 2 converter'ları, Task 5 `Drawer`.
- Korunan binding'ler: `Students`, `SelectedStudent`, `Search`, `StudentNo`, `CardNumber`, `FirstName`, `LastName`, `IsActive`, `SearchCommand`, `NewStudentCommand`, `SaveStudentCommand`, `FormStudentNo`, `FormFirstName`, `FormLastName`, `FormNotes`, `CanWrite`, `IsCardWorkflowOpen`, `PageText`, `PreviousPageCommand`, `NextPageCommand`.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/StudentsLayoutTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci ekraninda liste ve formun AYNI ANDA gorunur oldugunu dogrular.
///
/// Once form cekmecede aciliyordu; cekmece acilinca liste kapaniyordu.
/// Eski uygulama bu isi daha hizli yapiyordu cunku ikisi yan yanaydi.
/// </summary>
[Collection("UI")]
public sealed class StudentsLayoutTests
{
    [Fact]
    public void ListeVeFormAyniAndaGorunur() =>
        UiThread.Run(() =>
        {
            var view = new StudentsView();
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var grid = (FrameworkElement)view.FindName("StudentsGrid")!;
            var form = (FrameworkElement)view.FindName("StudentFormPanel")!;

            Assert.True(grid.ActualWidth > 0, "Ogrenci listesi gorunur degil.");
            Assert.True(form.ActualWidth > 0, "Ogrenci formu gorunur degil.");
        });

    /// <summary>Kaldirilan alanlar formda bulunmamali.</summary>
    [Theory]
    [InlineData("FormNationalId")]
    [InlineData("FormAddress")]
    public void KaldirilanAlanFormdaYok(string bindingPath)
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.DoesNotContain(bindingPath, xaml);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~StudentsLayoutTests"`
Expected: FAIL — `StudentFormPanel` bulunamadı, `FormNationalId` hâlâ var

- [ ] **Step 3: Düzeni yeniden kur**

`StudentsView.xaml` ana `Grid`'ini iki kolona ayır: solda liste (`Width="*"`), sağda form (`Width="420"`).

- Sol kolon: arama/filtre kartı + `StudentsGrid` + sayfalama
- Sağ kolon: `x:Name="StudentFormPanel"` — No, Ad, Soyad, Sınıf, Şube, Kart No, Veli Tel, Not, Durum
- `IsQuickDetailOpen`, `IsDetailOpen`, `IsFormOpen` çekmeceleri kaldırılır
- `IsCardWorkflowOpen` modalı **korunur**
- `DURUM` sütunu rozete çevrilir:

```xml
<DataGridTemplateColumn Header="DURUM" Width="76">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <Border Background="{Binding IsActive, Converter={StaticResource StatusBrush}}"
                    Style="{StaticResource BadgeNeutral}">
                <TextBlock Text="{Binding IsActive, Converter={StaticResource StatusBadge}}"
                           FontSize="{StaticResource FontXs}"/>
            </Border>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

- `RowHeight="30"` kaldırılır (tema 34 veriyor)
- TC (`FormNationalId`) ve Adres (`FormAddress`) alanları silinir

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~StudentsLayoutTests|FullyQualifiedName~ViewLayoutTests|FullyQualifiedName~FieldWidthTests|FullyQualifiedName~BindingIntegrityTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Desktop/Views/StudentsView.xaml tests/Yemekhane.UnitTests/Desktop/StudentsLayoutTests.cs
git commit -m "Ogrenci ekrani: liste ve form yan yana, TC ve adres kaldirildi"
```

---

### Task 8: Kasa — liste + form ve kimlik düzeltmesi

**Files:**
- Modify: `src/Yemekhane.Desktop/Views/CashView.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/CashIdentityTests.cs` (yeni)

**Interfaces:**
- Consumes: Task 2 `StudentIdentityConverter`.
- Korunan binding'ler: `Transactions`, `SelectedTransaction`, `IncomeTypes`, `AmountText`, `AddCommand`, `OpenVoidCommand`, `VoidReason`, `VoidConfirmed`, `VoidCommand`, `DailyTotal`, `WeeklyTotal`, `MonthlyTotal`.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/CashIdentityTests.cs`:

```csharp
namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Kasa ekraninda yikici eylemin dogru yerde ve dogru stilde oldugunu dogrular.
///
/// Once "Secili Islemi Iptal Et" dugmesi sayfanin ORTASINDA, sayfalama
/// dugmelerinin yaninda ve notr stildeydi. Yikici bir eylem beklenmedik bir
/// konumda duruyordu.
/// </summary>
public sealed class CashIdentityTests
{
    private static string CashXaml() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "CashView.xaml"));

    [Fact]
    public void IptalDugmesiYikiciStilTasir()
    {
        var xaml = CashXaml();
        var index = xaml.IndexOf("OpenVoidCommand", StringComparison.Ordinal);

        Assert.True(index >= 0, "Iptal komutu bulunamadi.");

        // Dugme tanimi icinde Destructive stili gecmeli.
        var start = xaml.LastIndexOf("<Button", index, StringComparison.Ordinal);
        var end = xaml.IndexOf("/>", index, StringComparison.Ordinal);
        var button = xaml[start..end];

        Assert.Contains("Destructive", button);
    }

    [Fact]
    public void IptalDugmesiSayfaOrtasindaDegil()
    {
        var xaml = CashXaml();
        var index = xaml.IndexOf("OpenVoidCommand", StringComparison.Ordinal);
        var start = xaml.LastIndexOf("<StackPanel", index, StringComparison.Ordinal);
        var block = xaml[start..index];

        Assert.DoesNotContain("HorizontalAlignment=\"Center\"", block);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~CashIdentityTests"`
Expected: FAIL — düğme `Action` stili taşıyor ve ortada

- [ ] **Step 3: Kasa ekranını düzenle**

- "Seçili İşlemi İptal Et" düğmesini alt banttan çıkarıp ızgara üstündeki araç çubuğuna taşı, `Style="{StaticResource Destructive}"` uygula.
- "Düzenleme ve silme desteklenmez." metnini alt banda `Caption` stiliyle taşı.
- `IsAddOpen` ve `IsVoidOpen` çekmecelerini `Drawer` kontrolüne çevir (`DrawerWidth="400"`).
- Gelir ekleme çekmecesinde `LookupStudentText` yerine kimlik converter'ı kullan.
- Onay kutuları işaretlenmeden kaydet düğmesi pasif kalsın:

```xml
<Button Content="Onayla ve Kaydet" Command="{Binding AddCommand}"
        Style="{StaticResource Primary}" HorizontalAlignment="Left"
        IsEnabled="{Binding AddConfirmed}"/>
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~CashIdentityTests|FullyQualifiedName~ViewLayoutTests|FullyQualifiedName~BindingIntegrityTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Desktop/Views/CashView.xaml tests/Yemekhane.UnitTests/Desktop/CashIdentityTests.cs
git commit -m "Kasa: yikici eylem dogru yerde, ogrenci kimligi ayirt edici"
```

---

### Task 9: Hakedişler — çoklu seçim ve atama formu

**Files:**
- Modify: `src/Yemekhane.Desktop/Views/MealEntitlementsView.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/EntitlementSelectionTests.cs` (yeni)

**Interfaces:**
- Korunan binding'ler: `Items`, `MealTypes`, `GrantMeal`, `GrantStartsOn`, `GrantEndsOn`, `Quantity`, `IncludeSaturday`, `IncludeSunday`, `PreviewCommand`, `ApplyCommand`, `PreviewText`, `HasPreview`, `TotalQuantity`, `ConsumedQuantity`, `RemainingQuantity`.

- [ ] **Step 1: Testi yaz**

`tests/Yemekhane.UnitTests/Desktop/EntitlementSelectionTests.cs`:

```csharp
namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogun atamada ogrenci seciminin kullanilabilir oldugunu dogrular.
///
/// Once "Hizli Hakedis" cekmecesinde ogrenci secimi "kimlikleri virgulle
/// girin" seklindeydi -- 200 ogrenci icin pratikte imkansiz.
/// </summary>
public sealed class EntitlementSelectionTests
{
    private static string Xaml() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "MealEntitlementsView.xaml"));

    [Fact]
    public void ElleKimlikGirisiKaldirildi() =>
        Assert.DoesNotContain("ManualStudentIds", Xaml());

    /// <summary>Onizleme korunmali: 200 ogrenciye yanlis atamayi engelliyor.</summary>
    [Fact]
    public void EtkileriOnizleKorundu()
    {
        var xaml = Xaml();

        Assert.Contains("PreviewCommand", xaml);
        Assert.Contains("ApplyCommand", xaml);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~EntitlementSelectionTests"`
Expected: FAIL — `ManualStudentIds` hâlâ var

- [ ] **Step 3: Ekranı düzenle**

- Sol kolon: arama + sınıf filtresi + checkbox'lı öğrenci listesi + "Hepsini seç" + seçili sayısı
- Sağ kolon: öğün, adet, gün, başlangıç tarihi, Cmt/Paz kutuları, Önizle/Uygula
- `ManualStudentIds` metin kutusu kaldırılır
- `IsGrantOpen` çekmecesi `Drawer`'a çevrilir (`DrawerWidth="400"`)
- `IsCancelConfirmationOpen` modalı korunur, onay düğmesine `Destructive` stili uygulanır
- `RowHeight="29"` kaldırılır

**Çoklu seçim — dikkat.** Mevcut kanca zaten çalışıyor ve korunmalıdır:

```csharp
// MealEntitlementsView.xaml.cs — mevcut hali, DEGISTIRILMEZ
private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (DataContext is MealEntitlementsViewModel viewModel && sender is DataGrid grid)
        viewModel.SetSelection(grid.SelectedItems.Cast<MealEntitlementListItem>());
}
```

Bu kanca `sender`'ın **`DataGrid` olmasına bağlıdır**. Öğrenci listesini
`ListBox` veya `ItemsControl`'e çevirmek kancayı sessizce devre dışı
bırakır — `sender is DataGrid` false döner, `SetSelection` hiç çağrılmaz
ve seçim ViewModel'e ulaşmaz.

Bu yüzden liste **`DataGrid` olarak kalır**; çoklu seçim için
`SelectionMode="Extended"` (zaten var) korunur ve başına bir
`DataGridCheckBoxColumn` eklenir:

```xml
<DataGridTemplateColumn Width="40">
    <DataGridTemplateColumn.HeaderTemplate>
        <DataTemplate><TextBlock Text="SEÇ" FontSize="{StaticResource FontXs}"/></DataTemplate>
    </DataGridTemplateColumn.HeaderTemplate>
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <CheckBox IsChecked="{Binding IsSelected,
                          RelativeSource={RelativeSource AncestorType=DataGridRow}}"
                      HorizontalAlignment="Center"
                      AutomationProperties.Name="Satırı seç"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

`DataGridRow.IsSelected`'a bağlanmak, kutunun `DataGrid`'in kendi seçim
mekanizmasını sürmesini sağlar; böylece `OnSelectionChanged` tetiklenmeye
devam eder.

"Hepsini seç" ve seçili sayısı için `SetSelection`'ın çağrıldığı yol
korunduğundan ViewModel'e **yeni özellik eklenmez**. Seçili sayısı
`ItemsSource` üzerinden değil, `DataGrid.SelectedItems.Count`'tan
code-behind ile bir `TextBlock`'a yazılır.

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~EntitlementSelectionTests|FullyQualifiedName~ViewLayoutTests|FullyQualifiedName~BindingIntegrityTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Desktop/Views/MealEntitlementsView.xaml tests/Yemekhane.UnitTests/Desktop/EntitlementSelectionTests.cs
git commit -m "Ogun atama: cokli secim listesi, elle kimlik girisi kaldirildi"
```

---

### Task 10: Aşama 2 doğrulaması

- [ ] **Step 1: Tam derleme ve test**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: 0 Hata, tüm testler geçer

- [ ] **Step 2: Uygulamayı elle çalıştır**

Run: `dotnet run --project src/Yemekhane.Desktop`
Kontrol et: üç ana ekranda liste+form yan yana mı, durum sütunları rozet mi, çekmeceler Esc ile kapanıyor mu.

- [ ] **Step 3: Kullanıcıya göster ve onay al**

---

## AŞAMA 3 — KALAN EKRANLAR

### Task 11: `PageShell` kontrolü ve kalan 9 ekran

**Files:**
- Create: `src/Yemekhane.Desktop/Controls/PageShell.cs`
- Create: `src/Yemekhane.Desktop/Themes/PageShell.xaml`
- Modify: `Views/DailyTrackingView.xaml`, `CalendarView.xaml`, `SmsView.xaml`, `ReportsView.xaml`, `SettingsView.xaml`, `StudentImportView.xaml`, `BulkOperationWizardView.xaml`
- Test: `tests/Yemekhane.UnitTests/Desktop/PageShellTests.cs` (yeni)

**Interfaces:**
- Produces: `PageShell` — `ContentControl` türevi. Özellikler: `Title` (`string`), `Subtitle` (`string`), `Actions` (`object`), `Filters` (`object`), `FooterLeft` (`object`), `FooterRight` (`object`).

- [ ] **Step 1: Testi yaz**

```csharp
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ortak sayfa iskeletinin bolgelerini dogrular.
///
/// Once her view baslik, alt baslik, arac cubugu, yukleniyor gostergesi,
/// bos liste yazisi, hata satiri ve sayfalamayi ELLE kuruyordu. Sekiz parca,
/// on iki ekran, hepsi biraz farkli.
/// </summary>
[Collection("UI")]
public sealed class PageShellTests
{
    [Fact]
    public void BaslikVeAltBaslikTasir() =>
        UiThread.Run(() =>
        {
            var shell = new PageShell { Title = "Raporlar", Subtitle = "Aylik ozet" };

            Assert.Equal("Raporlar", shell.Title);
            Assert.Equal("Aylik ozet", shell.Subtitle);
        });
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü doğrula**

Run: `dotnet test tests/Yemekhane.UnitTests --filter "FullyQualifiedName~PageShellTests"`
Expected: FAIL — `PageShell` bulunamadı

- [ ] **Step 3: `PageShell` kontrolünü yaz**

`Drawer.cs` desenini izle: `ContentControl` türevi, `DependencyProperty.Register` ile `Title`, `Subtitle`, `Actions`, `Filters`, `FooterLeft`, `FooterRight` özellikleri. Şablonda `DockPanel` ile dört bölge: üstte başlık+eylemler, altında filtreler, ortada `ContentPresenter`, altta hata (sol) + sayfalama (sağ).

- [ ] **Step 4: `App.xaml`'e sözlüğü ekle**

```xml
                <ResourceDictionary Source="Themes/PageShell.xaml"/>
```

- [ ] **Step 5: 7 ekranı `PageShell`'e taşı**

Her ekran için başlık bloğunu, filtre kartını ve alt bandı `PageShell` bölgelerine taşı. `RowHeight` ve renk tekrarlarını sil. Çekmeceleri `Drawer`'a çevir.

`SettingsView` için ek: Yedekleme sekmesi ikiye ayrılır (üstte zamanlama, altta elle işlemler); "Geri Yükle" düğmesinin `Background="#A4403A"` elle boyaması kaldırılıp `Style="{StaticResource Destructive}"` uygulanır.

- [ ] **Step 6: Testleri çalıştır**

Run: `dotnet test --nologo`
Expected: Tüm testler geçer

- [ ] **Step 7: Commit**

```bash
git add src/Yemekhane.Desktop/ tests/Yemekhane.UnitTests/Desktop/PageShellTests.cs
git commit -m "Ortak PageShell iskeleti kalan ekranlara uygulandi"
```

---

### Task 12: Panelde son yedek göstergesi — ENGELLENDİ

**Durum:** Bu görev uygulanamaz. Plan yazılırken doğrulandı.

`DashboardViewModel.cs` ve `DashboardClients.cs` içinde yedekleme durumuna
dair **hiçbir binding yok**. Göstergeyi eklemek için `DashboardViewModel`'e
`LastBackupAt` gibi bir özellik ve `DashboardClients`'a bir API çağrısı
eklemek gerekir.

Bu, Global Constraints'teki **"ViewModel dosyaları değiştirilmez"**
kuralına aykırıdır.

**Yapılacak:** Bu görev atlanır ve kullanıcıya sorulur:

> Panelde "Son yedek: 3 gün önce" göstergesi için `DashboardViewModel`'e
> bir özellik eklenmesi gerekiyor. ViewModel'lere dokunmama kararını
> gevşetmemi ister misiniz, yoksa gösterge olmadan devam edeyim mi?

Kullanıcı onay verirse ayrı bir görev olarak planlanır. Onay vermezse
spec'in 3.10 bölümündeki "Panelde tek satır" maddesi kapsam dışı kalır;
Yedekleme sekmesinin içinin düzenlenmesi (Task 11, Step 5) yine yapılır.

---

### Task 13: Komut paleti (Ctrl+K)

**Files:**
- Modify: `src/Yemekhane.Desktop/MainWindow.xaml`
- Modify: `src/Yemekhane.Desktop/MainWindow.xaml.cs`
- Test: `tests/Yemekhane.UnitTests/Desktop/CommandPaletteTests.cs` (yeni)

**Not:** Bu görev `GlobalSearchViewModel` (139 satır) üzerine kurulur. Mevcut arama altyapısı kullanılır; yeni ViewModel yazılmaz.

- [ ] **Step 1: Mevcut arama altyapısını incele**

Run: `cat src/Yemekhane.Desktop/ViewModels/GlobalSearchViewModel.cs`

Palet bu ViewModel'in arama sonuçlarını gösterir. Komut listesi (gezinme komutları) `MainWindow.xaml.cs` içinde statik olarak tanımlanır — ViewModel'e dokunulmaz.

- [ ] **Step 2: Testi yaz, çalıştır, düştüğünü doğrula, uygula, geçtiğini doğrula, commit et**

Palet açılışı `Ctrl+K` `KeyBinding` ile `ShortcutCommandRouter` üzerinden bağlanır. Esc kapatır.

---

### Task 14: Nihai doğrulama

- [ ] **Step 1: Tam derleme ve test**

Run: `dotnet build --nologo && dotnet test --nologo`

- [ ] **Step 2: Test sayısını doğrula**

Önceki çalıştırmalarla karşılaştır — yeşil banner tüm testlerin koştuğu anlamına gelmez.

- [ ] **Step 3: Spec ölçütlerini kontrol et**

- [ ] Tüm ekranlar tek tema kaynağını kullanır; yerel renk/stil tanımı kalmaz
- [ ] Aynı işlev her ekranda aynı yerde ve aynı görünümde
- [ ] Yıkıcı eylemler `Destructive` stili taşır ve beklenen yerde durur
- [ ] Hiçbir onay ekranı öğrenciyi yalnız adıyla göstermez
- [ ] Çekmeceler Esc ile kapanır, aynı anda yalnız biri açıktır
- [ ] Mevcut testler geçmeye devam eder

- [ ] **Step 4: Uygulamayı elle çalıştır ve kullanıcıya göster**
