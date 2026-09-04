namespace Yemekhane.Desktop.Services;

/// <param name="CanSubmit">Sifirlama istegi gonderilebilir mi.</param>
/// <param name="Hint">Kullaniciya gosterilecek yonlendirme; engel yoksa bos.</param>
public sealed record PasswordResetFormState(bool CanSubmit, string Hint);

/// <summary>
/// Parola sifirlama formunun karar mantigi.
///
/// <para>
/// CODE-BEHIND'DA DURMAZ: ayni hatanin ucuncu tekrarini onlemek icin ayrildi.
/// <c>MakeFileClick</c> icindeki on kosul, imzalama dali guncellenirken eski
/// halinde kalmisti ve 2000+ testin hicbiri goremiyordu -- cunku code-behind
/// test edilemiyordu. Buradaki kurallar (dosya secildi mi, parolalar tutuyor mu,
/// uzunluk yeterli mi) dogrudan test edilebilir olmalidir.
/// </para>
/// </summary>
public static class PasswordResetForm
{
    /// <summary>
    /// Sunucudaki <c>PasswordResetService.MinimumPasswordLength</c> ile AYNI olmalidir.
    /// Arayuz daha gevsek olsaydi kullanici formu doldurup sunucudan ret alirdi;
    /// daha kati olsaydi gecerli bir parolayi bosuna reddederdi.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    public static PasswordResetFormState Evaluate(
        string? licenseFileContent, string? username, string? newPassword, string? confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(licenseFileContent))
            return new(false, "Önce lisans dosyasını seçin.");
        if (string.IsNullOrWhiteSpace(username))
            return new(false, "Sıfırlanacak kullanıcı adını yazın.");

        var password = newPassword ?? string.Empty;
        if (password.Length < MinimumPasswordLength)
            return new(false, $"Yeni parola en az {MinimumPasswordLength} karakter olmalıdır.");

        // Tekrar alani BOSKEN uyari verilmez ama gonderim de acilmaz: kullanici daha
        // ikinci alani doldurmamisken "parolalar ayni degil" demek yanlis alarmdir.
        if (string.IsNullOrEmpty(confirmPassword))
            return new(false, string.Empty);
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            return new(false, "Parolalar aynı değil.");

        return new(true, string.Empty);
    }
}
