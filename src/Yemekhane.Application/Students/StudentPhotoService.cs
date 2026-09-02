using Yemekhane.Application.Common;

namespace Yemekhane.Application.Students;

/// <summary>Diskten okunan fotograf: icerik akisi ve tarayici/istemci icin MIME turu.</summary>
public sealed record StudentPhotoFile(Stream Content, string ContentType, string FileName);

/// <summary>
/// Fotograf dosyalarinin fiziksel deposu. Application katmani dosya sistemini bilmez;
/// yol her zaman veri klasorune GORELI ("photos/{id}.png") tutulur ki veri klasoru
/// tasindiginda (OkulYemek -> YemekhanePro gocu gibi) kayitlar kirilmasin.
/// </summary>
public interface IStudentPhotoStore
{
    /// <summary>Icerigi yazar ve GORELI yolu dondurur; ayni ogrencinin eski dosyasi ezilir.</summary>
    Task<string> SaveAsync(Guid studentId, string extension, Stream content, CancellationToken cancellationToken);
    /// <summary>Dosya yoksa null.</summary>
    Task<StudentPhotoFile?> OpenAsync(string relativePath, CancellationToken cancellationToken);
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);
}

/// <summary>
/// Sicil karti fotografi: yukle / oku / sil. Eski programda "Resim Sec" ile secilen
/// dosya veritabanina degil, veri klasorune yazilir; kayitta yalnizca goreli yol durur.
/// </summary>
public sealed class StudentPhotoService(IStudentRepository repository, IStudentPhotoStore store)
{
    public const long MaximumBytes = 2 * 1024 * 1024;

    public async Task<StudentDetails> UploadAsync(Guid studentId, string? fileName, long length, Stream content, CancellationToken cancellationToken = default)
    {
        if (length <= 0) throw new RequestValidationException("Fotoğraf dosyası boş.");
        if (length > MaximumBytes) throw new RequestValidationException("Fotoğraf en fazla 2 MB olabilir.");
        var student = await repository.GetAsync(studentId, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");

        // Uzanti degil, dosyanin ILK BAYTLARI belirleyici: ".png" adli bir metin dosyasi
        // ekranda kirik resim olarak kalir ve kullanici nedenini anlayamaz.
        var extension = await DetectExtensionAsync(content, cancellationToken)
            ?? throw new RequestValidationException("Yalnızca JPG ve PNG fotoğraflar yüklenebilir.");
        var claimed = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (claimed.Length != 0 && claimed is not ("jpg" or "jpeg" or "png"))
            throw new RequestValidationException("Yalnızca JPG ve PNG fotoğraflar yüklenebilir.");

        var relativePath = await store.SaveAsync(studentId, extension, content, cancellationToken);
        // Uzanti degistiyse (png -> jpg) eski dosya yetim kalmasin.
        if (student.PhotoPath is not null && !string.Equals(student.PhotoPath, relativePath, StringComparison.OrdinalIgnoreCase))
            await store.DeleteAsync(student.PhotoPath, cancellationToken);
        await repository.SetPhotoPathAsync(studentId, relativePath, cancellationToken);
        return await repository.GetAsync(studentId, cancellationToken) ?? student with { PhotoPath = relativePath };
    }

    public async Task<StudentPhotoFile> GetAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var student = await repository.GetAsync(studentId, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
        if (string.IsNullOrWhiteSpace(student.PhotoPath)) throw new EntityNotFoundException("Fotoğraf bulunamadı.");
        return await store.OpenAsync(student.PhotoPath, cancellationToken)
            ?? throw new EntityNotFoundException("Fotoğraf dosyası bulunamadı.");
    }

    public async Task DeleteAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var student = await repository.GetAsync(studentId, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
        if (student.PhotoPath is not null) await store.DeleteAsync(student.PhotoPath, cancellationToken);
        await repository.SetPhotoPathAsync(studentId, null, cancellationToken);
    }

    /// <summary>PNG (89 50 4E 47) ya da JPEG (FF D8 FF) imzasi; akis basa sarilir.</summary>
    private static async Task<string?> DetectExtensionAsync(Stream content, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        if (content.CanSeek) content.Position = 0;
        else throw new RequestValidationException("Fotoğraf akışı okunamadı.");
        if (read >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return "png";
        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "jpg";
        return null;
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };
}
