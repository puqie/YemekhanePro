using Microsoft.Data.Sqlite;
using Yemekhane.Application.Students;

namespace Yemekhane.Infrastructure.Students;

/// <summary>
/// Fotograflari <c>&lt;veri klasoru&gt;/photos/{ogrenciId}.{png|jpg}</c> olarak saklar.
/// Kayitta tutulan yol veri klasorune GORELI ("photos/..."); mutlak yol saklansaydi
/// veri klasoru tasindiginda (bkz. ApplicationDataPath) her fotograf kaybolurdu.
/// </summary>
public sealed class FileStudentPhotoStore(string rootDirectory) : IStudentPhotoStore
{
    public const string FolderName = "photos";
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    /// <summary>
    /// Baglanti dizgisindeki veritabani dosyasinin yanindaki photos/ klasoru. Bellek-ici
    /// veya dosyasiz baglantilarda (testler) gecici klasore duser; boylece kayit
    /// yolu asla surec calisma dizinine bagli olmaz.
    /// </summary>
    public static string ResolveRoot(string connectionString)
    {
        string? dataSource = null;
        try { dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource; }
        catch (ArgumentException) { }
        var databaseDirectory = dataSource is not null && Path.IsPathRooted(dataSource) && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(dataSource))
            : null;
        return Path.Combine(databaseDirectory ?? Path.Combine(Path.GetTempPath(), "YemekhanePro"), FolderName);
    }

    public async Task<string> SaveAsync(Guid studentId, string extension, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDirectory);
        var fileName = $"{studentId:D}.{extension.TrimStart('.').ToLowerInvariant()}";
        var target = Path.Combine(RootDirectory, fileName);
        // Once gecici dosyaya yazilir, sonra yerine konur: yarim kalan bir yukleme
        // (baglanti kopmasi) eski fotografi bozmaz.
        var temporary = target + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await content.CopyToAsync(output, cancellationToken);
        File.Move(temporary, target, overwrite: true);
        return FolderName + "/" + fileName;
    }

    public Task<StudentPhotoFile?> OpenAsync(string relativePath, CancellationToken cancellationToken)
    {
        var full = Resolve(relativePath);
        if (full is null || !File.Exists(full)) return Task.FromResult<StudentPhotoFile?>(null);
        // FileShare.Read: ayni anda ikinci bir okuma (liste + cekmece) engellenmez; yazma
        // Save'de gecici dosya + Move oldugundan okuma kilidiyle catismaz.
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult<StudentPhotoFile?>(new StudentPhotoFile(stream, StudentPhotoService.ContentTypeFor(full), Path.GetFileName(full)));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        var full = Resolve(relativePath);
        if (full is not null && File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Goreli yolu kok altinda cozer; "photos/" disina cikan ya da ".." iceren bir
    /// deger (bozuk veri, elle duzenlenmis kayit) null doner -- baska bir dosya
    /// asla okunmaz/silinmez.
    /// </summary>
    private string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/');
        if (!normalized.StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase)) return null;
        var fileName = normalized[(FolderName.Length + 1)..];
        if (fileName.Length == 0 || fileName.Contains('/') || fileName.Contains("..", StringComparison.Ordinal)) return null;
        var full = Path.GetFullPath(Path.Combine(RootDirectory, fileName));
        return full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
