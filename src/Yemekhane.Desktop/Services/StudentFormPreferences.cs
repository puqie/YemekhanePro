using System.IO;
using System.Text.Json;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Ogrenci kartinda hangi ISTEGE BAGLI alanlarin gorunecegi. Okul kullanmadigi alanlari
/// (TC kimlik, parmak izi, adres...) kalici olarak kapatabilsin diye diske yazilir:
/// her acilista yeniden kapatmak zorunda kalmamak icin.
///
/// ZORUNLU alanlar (Ad, Soyad) burada YOKTUR; onlar gizlenemez. Gizlenen alanin
/// KAYITLI DEGERINE dokunulmaz, yalnizca formda gorunmez.
/// </summary>
public interface IStudentFormPreferences
{
    bool IsVisible(string field);
    void SetVisible(string field, bool visible);
}

/// <summary>Gizlenebilir alanlarin anahtarlari; ekran ve tercih dosyasi ayni adlari kullanir.</summary>
public static class StudentFormFields
{
    public const string NationalId = "NationalId";
    public const string BirthDate = "BirthDate";
    public const string Department = "Department";
    public const string Job = "Job";
    public const string PrintedNumber = "PrintedNumber";
    public const string Photo = "Photo";
    public const string Fingerprint = "Fingerprint";
    public const string Pid = "Pid";
    public const string Address = "Address";
    public const string Notes = "Notes";

    /// <summary>Ekranda gosterilen Turkce adiyla, ayarlar listesinin sirasi.</summary>
    public static readonly IReadOnlyList<(string Field, string Label)> All =
    [
        (NationalId, "TC Kimlik No"),
        (BirthDate, "Doğum tarihi"),
        (Department, "Bölüm"),
        (Job, "Görev"),
        (PrintedNumber, "Baskı No"),
        (Photo, "Fotoğraf"),
        (Fingerprint, "Parmak izi ID"),
        (Pid, "PI ID"),
        (Address, "Adres"),
        (Notes, "Not")
    ];
}

public sealed class FileStudentFormPreferences : IStudentFormPreferences
{
    private readonly string path;
    private readonly Dictionary<string, bool> hidden = new(StringComparer.Ordinal);

    public FileStudentFormPreferences(string? path = null)
    {
        this.path = path ?? Path.Combine(Infrastructure.Persistence.ApplicationDataPath.Resolve(), "student-form.json");
        try
        {
            if (File.Exists(this.path))
                foreach (var field in JsonSerializer.Deserialize<Settings>(File.ReadAllText(this.path))?.Hidden ?? [])
                    hidden[field] = true;
        }
        // Bozuk/okunamayan tercih dosyasi formu acmayi ENGELLEMEMELI; herkes gorunur kalir.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { hidden.Clear(); }
    }

    public bool IsVisible(string field) => !hidden.ContainsKey(field);

    public void SetVisible(string field, bool visible)
    {
        if (visible) hidden.Remove(field); else hidden[field] = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Settings([.. hidden.Keys.Order(StringComparer.Ordinal)])));
        }
        // Yazilamazsa secim bu oturumda gecerli kalir; kullaniciya hata gostermeye degmez.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Settings(IReadOnlyList<string> Hidden);
}

/// <summary>Testler ve tercih verilmeyen durumlar icin: her alan gorunur.</summary>
public sealed class AllFieldsVisible : IStudentFormPreferences
{
    public bool IsVisible(string field) => true;
    public void SetVisible(string field, bool visible) { }
}
