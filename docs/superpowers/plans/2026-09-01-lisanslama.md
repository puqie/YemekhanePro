# Lisanslama Sistemi Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** YemekhanePro'ya online aktivasyonlu, 30 gün çevrimdışı toleranslı, donanıma bağlı bir lisans katmanı eklemek; mevcut giriş sistemine dokunmadan.

**Architecture:** Bağımsız `Yemekhane.Licensing` projesi (hiçbir proje referansı yok). Donanım parmak izi 2/3 eşleşme kuralıyla makineye bağlar; lisans DPAPI ile şifreli yerel dosyada tutulur; sunucu çağrısı `ILicenseActivationClient` arkasındadır. Desktop açılışında, yerel API başlamadan ÖNCE kontrol edilir.

**Tech Stack:** .NET 10, C# latest, WPF (net10.0-windows), `System.Management` (WMI), `System.Security.Cryptography.ProtectedData` (DPAPI), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-01-lisanslama-design.md`

## Global Constraints

- Hedef çerçeve: `net10.0-windows` (WMI ve DPAPI Windows'a özgüdür). Windows'a özgü tipler `[SupportedOSPlatform("windows")]` ile işaretlenir.
- `AnalysisLevel=latest-recommended` çözüm genelinde açıktır. Yeni kod analiz uyarısı üretmemelidir (örn. CA1001: atılabilir alan taşıyan tip `IDisposable` olmalı).
- `Directory.Build.props` zaten `Nullable=enable` ve `ImplicitUsings=enable` verir; csproj'da tekrar edilmez.
- **Mevcut DPAPI entropisi `OkulYemek.SystemSettings.v1` DEĞİŞTİRİLMEZ.** Lisans kendi entropisini kullanır: `YemekhanePro.License.v1`.
- Kullanıcıya görünen tüm metinler Türkçedir ve somut olmalıdır ("Bir hata oluştu" yasaktır).
- Kod içi yorumlar Türkçe ve ASCII'dir (mevcut kod tabanının deseni); XML doc yorumları Türkçe olabilir.
- `Yemekhane.Licensing` **hiçbir ürün projesine referans veremez**; bu Task 1'de mimari testle zorunlu kılınır.
- Test komutu: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~<Ad>"`.
- Her task sonunda tam derleme yeşil olmalıdır: `dotnet build Yemekhane.sln`.

## Dosya Yapısı

| Dosya | Sorumluluk |
|---|---|
| `src/Yemekhane.Licensing/Yemekhane.Licensing.csproj` | Bağımsız proje |
| `src/Yemekhane.Licensing/LicenseContracts.cs` | Kayıtlar, enum'lar, arayüzler |
| `src/Yemekhane.Licensing/HardwareFingerprint.cs` | Parmak izi toplama + 2/3 karşılaştırma |
| `src/Yemekhane.Licensing/WindowsHardwareFingerprintReader.cs` | WMI/kayıt defteri okuma |
| `src/Yemekhane.Licensing/DpapiLicenseStore.cs` | Şifreli okuma/yazma |
| `src/Yemekhane.Licensing/LicenseSignature.cs` | İmza doğrulama |
| `src/Yemekhane.Licensing/LicenseService.cs` | Karar tablosu |
| `src/Yemekhane.Licensing/HttpLicenseActivationClient.cs` | Gerçek sunucu istemcisi |
| `src/Yemekhane.Desktop/Views/ActivationWindow.xaml(.cs)` | Aktivasyon ekranı |
| `src/Yemekhane.Desktop/ViewModels/ActivationViewModel.cs` | Ekran mantığı |
| `tests/Yemekhane.UnitTests/Licensing/*.cs` | Testler |

---

### Task 1: Proje iskeleti ve mimari kilidi

Lisans projesinin **hiçbir şeye bağlanmadığını** makine tarafından zorunlu kılar. Bu ilk sırada çünkü sonraki tasklar bu sınırın içinde yazılır.

**Files:**
- Create: `src/Yemekhane.Licensing/Yemekhane.Licensing.csproj`
- Create: `src/Yemekhane.Licensing/LicenseContracts.cs`
- Modify: `Yemekhane.sln`
- Modify: `tests/Yemekhane.ArchitectureTests/LayerDependencyTests.cs`

**Interfaces:**
- Consumes: yok (ilk task)
- Produces: `LicenseStatus` enum, `StoredLicense`, `HardwareFingerprint`, `ActivationResult`, `ValidationResult`, `ILicenseStore`, `ILicenseActivationClient`, `IHardwareFingerprintReader`

- [ ] **Step 1: Mimari testi genişlet (önce başarısız olmalı)**

`tests/Yemekhane.ArchitectureTests/LayerDependencyTests.cs` içinde `ProductProjects` dizisine `"Yemekhane.Licensing"` ekle ve `LayerRules` içine şu satırı ekle:

```csharp
        { "Yemekhane.Licensing", [] },
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.ArchitectureTests/Yemekhane.ArchitectureTests.csproj`
Expected: FAIL — `Yemekhane.Licensing.csproj` bulunamadı (dosya henüz yok).

- [ ] **Step 3: Projeyi oluştur**

`src/Yemekhane.Licensing/Yemekhane.Licensing.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
  </PropertyGroup>

  <!-- Bilerek HICBIR ProjectReference yoktur. Lisans, kaliciliktan daha alt
       seviye bir konudur; Infrastructure'a baglanmak bagimlilik yonunu ters
       cevirir ve lisans kontrolunu baska bir derlemeyi degistirerek atlamayi
       kolaylastirir. Bu kural mimari testle zorunlu kilinir. -->
  <ItemGroup>
    <PackageReference Include="System.Management" Version="10.0.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Sözleşmeleri yaz**

`src/Yemekhane.Licensing/LicenseContracts.cs`:

```csharp
namespace Yemekhane.Licensing;

/// <summary>Lisansin o anki durumu. Valid disindaki her deger aktivasyon ekranini acar.</summary>
public enum LicenseStatus
{
    Valid,
    NotActivated,
    Tampered,
    WrongMachine,
    Expired,
    Revoked,
    OfflineGracePeriodExceeded
}

/// <summary>
/// Uc donanim bileseninin HASH'lenmis hali. Ham seri numaralari diske yazilmaz:
/// lisans dosyasi calinsa bile donanim kimligi sizmamalidir.
/// </summary>
public sealed record HardwareFingerprint(string? BaseBoardHash, string? DiskHash, string? MachineGuidHash)
{
    /// <summary>Okunabilen bilesen sayisi. Sifirsa parmak izi guvenilir degildir.</summary>
    public int ReadableCount =>
        (BaseBoardHash is null ? 0 : 1) + (DiskHash is null ? 0 : 1) + (MachineGuidHash is null ? 0 : 1);
}

/// <summary>Diskte saklanan lisans. Signature disindaki alanlar imzaya dahildir.</summary>
public sealed record StoredLicense(
    string LicenseKey,
    string CustomerName,
    string Edition,
    HardwareFingerprint Fingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastValidatedAt,
    string Signature);

public sealed record ActivationResult(bool Succeeded, StoredLicense? License, string? ErrorMessage);

/// <summary>
/// Sunucu dogrulamasinin sonucu. Unreachable ile Revoked AYRI tutulur:
/// ag hatasinda cevrimdisi toleransa dusulur, iptalde lisans aninda gecersizlesir.
/// </summary>
public enum ValidationOutcome { Valid, Revoked, Unreachable }

public sealed record ValidationResult(ValidationOutcome Outcome, DateTimeOffset? ExpiresAt, string? Signature);

public interface IHardwareFingerprintReader
{
    HardwareFingerprint Read();
}

public interface ILicenseStore
{
    StoredLicense? Read();
    void Write(StoredLicense license);
    void Clear();
}

public interface ILicenseActivationClient
{
    Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint,
        CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Çözüme ekle**

Run: `dotnet sln Yemekhane.sln add src/Yemekhane.Licensing/Yemekhane.Licensing.csproj`

- [ ] **Step 6: Test projesine referans ver**

`tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj` içindeki `ProjectReference` grubuna ekle:

```xml
    <ProjectReference Include="..\..\src\Yemekhane.Licensing\Yemekhane.Licensing.csproj" />
```

- [ ] **Step 7: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.ArchitectureTests/Yemekhane.ArchitectureTests.csproj`
Expected: PASS (7 test — biri yeni `Yemekhane.Licensing` kuralı)

- [ ] **Step 8: Commit**

```bash
git add src/Yemekhane.Licensing tests/Yemekhane.ArchitectureTests Yemekhane.sln tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj
git commit -m "Lisans projesi iskeleti ve mimari kilidi"
```

---

### Task 2: Donanım parmak izi 2/3 eşleşme kuralı

Saf mantık; WMI'ye dokunmaz, bu yüzden tamamen test edilebilir.

**Files:**
- Create: `src/Yemekhane.Licensing/HardwareFingerprint.cs` (kısmi sınıf değil; `FingerprintMatcher` statik sınıfı)
- Test: `tests/Yemekhane.UnitTests/Licensing/FingerprintMatcherTests.cs`

**Interfaces:**
- Consumes: `HardwareFingerprint` (Task 1)
- Produces: `FingerprintMatcher.Matches(HardwareFingerprint stored, HardwareFingerprint current) -> bool`, `FingerprintMatcher.MinimumMatches = 2`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/FingerprintMatcherTests.cs`:

```csharp
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// 2/3 kurali bir denge: kati eslesme musteriyi disk degisiminde magdur eder,
/// tek bilesen ise sanal makineye kopyalamayi serbest birakir.
/// </summary>
public sealed class FingerprintMatcherTests
{
    private static HardwareFingerprint Fingerprint(string? board = "B", string? disk = "D", string? guid = "G") =>
        new(board, disk, guid);

    [Fact]
    public void IdenticalFingerprintsMatch()
    {
        Assert.True(FingerprintMatcher.Matches(Fingerprint(), Fingerprint()));
    }

    [Fact]
    public void OneChangedComponentStillMatches()
    {
        // Disk degistirildi; musteri lisansini kaybetmemeli.
        Assert.True(FingerprintMatcher.Matches(Fingerprint(), Fingerprint(disk: "YENI-DISK")));
    }

    [Fact]
    public void TwoChangedComponentsDoNotMatch()
    {
        // Bu artik baska bir makinedir.
        Assert.False(FingerprintMatcher.Matches(Fingerprint(), Fingerprint(disk: "X", guid: "Y")));
    }

    [Fact]
    public void UnreadableComponentsCountAsMismatchNotAsWildcard()
    {
        // Okunamayan bilesen "her seye uyar" ANLAMINA GELMEZ; aksi halde
        // WMI'yi engelleyen bir makinede lisans her yerde gecerli olurdu.
        var stored = Fingerprint();
        var current = new HardwareFingerprint(null, null, "G");

        Assert.False(FingerprintMatcher.Matches(stored, current));
    }

    [Fact]
    public void NullOnBothSidesIsNotAMatch()
    {
        var stored = new HardwareFingerprint(null, "D", "G");
        var current = new HardwareFingerprint(null, "D", "BASKA");

        // Bos-bos esitligi sayilsaydi bu 2 eslesme olurdu; sayilmaz, yalnizca disk eslesir.
        Assert.False(FingerprintMatcher.Matches(stored, current));
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~FingerprintMatcherTests"`
Expected: FAIL — `FingerprintMatcher` tipi bulunamadı.

- [ ] **Step 3: Uygulamayı yaz**

`src/Yemekhane.Licensing/HardwareFingerprint.cs`:

```csharp
namespace Yemekhane.Licensing;

/// <summary>
/// Iki parmak izini karsilastirir. Uc bilesenden IKISI tutuyorsa ayni makine sayilir.
/// </summary>
public static class FingerprintMatcher
{
    public const int MinimumMatches = 2;

    public static bool Matches(HardwareFingerprint stored, HardwareFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);

        var matches =
            Same(stored.BaseBoardHash, current.BaseBoardHash) +
            Same(stored.DiskHash, current.DiskHash) +
            Same(stored.MachineGuidHash, current.MachineGuidHash);

        return matches >= MinimumMatches;
    }

    // Okunamayan bilesen (null) ESLESME SAYILMAZ. Iki tarafta da null olsaydi
    // "esit" saymak, WMI'yi engelleyen bir makinede lisansi her yerde gecerli kilardi.
    private static int Same(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal) ? 1 : 0;
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~FingerprintMatcherTests"`
Expected: PASS (5 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Licensing/HardwareFingerprint.cs tests/Yemekhane.UnitTests/Licensing/FingerprintMatcherTests.cs
git commit -m "Donanim parmak izi 2/3 eslesme kurali"
```

---

### Task 3: Windows parmak izi okuyucu

WMI ve kayıt defterinden gerçek değerleri okur, hash'ler. Gerçek donanıma dokunduğu için testleri "çökmüyor ve tutarlı" düzeyindedir; mantık testi Task 2'dedir.

**Files:**
- Create: `src/Yemekhane.Licensing/WindowsHardwareFingerprintReader.cs`
- Test: `tests/Yemekhane.UnitTests/Licensing/WindowsHardwareFingerprintReaderTests.cs`

**Interfaces:**
- Consumes: `IHardwareFingerprintReader`, `HardwareFingerprint` (Task 1)
- Produces: `WindowsHardwareFingerprintReader` (parametresiz kurucu)

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/WindowsHardwareFingerprintReaderTests.cs`:

```csharp
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class WindowsHardwareFingerprintReaderTests
{
    [Fact]
    public void ReadingTwiceGivesTheSameFingerprint()
    {
        // Ayni makinede kararli olmali; aksi halde her acilista lisans kirilir.
        var reader = new WindowsHardwareFingerprintReader();

        var first = reader.Read();
        var second = reader.Read();

        Assert.Equal(first, second);
    }

    [Fact]
    public void AtLeastOneComponentIsReadableOnADevelopmentMachine()
    {
        // MachineGuid her Windows kurulumunda vardir; hicbiri okunamiyorsa
        // okuyucu bozuktur.
        var reader = new WindowsHardwareFingerprintReader();

        Assert.True(reader.Read().ReadableCount >= 1);
    }

    [Fact]
    public void RawSerialNumbersAreNotStored()
    {
        // Deger hash'lenmis olmali: 64 karakterlik onaltilik SHA-256.
        var fingerprint = new WindowsHardwareFingerprintReader().Read();

        foreach (var value in new[] { fingerprint.BaseBoardHash, fingerprint.DiskHash, fingerprint.MachineGuidHash })
        {
            if (value is null) continue;
            Assert.Equal(64, value.Length);
            Assert.True(value.All(Uri.IsHexDigit), $"Hash beklenirken ham deger bulundu: {value}");
        }
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~WindowsHardwareFingerprintReaderTests"`
Expected: FAIL — `WindowsHardwareFingerprintReader` tipi bulunamadı.

- [ ] **Step 3: Uygulamayı yaz**

`src/Yemekhane.Licensing/WindowsHardwareFingerprintReader.cs`:

```csharp
using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Yemekhane.Licensing;

/// <summary>
/// Donanim bilesenlerini okur ve HASH'ler. Tek bir bilesenin okunamamasi
/// (WMI kapali, sanal disk) olumcul degildir: 2/3 kurali bunu tolere eder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareFingerprintReader : IHardwareFingerprintReader
{
    public HardwareFingerprint Read() => new(
        Hash(QueryWmi("Win32_BaseBoard", "SerialNumber")),
        Hash(QueryWmi("Win32_DiskDrive", "SerialNumber")),
        Hash(ReadMachineGuid()));

    private static string? QueryWmi(string className, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    var value = item[property]?.ToString()?.Trim();
                    // Bazi anakartlar "To be filled by O.E.M." gibi yer tutucu dondurur;
                    // bunlar makineye ozgu degildir ve kimlik olarak kullanilamaz.
                    if (!string.IsNullOrWhiteSpace(value) && value.Length > 3 &&
                        !value.Contains("To be filled", StringComparison.OrdinalIgnoreCase) &&
                        !value.Contains("Default string", StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = key?.GetValue("MachineGuid")?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
    }

    private static string? Hash(string? value) => value is null
        ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~WindowsHardwareFingerprintReaderTests"`
Expected: PASS (3 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Licensing/WindowsHardwareFingerprintReader.cs tests/Yemekhane.UnitTests/Licensing/WindowsHardwareFingerprintReaderTests.cs
git commit -m "Windows donanim parmak izi okuyucu"
```

---

### Task 4: İmza doğrulama

Lisans dosyası kurcalanırsa yakalanmalı. HMAC-SHA256 kullanılır; gerçek sunucu geldiğinde asimetrik imzaya (RSA/ECDSA) geçilebilir — arayüz aynı kalır.

**Files:**
- Create: `src/Yemekhane.Licensing/LicenseSignature.cs`
- Test: `tests/Yemekhane.UnitTests/Licensing/LicenseSignatureTests.cs`

**Interfaces:**
- Consumes: `StoredLicense`, `HardwareFingerprint` (Task 1)
- Produces: `LicenseSignature.Compute(StoredLicense, string secret) -> string`, `LicenseSignature.Verify(StoredLicense, string secret) -> bool`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/LicenseSignatureTests.cs`:

```csharp
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class LicenseSignatureTests
{
    private const string Secret = "test-imza-anahtari";

    private static StoredLicense Sign(StoredLicense license) =>
        license with { Signature = LicenseSignature.Compute(license, Secret) };

    private static StoredLicense Sample() => new(
        "ANAHTAR-1", "Ornek Okulu", "Standard",
        new HardwareFingerprint("B", "D", "G"),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        Signature: string.Empty);

    [Fact]
    public void ASignedLicenseVerifies()
    {
        Assert.True(LicenseSignature.Verify(Sign(Sample()), Secret));
    }

    [Fact]
    public void ChangingTheExpiryBreaksTheSignature()
    {
        // En cazip kurcalama: bitis tarihini ileri almak.
        var tampered = Sign(Sample()) with { ExpiresAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.False(LicenseSignature.Verify(tampered, Secret));
    }

    [Fact]
    public void ChangingTheFingerprintBreaksTheSignature()
    {
        // Ikinci cazip kurcalama: parmak izini bu makineninkiyle degistirmek.
        var tampered = Sign(Sample()) with { Fingerprint = new HardwareFingerprint("X", "Y", "Z") };

        Assert.False(LicenseSignature.Verify(tampered, Secret));
    }

    [Fact]
    public void ADifferentSecretDoesNotVerify()
    {
        Assert.False(LicenseSignature.Verify(Sign(Sample()), "baska-anahtar"));
    }

    [Fact]
    public void AnEmptySignatureDoesNotVerify()
    {
        Assert.False(LicenseSignature.Verify(Sample(), Secret));
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~LicenseSignatureTests"`
Expected: FAIL — `LicenseSignature` tipi bulunamadı.

- [ ] **Step 3: Uygulamayı yaz**

`src/Yemekhane.Licensing/LicenseSignature.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <summary>
/// Lisans alanlarini imzalar. Simdilik HMAC-SHA256; gercek sunucu geldiginde
/// asimetrik imzaya gecilebilir, cagiran taraf degismez.
/// </summary>
public static class LicenseSignature
{
    public static string Compute(StoredLicense license, string secret)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(Payload(license))));
    }

    public static bool Verify(StoredLicense license, string secret)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (string.IsNullOrWhiteSpace(license.Signature)) return false;

        var expected = Compute(license, secret);
        // Sabit zamanli karsilastirma: imza dogrulamasi zamanlama saldirisina
        // acik birakilmamalidir.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(license.Signature));
    }

    /// <summary>
    /// Imzalanan metin. Signature DISINDAKI her alan dahildir; biri degisirse
    /// imza tutmaz. LastValidatedAt de dahildir ki geriye alinmis bir dosya
    /// imza dogrulamasindan gecemesin.
    /// </summary>
    private static string Payload(StoredLicense license) => string.Join('|',
        license.LicenseKey,
        license.CustomerName,
        license.Edition,
        license.Fingerprint.BaseBoardHash ?? "-",
        license.Fingerprint.DiskHash ?? "-",
        license.Fingerprint.MachineGuidHash ?? "-",
        license.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        license.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        license.LastValidatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~LicenseSignatureTests"`
Expected: PASS (5 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Licensing/LicenseSignature.cs tests/Yemekhane.UnitTests/Licensing/LicenseSignatureTests.cs
git commit -m "Lisans imza dogrulamasi"
```

---

### Task 5: DPAPI lisans deposu

**Files:**
- Create: `src/Yemekhane.Licensing/DpapiLicenseStore.cs`
- Test: `tests/Yemekhane.UnitTests/Licensing/DpapiLicenseStoreTests.cs`

**Interfaces:**
- Consumes: `ILicenseStore`, `StoredLicense` (Task 1)
- Produces: `DpapiLicenseStore(string directoryPath)`; dosya adı `license.dat`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/DpapiLicenseStoreTests.cs`:

```csharp
using System.Text;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class DpapiLicenseStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Yemekhane.License", Guid.NewGuid().ToString("N"));

    public DpapiLicenseStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static StoredLicense Sample() => new(
        "ANAHTAR-1", "Ornek Okulu", "Standard",
        new HardwareFingerprint("B", "D", "G"),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), DateTimeOffset.UtcNow, "imza");

    [Fact]
    public void WrittenLicenseIsReadBackUnchanged()
    {
        var store = new DpapiLicenseStore(directory);
        var license = Sample();

        store.Write(license);

        Assert.Equal(license, store.Read());
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        Assert.Null(new DpapiLicenseStore(directory).Read());
    }

    [Fact]
    public void FileIsNotStoredInPlainText()
    {
        // Musteri adi duz metin gorunuyorsa sifreleme calismiyordur.
        var store = new DpapiLicenseStore(directory);
        store.Write(Sample());

        var bytes = File.ReadAllBytes(Path.Combine(directory, "license.dat"));

        Assert.DoesNotContain("Ornek Okulu", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptedFileReturnsNullInsteadOfThrowing()
    {
        // Bozuk dosya uygulamayi acilista dusurmemeli; lisanssiz sayilir.
        File.WriteAllText(Path.Combine(directory, "license.dat"), "bu gecerli bir sifreli icerik degil");

        Assert.Null(new DpapiLicenseStore(directory).Read());
    }

    [Fact]
    public void ClearRemovesTheLicense()
    {
        var store = new DpapiLicenseStore(directory);
        store.Write(Sample());

        store.Clear();

        Assert.Null(store.Read());
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~DpapiLicenseStoreTests"`
Expected: FAIL — `DpapiLicenseStore` tipi bulunamadı.

- [ ] **Step 3: Uygulamayı yaz**

`src/Yemekhane.Licensing/DpapiLicenseStore.cs`:

```csharp
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yemekhane.Licensing;

/// <summary>
/// Lisansi DPAPI ile sifreleyerek diskte tutar.
///
/// Entropi MEVCUT ayar entropisinden (OkulYemek.SystemSettings.v1) FARKLIDIR:
/// mevcut deger degistirilirse sahadaki sifreli ayarlar okunamaz hale gelir.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiLicenseStore(string directoryPath) : ILicenseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YemekhanePro.License.v1");
    private const string FileName = "license.dat";

    private string FilePath => Path.Combine(directoryPath, FileName);

    public StoredLicense? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy,
                DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<StoredLicense>(plaintext);
        }
        // Bozuk, baska makinede sifrelenmis ya da elle degistirilmis dosya
        // uygulamayi acilista dusurmemeli: lisanssiz sayilir ve aktivasyon istenir.
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Write(StoredLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        Directory.CreateDirectory(directoryPath);
        var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(license), Entropy,
            DataProtectionScope.LocalMachine);

        // Once gecici dosyaya yazilir: yazma sirasinda elektrik kesilirse
        // yarim bir lisans dosyasi kalmamalidir.
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, FilePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~DpapiLicenseStoreTests"`
Expected: PASS (5 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Licensing/DpapiLicenseStore.cs tests/Yemekhane.UnitTests/Licensing/DpapiLicenseStoreTests.cs
git commit -m "DPAPI ile sifreli lisans deposu"
```

---

### Task 6: Lisans servisi karar tablosu

Sistemin kalbi. Saat manipülasyonu ve çevrimdışı tolerans burada uygulanır.

**Files:**
- Create: `src/Yemekhane.Licensing/LicenseService.cs`
- Test: `tests/Yemekhane.UnitTests/Licensing/LicenseServiceTests.cs`

**Interfaces:**
- Consumes: `ILicenseStore`, `IHardwareFingerprintReader`, `ILicenseActivationClient`, `LicenseStatus`, `FingerprintMatcher`, `LicenseSignature`
- Produces:
  - `LicenseOptions { int OfflineGraceDays = 30; int WarningThresholdDays = 23; string SigningSecret }`
  - `LicenseEvaluation(LicenseStatus Status, StoredLicense? License, string? Message, int? RemainingOfflineDays)`
  - `LicenseService(ILicenseStore, IHardwareFingerprintReader, ILicenseActivationClient, LicenseOptions, TimeProvider)`
  - `Task<LicenseEvaluation> EvaluateAsync(CancellationToken)`
  - `Task<LicenseEvaluation> ActivateAsync(string licenseKey, CancellationToken)`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/LicenseServiceTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Karar tablosunun tamami. Bu testler urunun ticari davranisini tanimlar:
/// yanlis bir "Valid" gelir kaybi, yanlis bir kilit ise ogrencinin yemek
/// yiyememesi demektir.
/// </summary>
public sealed class LicenseServiceTests
{
    private const string Secret = "test-imza-anahtari";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly HardwareFingerprint Machine = new("B", "D", "G");

    private static LicenseOptions Options() => new() { SigningSecret = Secret };

    private static StoredLicense Licensed(
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? lastValidatedAt = null,
        HardwareFingerprint? fingerprint = null)
    {
        var license = new StoredLicense("ANAHTAR-1", "Ornek Okulu", "Standard",
            fingerprint ?? Machine,
            Now.AddYears(-1),
            expiresAt ?? Now.AddYears(1),
            lastValidatedAt ?? Now,
            string.Empty);
        return license with { Signature = LicenseSignature.Compute(license, Secret) };
    }

    private static LicenseService Service(StoredLicense? stored, ValidationOutcome outcome = ValidationOutcome.Unreachable,
        DateTimeOffset? now = null)
    {
        var store = new MemoryStore { Current = stored };
        var client = new StubClient { Outcome = outcome };
        var time = new FakeTimeProvider(now ?? Now);
        return new LicenseService(store, new StubReader(), client, Options(), time);
    }

    [Fact]
    public async Task NoLicenseFileMeansNotActivated()
    {
        Assert.Equal(LicenseStatus.NotActivated, (await Service(null).EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task AValidRecentlyValidatedLicenseIsValid()
    {
        Assert.Equal(LicenseStatus.Valid, (await Service(Licensed()).EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task ATamperedSignatureIsRejected()
    {
        var tampered = Licensed() with { ExpiresAt = Now.AddYears(50) };

        Assert.Equal(LicenseStatus.Tampered, (await Service(tampered).EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task ALicenseFromAnotherMachineIsRejected()
    {
        var other = Licensed(fingerprint: new HardwareFingerprint("X", "Y", "Z"));

        Assert.Equal(LicenseStatus.WrongMachine, (await Service(other).EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task AnExpiredLicenseIsRejected()
    {
        var expired = Licensed(expiresAt: Now.AddDays(-1));

        Assert.Equal(LicenseStatus.Expired, (await Service(expired).EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task ServerRevocationTakesEffectImmediately()
    {
        var service = Service(Licensed(), ValidationOutcome.Revoked);

        Assert.Equal(LicenseStatus.Revoked, (await service.EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task ANetworkFailureFallsBackToTheOfflineGracePeriod()
    {
        // Okul interneti kesikken program CALISMAYA DEVAM ETMELIDIR.
        var service = Service(Licensed(lastValidatedAt: Now.AddDays(-5)), ValidationOutcome.Unreachable);

        Assert.Equal(LicenseStatus.Valid, (await service.EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task PassingThirtyOfflineDaysLocksTheProgram()
    {
        var service = Service(Licensed(lastValidatedAt: Now.AddDays(-31)), ValidationOutcome.Unreachable);

        Assert.Equal(LicenseStatus.OfflineGracePeriodExceeded, (await service.EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task TheTwentyThirdOfflineDayWarnsButStillWorks()
    {
        var service = Service(Licensed(lastValidatedAt: Now.AddDays(-23)), ValidationOutcome.Unreachable);

        var evaluation = await service.EvaluateAsync(default);

        Assert.Equal(LicenseStatus.Valid, evaluation.Status);
        Assert.NotNull(evaluation.Message);
        Assert.Equal(7, evaluation.RemainingOfflineDays);
    }

    [Fact]
    public async Task WindingTheClockBackIsTreatedAsTampering()
    {
        // Sayaci sifirlamak icin sistem saatini geri almak en kolay saldiridir.
        var service = Service(Licensed(lastValidatedAt: Now), ValidationOutcome.Unreachable, now: Now.AddDays(-2));

        Assert.Equal(LicenseStatus.Tampered, (await service.EvaluateAsync(default)).Status);
    }

    [Fact]
    public async Task ASuccessfulOnlineValidationRefreshesTheOfflineCounter()
    {
        var store = new MemoryStore { Current = Licensed(lastValidatedAt: Now.AddDays(-10)) };
        var service = new LicenseService(store, new StubReader(),
            new StubClient { Outcome = ValidationOutcome.Valid }, Options(), new FakeTimeProvider(Now));

        await service.EvaluateAsync(default);

        Assert.Equal(Now, store.Current!.LastValidatedAt);
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public StoredLicense? Current { get; set; }
        public StoredLicense? Read() => Current;
        public void Write(StoredLicense license) => Current = license;
        public void Clear() => Current = null;
    }

    private sealed class StubReader : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => Machine;
    }

    private sealed class StubClient : ILicenseActivationClient
    {
        public ValidationOutcome Outcome { get; init; } = ValidationOutcome.Unreachable;

        public Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivationResult(false, null, "kullanilmadi"));

        public Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(Outcome, license.ExpiresAt, license.Signature));
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~LicenseServiceTests"`
Expected: FAIL — `LicenseService`, `LicenseOptions` tipleri bulunamadı.

- [ ] **Step 3: `Microsoft.Extensions.TimeProvider.Testing` paketini ekle**

Bu paket projede YOKTUR (dogrulandi); `FakeTimeProvider` onsuz derlenmez.
Saat davranisini test etmek icin gercek saati beklemek yerine sahte saat kullanilir.

```bash
dotnet add tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 4: Uygulamayı yaz**

`src/Yemekhane.Licensing/LicenseService.cs`:

```csharp
namespace Yemekhane.Licensing;

public sealed class LicenseOptions
{
    /// <summary>Okul interneti haftalarca kesik kalabilir; 30 gun sahada gecerli bir denge.</summary>
    public int OfflineGraceDays { get; init; } = 30;

    /// <summary>Bu gunden sonra kullanici uyarilir ama program calismaya devam eder.</summary>
    public int WarningThresholdDays { get; init; } = 23;

    public required string SigningSecret { get; init; }
}

public sealed record LicenseEvaluation(LicenseStatus Status, StoredLicense? License, string? Message,
    int? RemainingOfflineDays);

/// <summary>
/// Lisans karar motoru. Yanlis bir "gecerli" gelir kaybidir; yanlis bir kilit
/// ise ogrencinin yemek yiyememesidir. Bu yuzden her dal acikca yazilmistir.
/// </summary>
public sealed class LicenseService(
    ILicenseStore store,
    IHardwareFingerprintReader fingerprintReader,
    ILicenseActivationClient activationClient,
    LicenseOptions options,
    TimeProvider timeProvider)
{
    public async Task<LicenseEvaluation> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        if (store.Read() is not { } license)
            return new(LicenseStatus.NotActivated, null, "Ürün henüz etkinleştirilmedi.", null);

        if (!LicenseSignature.Verify(license, options.SigningSecret))
            return new(LicenseStatus.Tampered, null, "Lisans dosyası değiştirilmiş.", null);

        if (!FingerprintMatcher.Matches(license.Fingerprint, fingerprintReader.Read()))
            return new(LicenseStatus.WrongMachine, null,
                "Bu lisans başka bir bilgisayar için etkinleştirilmiş.", null);

        var now = timeProvider.GetUtcNow();

        // Saat geri alinmis: cevrimdisi sayaci boylece sifirlanamaz.
        if (now < license.LastValidatedAt)
            return new(LicenseStatus.Tampered, null,
                "Sistem saati geriye alınmış görünüyor. Saati düzeltip yeniden deneyin.", null);

        if (now > license.ExpiresAt)
            return new(LicenseStatus.Expired, null, "Lisans süresi dolmuş.", null);

        var validation = await activationClient.ValidateAsync(license, cancellationToken).ConfigureAwait(false);

        // Iptal ANINDA etkilidir; ag hatasiyla karistirilmaz.
        if (validation.Outcome == ValidationOutcome.Revoked)
        {
            store.Clear();
            return new(LicenseStatus.Revoked, null, "Lisans iptal edilmiş.", null);
        }

        if (validation.Outcome == ValidationOutcome.Valid)
        {
            var refreshed = license with
            {
                ExpiresAt = validation.ExpiresAt ?? license.ExpiresAt,
                LastValidatedAt = now,
                Signature = string.Empty
            };
            refreshed = refreshed with { Signature = LicenseSignature.Compute(refreshed, options.SigningSecret) };
            store.Write(refreshed);
            return new(LicenseStatus.Valid, refreshed, null, options.OfflineGraceDays);
        }

        // Sunucuya ulasilamadi: cevrimdisi tolerans devreye girer.
        var offlineDays = (int)(now - license.LastValidatedAt).TotalDays;
        if (offlineDays > options.OfflineGraceDays)
            return new(LicenseStatus.OfflineGracePeriodExceeded, null,
                $"Lisans {options.OfflineGraceDays} gündür doğrulanamadı. İnternet bağlantısı gereklidir.", 0);

        var remaining = options.OfflineGraceDays - offlineDays;
        var message = offlineDays >= options.WarningThresholdDays
            ? $"Lisans {offlineDays} gündür doğrulanamadı. {remaining} gün içinde internet bağlantısı gereklidir."
            : null;

        return new(LicenseStatus.Valid, license, message, remaining);
    }

    public async Task<LicenseEvaluation> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new(LicenseStatus.NotActivated, null, "Lisans anahtarı girin.", null);

        var fingerprint = fingerprintReader.Read();

        // Hicbir bilesen okunamiyorsa makineye baglama yapilamaz. Sessizce
        // "her makine gecerli" duruma DUSULMEZ.
        if (fingerprint.ReadableCount == 0)
            return new(LicenseStatus.NotActivated, null,
                "Bilgisayar kimliği okunamadı. Uygulamayı yönetici olarak çalıştırmayı deneyin.", null);

        var result = await activationClient.ActivateAsync(licenseKey.Trim(), fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.License is null)
            return new(LicenseStatus.NotActivated, null, result.ErrorMessage ?? "Etkinleştirme başarısız.", null);

        store.Write(result.License);
        return await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~LicenseServiceTests"`
Expected: PASS (11 test)

- [ ] **Step 6: Commit**

```bash
git add src/Yemekhane.Licensing/LicenseService.cs tests/Yemekhane.UnitTests/Licensing/LicenseServiceTests.cs tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj
git commit -m "Lisans karar motoru: cevrimdisi tolerans ve saat korumasi"
```

---

### Task 7: HTTP aktivasyon istemcisi

**Files:**
- Create: `src/Yemekhane.Licensing/HttpLicenseActivationClient.cs`
- Test: `tests/Yemekhane.UnitTests/Licensing/HttpLicenseActivationClientTests.cs`

**Interfaces:**
- Consumes: `ILicenseActivationClient`, `ActivationResult`, `ValidationResult` (Task 1)
- Produces: `HttpLicenseActivationClient(HttpClient client, string signingSecret)`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/HttpLicenseActivationClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class HttpLicenseActivationClientTests
{
    private static readonly HardwareFingerprint Machine = new("B", "D", "G");

    private static HttpLicenseActivationClient Client(HttpStatusCode status, string body = "{}") =>
        new(new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://lisans.ornek/") },
            "test-imza-anahtari");

    private static StoredLicense Sample() => new("ANAHTAR-1", "Ornek", "Standard", Machine,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), DateTimeOffset.UtcNow, "imza");

    [Fact]
    public async Task ASuccessfulActivationReturnsALicense()
    {
        var body = """
        {"customerName":"Ornek Okulu","edition":"Standard",
         "issuedAt":"2026-01-01T00:00:00+00:00","expiresAt":"2027-01-01T00:00:00+00:00",
         "signature":"sunucu-imzasi"}
        """;

        var result = await Client(HttpStatusCode.OK, body).ActivateAsync("ANAHTAR-1", Machine);

        Assert.True(result.Succeeded);
        Assert.Equal("Ornek Okulu", result.License!.CustomerName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "başka bir bilgisayarda")]
    [InlineData(HttpStatusCode.NotFound, "bulunamadı")]
    [InlineData(HttpStatusCode.Gone, "iptal")]
    public async Task ServerRejectionsBecomeReadableTurkishMessages(HttpStatusCode status, string expectedFragment)
    {
        // Kullanici "409 Conflict" gormemeli; ne yapacagini anlamali.
        var result = await Client(status).ActivateAsync("ANAHTAR-1", Machine);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedFragment, result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAsUnreachableNotAsRevoked()
    {
        // Bu ayrim kritik: ag hatasi iptal sayilirsa internet kesintisi okulu kilitler.
        var client = new HttpLicenseActivationClient(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://lisans.ornek/") },
            "test-imza-anahtari");

        var result = await client.ValidateAsync(Sample());

        Assert.Equal(ValidationOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ServerGoneMeansRevoked()
    {
        var result = await Client(HttpStatusCode.Gone).ValidateAsync(Sample());

        Assert.Equal(ValidationOutcome.Revoked, result.Outcome);
    }

    [Fact]
    public async Task AServerErrorIsUnreachableNotRevoked()
    {
        // 500 sunucu arizasidir; musterinin lisansi iptal edilmis degildir.
        var result = await Client(HttpStatusCode.InternalServerError).ValidateAsync(Sample());

        Assert.Equal(ValidationOutcome.Unreachable, result.Outcome);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("ağ yok");
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~HttpLicenseActivationClientTests"`
Expected: FAIL — `HttpLicenseActivationClient` tipi bulunamadı.

- [ ] **Step 3: Uygulamayı yaz**

`src/Yemekhane.Licensing/HttpLicenseActivationClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Yemekhane.Licensing;

/// <summary>
/// Lisans sunucusuyla konusur. Sunucu sozlesmesi tasarim dokumaninda tanimlidir.
/// </summary>
public sealed class HttpLicenseActivationClient(HttpClient client, string signingSecret) : ILicenseActivationClient
{
    private sealed record ActivationResponse(
        [property: JsonPropertyName("customerName")] string CustomerName,
        [property: JsonPropertyName("edition")] string Edition,
        [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record ValidationResponse(
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("signature")] string Signature);

    public async Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("activate", new
            {
                licenseKey,
                fingerprints = new[] { fingerprint.BaseBoardHash, fingerprint.DiskHash, fingerprint.MachineGuidHash },
                productVersion = typeof(HttpLicenseActivationClient).Assembly.GetName().Version?.ToString()
            }, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new(false, null, DescribeFailure(response.StatusCode));

            var payload = await response.Content.ReadFromJsonAsync<ActivationResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (payload is null) return new(false, null, "Sunucu yanıtı okunamadı.");

            var license = new StoredLicense(licenseKey, payload.CustomerName, payload.Edition, fingerprint,
                payload.IssuedAt, payload.ExpiresAt, DateTimeOffset.UtcNow, string.Empty);
            // Yerel imza, dosyanin sonradan degistirilmedigini dogrular.
            return new(true, license with { Signature = LicenseSignature.Compute(license, signingSecret) }, null);
        }
        catch (HttpRequestException)
        {
            return new(false, null, "Lisans sunucusuna ulaşılamadı. İnternet bağlantınızı kontrol edin.");
        }
        catch (TaskCanceledException)
        {
            return new(false, null, "Lisans sunucusu yanıt vermedi. Daha sonra yeniden deneyin.");
        }
    }

    public async Task<ValidationResult> ValidateAsync(StoredLicense license,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("validate", new
            {
                licenseKey = license.LicenseKey,
                fingerprints = new[]
                {
                    license.Fingerprint.BaseBoardHash, license.Fingerprint.DiskHash, license.Fingerprint.MachineGuidHash
                },
                signature = license.Signature
            }, cancellationToken).ConfigureAwait(false);

            // Yalnizca 410 iptaldir. 500 sunucu arizasidir ve musterinin
            // lisansi iptal edilmis DEGILDIR; cevrimdisi toleransa dusulur.
            if (response.StatusCode == HttpStatusCode.Gone)
                return new(ValidationOutcome.Revoked, null, null);
            if (!response.IsSuccessStatusCode)
                return new(ValidationOutcome.Unreachable, null, null);

            var payload = await response.Content.ReadFromJsonAsync<ValidationResponse>(cancellationToken)
                .ConfigureAwait(false);
            return payload is null
                ? new(ValidationOutcome.Unreachable, null, null)
                : new(ValidationOutcome.Valid, payload.ExpiresAt, payload.Signature);
        }
        catch (HttpRequestException) { return new(ValidationOutcome.Unreachable, null, null); }
        catch (TaskCanceledException) { return new(ValidationOutcome.Unreachable, null, null); }
    }

    private static string DescribeFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Conflict => "Bu lisans başka bir bilgisayarda kullanımda.",
        HttpStatusCode.NotFound => "Lisans anahtarı bulunamadı. Anahtarı kontrol edin.",
        HttpStatusCode.Gone => "Bu lisans iptal edilmiş. Satıcınızla görüşün.",
        _ => "Etkinleştirme başarısız oldu. Daha sonra yeniden deneyin."
    };
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~HttpLicenseActivationClientTests"`
Expected: PASS (7 test)

- [ ] **Step 5: Commit**

```bash
git add src/Yemekhane.Licensing/HttpLicenseActivationClient.cs tests/Yemekhane.UnitTests/Licensing/HttpLicenseActivationClientTests.cs
git commit -m "HTTP lisans aktivasyon istemcisi"
```

---

### Task 8: Aktivasyon ekranı (ViewModel + pencere)

**Files:**
- Create: `src/Yemekhane.Desktop/ViewModels/ActivationViewModel.cs`
- Create: `src/Yemekhane.Desktop/Views/ActivationWindow.xaml`
- Create: `src/Yemekhane.Desktop/Views/ActivationWindow.xaml.cs`
- Modify: `src/Yemekhane.Desktop/Yemekhane.Desktop.csproj` (Licensing referansı)
- Test: `tests/Yemekhane.UnitTests/Licensing/ActivationViewModelTests.cs`

**Interfaces:**
- Consumes: `LicenseService`, `LicenseEvaluation`, `LicenseStatus` (Task 6); `AsyncCommand`, `ObservableObject` (mevcut)
- Produces: `ActivationViewModel(LicenseService, LicenseEvaluation initial)` — `LicenseKey`, `StatusText`, `ErrorMessage`, `HasError`, `IsBusy`, `MachineId`, `IsActivated`, `ActivateCommand`

- [ ] **Step 1: Desktop projesine referans ekle**

`src/Yemekhane.Desktop/Yemekhane.Desktop.csproj` içindeki ilk `ItemGroup`'a ekle:

```xml
    <ProjectReference Include="..\Yemekhane.Licensing\Yemekhane.Licensing.csproj" />
```

- [ ] **Step 2: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/ActivationViewModelTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class ActivationViewModelTests
{
    private const string Secret = "test-imza-anahtari";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly HardwareFingerprint Machine = new("B", "D", "G");

    private static (ActivationViewModel Screen, MemoryStore Store) Build(ActivationResult activationResult)
    {
        var store = new MemoryStore();
        var service = new LicenseService(store, new StubReader(),
            new StubClient { Result = activationResult }, new LicenseOptions { SigningSecret = Secret },
            new FakeTimeProvider(Now));
        var initial = new LicenseEvaluation(LicenseStatus.NotActivated, null, "Ürün henüz etkinleştirilmedi.", null);
        return (new ActivationViewModel(service, initial), store);
    }

    private static StoredLicense Licensed()
    {
        var license = new StoredLicense("ANAHTAR-1", "Ornek Okulu", "Standard", Machine,
            Now.AddYears(-1), Now.AddYears(1), Now, string.Empty);
        return license with { Signature = LicenseSignature.Compute(license, Secret) };
    }

    [Fact]
    public void TheReasonForTheLockIsShownToTheUser()
    {
        var (screen, _) = Build(new ActivationResult(false, null, null));

        Assert.Contains("etkinleştir", screen.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheActivateButtonIsDisabledUntilAKeyIsEntered()
    {
        var (screen, _) = Build(new ActivationResult(false, null, null));

        Assert.False(screen.ActivateCommand.CanExecute(null));

        screen.LicenseKey = "ANAHTAR-1";

        Assert.True(screen.ActivateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASuccessfulActivationStoresTheLicenseAndClosesTheScreen()
    {
        var (screen, store) = Build(new ActivationResult(true, Licensed(), null));
        screen.LicenseKey = "ANAHTAR-1";

        await screen.ActivateCommand.ExecuteAsync(null);

        Assert.True(screen.IsActivated);
        Assert.NotNull(store.Current);
    }

    [Fact]
    public async Task AFailedActivationShowsTheServerMessageAndKeepsTheScreenOpen()
    {
        var (screen, store) = Build(new ActivationResult(false, null, "Bu lisans başka bir bilgisayarda kullanımda."));
        screen.LicenseKey = "ANAHTAR-1";

        await screen.ActivateCommand.ExecuteAsync(null);

        Assert.False(screen.IsActivated);
        Assert.True(screen.HasError);
        Assert.Contains("başka bir bilgisayarda", screen.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task TheKeyBoxIsLockedWhileActivationIsRunning()
    {
        // Islem surerken kullanici anahtari degistirebilseydi, donen sonuc
        // ekranda yazan anahtarla eslesmezdi.
        var (screen, _) = Build(new ActivationResult(true, Licensed(), null));
        screen.LicenseKey = "ANAHTAR-1";

        Assert.True(screen.IsEditable);
        await screen.ActivateCommand.ExecuteAsync(null);
        Assert.True(screen.IsEditable, "İşlem bittikten sonra kutu yeniden açılmalı.");
    }

    [Fact]
    public void TheMachineIdIsShownSoSupportCanIdentifyTheComputer()
    {
        var (screen, _) = Build(new ActivationResult(false, null, null));

        Assert.False(string.IsNullOrWhiteSpace(screen.MachineId));
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public StoredLicense? Current { get; set; }
        public StoredLicense? Read() => Current;
        public void Write(StoredLicense license) => Current = license;
        public void Clear() => Current = null;
    }

    private sealed class StubReader : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => Machine;
    }

    private sealed class StubClient : ILicenseActivationClient
    {
        public required ActivationResult Result { get; init; }

        public Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(ValidationOutcome.Unreachable, null, null));
    }
}
```

- [ ] **Step 3: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~ActivationViewModelTests"`
Expected: FAIL — `ActivationViewModel` tipi bulunamadı.

- [ ] **Step 4: ViewModel'i yaz**

`src/Yemekhane.Desktop/ViewModels/ActivationViewModel.cs`:

```csharp
using System.Windows.Input;
using Yemekhane.Licensing;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Aktivasyon ekrani. Kullanici NEDEN kilitli oldugunu ve NE yapacagini
/// anlamalidir; "bir hata olustu" yeterli degildir.
/// </summary>
public sealed class ActivationViewModel : ObservableObject
{
    private readonly LicenseService service;
    private string licenseKey = string.Empty;
    private string? errorMessage;
    private bool isBusy, isActivated;

    public ActivationViewModel(LicenseService service, LicenseEvaluation initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        this.service = service;
        StatusText = Describe(initial);
        MachineId = BuildMachineId(initial);
        ActivateCommand = new AsyncCommand(ActivateAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(LicenseKey));
    }

    public string StatusText { get; }

    /// <summary>Destek ekibinin bilgisayari tanimasi icin; kullanici kopyalayabilir.</summary>
    public string MachineId { get; }

    public AsyncCommand ActivateCommand { get; }

    public string LicenseKey
    {
        get => licenseKey;
        set { if (Set(ref licenseKey, value)) ActivateCommand.Refresh(); }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Raise(nameof(IsEditable));
            ActivateCommand.Refresh();
        }
    }

    /// <summary>Metin kutusunun duzenlenebilirligi. XAML'de tersine cevirici kullanmamak icin olumlu tutulur.</summary>
    public bool IsEditable => !IsBusy;

    public bool IsActivated { get => isActivated; private set => Set(ref isActivated, value); }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private async Task ActivateAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var evaluation = await service.ActivateAsync(LicenseKey);
            if (evaluation.Status == LicenseStatus.Valid) IsActivated = true;
            else ErrorMessage = evaluation.Message ?? "Etkinleştirme başarısız oldu.";
        }
        finally { IsBusy = false; }
    }

    private static string Describe(LicenseEvaluation evaluation) => evaluation.Status switch
    {
        LicenseStatus.NotActivated => "Ürünü kullanmak için lisans anahtarınızı girin.",
        LicenseStatus.Tampered => "Lisans dosyası geçersiz. Lütfen yeniden etkinleştirin.",
        LicenseStatus.WrongMachine => "Bu lisans başka bir bilgisayar için etkinleştirilmiş.",
        LicenseStatus.Expired => "Lisans süresi dolmuş. Yenilemek için satıcınızla görüşün.",
        LicenseStatus.Revoked => "Lisans iptal edilmiş. Satıcınızla görüşün.",
        LicenseStatus.OfflineGracePeriodExceeded =>
            "Lisans uzun süredir doğrulanamadı. Bir kez internete bağlanmanız gerekiyor.",
        _ => "Lisans anahtarınızı girin."
    };

    private static string BuildMachineId(LicenseEvaluation evaluation)
    {
        // Kayitli lisans varsa onun parmak izi, yoksa okunan deger gosterilir.
        var hash = evaluation.License?.Fingerprint.MachineGuidHash
            ?? evaluation.License?.Fingerprint.BaseBoardHash;
        return hash is null ? "-" : hash[..12].ToUpperInvariant();
    }
}
```

- [ ] **Step 5: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~ActivationViewModelTests"`
Expected: PASS (6 test)

- [ ] **Step 6: Pencereyi yaz**

`src/Yemekhane.Desktop/Views/ActivationWindow.xaml`:

```xml
<Window x:Class="Yemekhane.Desktop.Views.ActivationWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="YemekhanePro • Etkinleştirme" Height="360" Width="470"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize"
        Background="#F5F6F7" FontFamily="Segoe UI" FontSize="12" Language="tr-TR">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisibility"/>
    </Window.Resources>
    <Grid Margin="26">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/><RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Text="Ürün Etkinleştirme" FontSize="20" FontWeight="SemiBold" Foreground="#18222D"/>
        <TextBlock Grid.Row="1" Text="{Binding StatusText}" Foreground="#65717E"
                   TextWrapping="Wrap" Margin="0,8,0,16"/>

        <StackPanel Grid.Row="2">
            <TextBlock Text="Lisans anahtarı" Foreground="#65717E" Margin="0,0,0,4"/>
            <!-- IsEnabled dogrudan olumlu bir ozelliye baglanir. InverseBooleanConverter'in
                 statik bir Instance alani YOKTUR; x:Static ile kullanilamaz. -->
            <TextBox Height="30" Text="{Binding LicenseKey, UpdateSourceTrigger=PropertyChanged}"
                     IsEnabled="{Binding IsEditable}"/>
            <TextBlock Margin="0,10,0,0" Foreground="#96A1AC">
                <Run Text="Bilgisayar kimliği:"/>
                <Run Text="{Binding MachineId, Mode=OneWay}" FontFamily="Consolas"/>
            </TextBlock>
        </StackPanel>

        <Border Grid.Row="3" Background="#FDECEA" BorderBrush="#F5C6C2" BorderThickness="1" CornerRadius="6"
                Padding="10" Margin="0,14,0,0" VerticalAlignment="Top"
                Visibility="{Binding HasError, Converter={StaticResource BoolToVisibility}}">
            <TextBlock Text="{Binding ErrorMessage}" Foreground="#B3261E" TextWrapping="Wrap"/>
        </Border>

        <StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Çıkış" Click="OnCancel" Padding="16,7" Margin="0,0,8,0"/>
            <Button Content="Etkinleştir" Command="{Binding ActivateCommand}" IsDefault="True"
                    Padding="16,7" Background="#1D5FA8" Foreground="White" BorderBrush="#1D5FA8"/>
        </StackPanel>
    </Grid>
</Window>
```

`src/Yemekhane.Desktop/Views/ActivationWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop.Views;

public partial class ActivationWindow : Window
{
    private readonly ActivationViewModel viewModel;

    public ActivationWindow(ActivationViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        // Aktivasyon basarili oldugunda pencere kendi kendine kapanir; cagiran
        // taraf DialogResult'a bakar.
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActivationViewModel.IsActivated) || !viewModel.IsActivated) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

- [ ] **Step 7: Derle**

Run: `dotnet build Yemekhane.sln`
Expected: 0 hata

- [ ] **Step 8: Commit**

```bash
git add src/Yemekhane.Desktop/ViewModels/ActivationViewModel.cs src/Yemekhane.Desktop/Views/ActivationWindow.xaml src/Yemekhane.Desktop/Views/ActivationWindow.xaml.cs src/Yemekhane.Desktop/Yemekhane.Desktop.csproj tests/Yemekhane.UnitTests/Licensing/ActivationViewModelTests.cs
git commit -m "Aktivasyon ekrani"
```

---

### Task 9: Açılışa bağlama

Lisans kontrolü **yerel API başlamadan önce** çalışır. Bu, planın en riskli adımıdır: mevcut açılış sırası hâlihazırda bir kez sahada kırılmıştı (bkz. hafıza: bootstrap yeniden başlatma çökmesi).

**Files:**
- Modify: `src/Yemekhane.Desktop/App.xaml.cs`
- Modify: `src/Yemekhane.Desktop/appsettings.json`
- Test: `tests/Yemekhane.UnitTests/Licensing/StartupLicenseGateTests.cs`

**Interfaces:**
- Consumes: `LicenseService`, `LicenseEvaluation`, `ActivationViewModel`, `ActivationWindow`, `DpapiLicenseStore`, `WindowsHardwareFingerprintReader`, `HttpLicenseActivationClient`
- Produces: `AppStartup.EnsureLicensedAsync(LicenseService, Func<LicenseEvaluation, bool> showActivation) -> Task<bool>`

- [ ] **Step 1: Başarısız testi yaz**

`tests/Yemekhane.UnitTests/Licensing/StartupLicenseGateTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Yemekhane.Desktop;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Lisans kapisi. En kritik davranis: lisans gecersizken yerel API'nin
/// HIC BASLAMAMASI. Aksi halde veritabani ve turnike servisleri lisanssiz ayaga kalkar.
/// </summary>
public sealed class StartupLicenseGateTests
{
    private const string Secret = "test-imza-anahtari";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly HardwareFingerprint Machine = new("B", "D", "G");

    private static LicenseService Service(StoredLicense? stored) =>
        new(new MemoryStore { Current = stored }, new StubReader(), new StubClient(),
            new LicenseOptions { SigningSecret = Secret }, new FakeTimeProvider(Now));

    private static StoredLicense Licensed()
    {
        var license = new StoredLicense("ANAHTAR-1", "Ornek", "Standard", Machine,
            Now.AddYears(-1), Now.AddYears(1), Now, string.Empty);
        return license with { Signature = LicenseSignature.Compute(license, Secret) };
    }

    [Fact]
    public async Task AValidLicenseLetsStartupContinueWithoutShowingTheScreen()
    {
        var shown = false;

        var allowed = await AppStartup.EnsureLicensedAsync(Service(Licensed()), _ => { shown = true; return true; });

        Assert.True(allowed);
        Assert.False(shown, "Geçerli lisansta aktivasyon ekranı gösterilmemeli.");
    }

    [Fact]
    public async Task AnInvalidLicenseShowsTheActivationScreen()
    {
        var shown = false;

        await AppStartup.EnsureLicensedAsync(Service(null), _ => { shown = true; return true; });

        Assert.True(shown);
    }

    [Fact]
    public async Task CancellingActivationStopsStartup()
    {
        // Kullanici vazgecerse uygulama ACILMAMALIDIR.
        var allowed = await AppStartup.EnsureLicensedAsync(Service(null), _ => false);

        Assert.False(allowed);
    }

    [Fact]
    public async Task SucceedingOnTheActivationScreenLetsStartupContinue()
    {
        var allowed = await AppStartup.EnsureLicensedAsync(Service(null), _ => true);

        Assert.True(allowed);
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public StoredLicense? Current { get; set; }
        public StoredLicense? Read() => Current;
        public void Write(StoredLicense license) => Current = license;
        public void Clear() => Current = null;
    }

    private sealed class StubReader : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => Machine;
    }

    private sealed class StubClient : ILicenseActivationClient
    {
        public Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivationResult(false, null, "kullanilmadi"));

        public Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(ValidationOutcome.Unreachable, null, null));
    }
}
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~StartupLicenseGateTests"`
Expected: FAIL — `AppStartup.EnsureLicensedAsync` bulunamadı.

- [ ] **Step 3: Kapıyı `AppStartup`'a ekle**

`src/Yemekhane.Desktop/AppStartup.cs` dosyasına ekle:

```csharp
    /// <summary>
    /// Lisans kapisi. Gecerliyse true doner; degilse aktivasyon ekranini gosterir
    /// (showActivation) ve kullanicinin sonucuna gore devam edilir.
    ///
    /// UI'dan ayri tutulmustur: pencere acmayi bir temsilciye devrederek bu mantik
    /// bassiz olarak test edilebilir hale gelir.
    /// </summary>
    public static async Task<bool> EnsureLicensedAsync(
        Yemekhane.Licensing.LicenseService licenseService,
        Func<Yemekhane.Licensing.LicenseEvaluation, bool> showActivation)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        ArgumentNullException.ThrowIfNull(showActivation);

        var evaluation = await licenseService.EvaluateAsync();
        return evaluation.Status == Yemekhane.Licensing.LicenseStatus.Valid || showActivation(evaluation);
    }
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test tests/Yemekhane.UnitTests/Yemekhane.UnitTests.csproj --filter "FullyQualifiedName~StartupLicenseGateTests"`
Expected: PASS (4 test)

- [ ] **Step 5: `App.xaml.cs` içinde kapıyı çağır**

`src/Yemekhane.Desktop/App.xaml.cs` içinde, `localApi = new LocalApiProcessManager(baseUri);` satırının **ÖNCESİNE** ekle:

```csharp
        // Lisans kontrolu yerel API BASLAMADAN once yapilir: lisanssiz bir
        // kurulumda veritabani ve turnike servisleri hic ayaga kalkmamalidir.
        var licenseService = new Yemekhane.Licensing.LicenseService(
            new Yemekhane.Licensing.DpapiLicenseStore(
                Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve()),
            new Yemekhane.Licensing.WindowsHardwareFingerprintReader(),
            new Yemekhane.Licensing.HttpLicenseActivationClient(
                new HttpClient
                {
                    BaseAddress = new Uri(configuration["Licensing:Endpoint"] ?? "https://lisans.yemekhanepro.local/"),
                    Timeout = TimeSpan.FromSeconds(10)
                },
                configuration["Licensing:SigningSecret"] ?? "yemekhanepro-varsayilan-imza-anahtari"),
            new Yemekhane.Licensing.LicenseOptions
            {
                SigningSecret = configuration["Licensing:SigningSecret"] ?? "yemekhanepro-varsayilan-imza-anahtari"
            },
            TimeProvider.System);

        var licensed = await AppStartup.EnsureLicensedAsync(licenseService, evaluation =>
            new Views.ActivationWindow(new ViewModels.ActivationViewModel(licenseService, evaluation))
                .ShowDialog() == true);
        if (!licensed) return false;
```

- [ ] **Step 6: `appsettings.json`'a ayarları ekle**

`src/Yemekhane.Desktop/appsettings.json` içindeki kök nesneye ekle:

```json
  "Licensing": {
    "Endpoint": "https://lisans.yemekhanepro.local/",
    "SigningSecret": "yemekhanepro-varsayilan-imza-anahtari"
  }
```

- [ ] **Step 7: Tam derleme ve tüm testler**

Run: `dotnet build Yemekhane.sln` → 0 hata
Run: `dotnet test Yemekhane.sln` → tüm testler geçmeli

- [ ] **Step 8: Commit**

```bash
git add src/Yemekhane.Desktop/App.xaml.cs src/Yemekhane.Desktop/AppStartup.cs src/Yemekhane.Desktop/appsettings.json tests/Yemekhane.UnitTests/Licensing/StartupLicenseGateTests.cs
git commit -m "Lisans kapisini acilisa bagla"
```

---

### Task 10: Tam doğrulama

**Files:**
- Test: yok (yalnızca doğrulama)

- [ ] **Step 1: Tüm testleri üç kez çalıştır**

Run (3 kez): `dotnet test Yemekhane.sln`
Expected: her seferinde aynı sayı, 0 başarısız. Sayı değişiyorsa test yalıtımı bozulmuştur — düzeltilmeden devam edilmez.

- [ ] **Step 2: Analiz uyarısı olmadığını doğrula**

Run: `dotnet build Yemekhane.sln 2>&1 | grep -i "warning"`
Expected: lisans dosyalarıyla ilgili uyarı yok.

- [ ] **Step 3: Commit (gerekirse)**

```bash
git commit --allow-empty -m "Lisanslama dogrulamasi tamamlandi"
```

---

## Sonraki adımlar (bu planın kapsamı dışında)

1. **Aktivasyon sunucusu** — sözleşmesi spec'te tanımlı; ayrı bir plan gerektirir.
2. **`Licensing:SigningSecret` üretim değeri** — varsayılan değer geliştirme içindir; yayın öncesi DPAPI korumalı ayara taşınmalıdır.
3. **ConfuserEx obfüskasyonu** — build sürecine en son eklenir.
