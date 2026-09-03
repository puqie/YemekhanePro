using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.KeyTool;

/// <summary>
/// Imza sirrini satici bilgisayarinda saklar.
///
/// <para>
/// Duz metin YAZILMAZ: sir sizarsa herkes gecerli lisans anahtari uretebilir ve
/// korumanin tamami anlamini yitirir. Windows DPAPI ile sifrelenir; dosya baska
/// bir bilgisayara kopyalansa cozulmez.
/// </para>
/// <para>
/// Kapsam <see cref="DataProtectionScope.CurrentUser"/>: sir yalnizca SIZIN Windows
/// hesabinizla cozulur. Ayni bilgisayardaki baska bir kullanici dosyayi okusa bile
/// icerigi alamaz -- LocalMachine kapsami bunu saglamazdi.
/// </para>
/// </summary>
public static class SecretStore
{
    /// <summary>
    /// DPAPI entropisi: sifreli veriyi bu uygulamaya baglar. Baska bir program
    /// ayni kullanici olarak calissa bile bu entropiyi bilmeden cozemez.
    /// </summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("YemekhanePro.LisansUretici.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YemekhanePro", "lisans-uretici.dat");

    /// <summary>Kayitli sirri okur; yoksa veya cozulemezse null.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
        }
        // Dosya baska bir kullanici/bilgisayar tarafindan yazilmis ya da bozulmus.
        // Cokme yerine "kayitli sir yok" davranisi dogru: kullanici yeniden girer.
        catch (Exception exception) when (exception is CryptographicException or IOException)
        {
            return null;
        }
    }

    /// <summary>Sirri sifreleyerek kaydeder.</summary>
    public static void Save(string secret)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Kayitli sirri siler (baska bir sirra gecerken).</summary>
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (IOException) { }
    }
}
