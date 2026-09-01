using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Yemekhane.Licensing;

/// <summary>Makinenin donanim parmak izini uretir.</summary>
public interface IHardwareFingerprintReader
{
    /// <summary>Uc bilesenin hash'ini okur. Okunamayan bilesen bos dize olur.</summary>
    HardwareFingerprint Read();
}

/// <summary>
/// Windows uzerinde WMI ve kayit defterinden donanim bileseni okur.
///
/// Bilesen sirasi SABITTIR (anakart, disk, makine GUID): sira degisirse sahadaki
/// lisanslarda kayitli hash'ler yanlis bilesenle karsilastirilir ve tum musteriler
/// "yanlis makine" hatasi alir.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareFingerprintReader : IHardwareFingerprintReader
{
    public HardwareFingerprint Read() =>
        new([
            FingerprintHasher.Hash(ReadWmiProperty("Win32_BaseBoard", "SerialNumber")),
            FingerprintHasher.Hash(ReadSystemDiskSerial()),
            FingerprintHasher.Hash(ReadMachineGuid())
        ]);

    private static string? ReadWmiProperty(string wmiClass, string property)
    {
        // WMI erisimi kisitlanmis, servis durdurulmus veya sanal donanim bos deger
        // donduruyor olabilir. Bu bir hata degildir: bilesen okunamamis sayilir ve
        // diger ikisi karari verir. Uygulamanin ACILMAMASI kabul edilemez.
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var value = item[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value)) return value;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        return null;
    }

    private static string? ReadSystemDiskSerial()
    {
        // Yalnizca sistem diski okunur. Tum diskleri toplamak, takilan bir USB bellek
        // yuzunden parmak izinin degismesine ve lisansin gecersizlesmesine yol acardi.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SerialNumber, Index FROM Win32_DiskDrive WHERE Index = 0");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var value = item["SerialNumber"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value)) return value;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        return null;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
    }

    /// <summary>
    /// Bazi anakart ve sanal disk ureticileri seri numarasi yerine sabit bir doldurma
    /// metni yazar. Bu degerler BUTUN makinelerde aynidir; gercek bir bilesen sayilirsa
    /// farkli bilgisayarlar birbirinin ayni gorunur ve lisans serbestce kopyalanir.
    /// </summary>
    private static bool IsPlaceholder(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length < 2) return true;

        string[] placeholders =
        [
            "To be filled by O.E.M.", "To Be Filled By O.E.M.", "Default string",
            "None", "N/A", "NA", "Not Applicable", "System Serial Number",
            "Not Specified", "Unknown", "0", "00000000"
        ];

        if (placeholders.Any(candidate => normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Yalnizca sifir veya bosluktan olusan degerler de gercek seri numarasi degildir.
        return normalized.All(character => character is '0' or ' ' or '-' or '.');
    }
}
