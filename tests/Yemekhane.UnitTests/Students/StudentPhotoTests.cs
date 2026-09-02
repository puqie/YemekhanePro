using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Sicil karti fotografi: yukleme / okuma / silme ve reddedilen dosyalar. Fotograf
/// veritabaninda DEGIL, veri klasoru altindaki photos/ klasorunde durur; kayitta
/// yalnizca goreli yol tutulur (veri klasoru tasindiginda kirilmasin diye).
/// </summary>
public sealed class StudentPhotoTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly YemekhaneDbContext db;
    private readonly string root;
    private readonly StudentPhotoService service;
    private readonly EfStudentRepository repository;

    public StudentPhotoTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        root = Path.Combine(Path.GetTempPath(), "yp-photo-" + Guid.NewGuid().ToString("N"));
        repository = new EfStudentRepository(db);
        service = new StudentPhotoService(repository, new FileStudentPhotoStore(root));
    }

    private async Task<Guid> CreateStudentAsync()
    {
        var students = new StudentService(repository);
        var created = await students.CreateAsync(new SaveStudentRequest("7100", "Ada", "Yılmaz"));
        return created.Id;
    }

    /// <summary>Yuklenen dosya photos/{id}.png olur, kayitta GORELI yol durur ve geri okunur.</summary>
    [Fact]
    public async Task FotografYuklenirOkunurVeSilinir()
    {
        var id = await CreateStudentAsync();
        var png = SmallPng();

        var updated = await service.UploadAsync(id, "vesikalik.png", png.Length, new MemoryStream(png));

        Assert.Equal($"photos/{id:D}.png", updated.PhotoPath);
        Assert.True(File.Exists(Path.Combine(root, $"{id:D}.png")));
        // Mutlak yol SAKLANMAZ: veri klasoru tasinirsa kayit kirilmamali.
        Assert.DoesNotContain(root, updated.PhotoPath!, StringComparison.OrdinalIgnoreCase);

        var read = await service.GetAsync(id);
        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer);
        read.Content.Dispose();
        Assert.Equal(png, buffer.ToArray());
        Assert.Equal("image/png", read.ContentType);

        await service.DeleteAsync(id);
        Assert.False(File.Exists(Path.Combine(root, $"{id:D}.png")));
        Assert.Null((await repository.GetAsync(id, default))!.PhotoPath);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.GetAsync(id));
    }

    /// <summary>2 MB ustu dosya REDDEDILIR ve diske hicbir sey yazilmaz.</summary>
    [Fact]
    public async Task BuyukDosyaReddedilir()
    {
        var id = await CreateStudentAsync();
        var big = new byte[StudentPhotoService.MaximumBytes + 1];

        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => service.UploadAsync(id, "buyuk.png", big.LongLength, new MemoryStream(big)));

        Assert.Contains("2 MB", error.Message);
        Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root).Any());
    }

    /// <summary>
    /// Uzanti degil ICERIK belirleyicidir: ".png" adli bir metin dosyasi reddedilir,
    /// aksi halde ekranda sessizce kirik resim gorunurdu.
    /// </summary>
    [Fact]
    public async Task ResimOlmayanIcerikReddedilir()
    {
        var id = await CreateStudentAsync();
        var text = System.Text.Encoding.UTF8.GetBytes("bu bir resim degil");

        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => service.UploadAsync(id, "sahte.png", text.LongLength, new MemoryStream(text)));

        Assert.Contains("JPG ve PNG", error.Message);
        Assert.Null((await repository.GetAsync(id, default))!.PhotoPath);
    }

    [Fact]
    public async Task GecersizUzantiReddedilir()
    {
        var id = await CreateStudentAsync();
        var png = SmallPng();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => service.UploadAsync(id, "belge.pdf", png.LongLength, new MemoryStream(png)));
    }

    [Fact]
    public async Task BosDosyaVeBilinmeyenOgrenciReddedilir()
    {
        var id = await CreateStudentAsync();
        await Assert.ThrowsAsync<RequestValidationException>(
            () => service.UploadAsync(id, "bos.png", 0, new MemoryStream()));

        var png = SmallPng();
        await Assert.ThrowsAsync<EntityNotFoundException>(
            () => service.UploadAsync(Guid.NewGuid(), "a.png", png.LongLength, new MemoryStream(png)));
    }

    /// <summary>PNG'den JPG'ye gecince ESKI dosya silinir; klasorde yetim dosya kalmaz.</summary>
    [Fact]
    public async Task UzantiDegisinceEskiDosyaSilinir()
    {
        var id = await CreateStudentAsync();
        var png = SmallPng();
        await service.UploadAsync(id, "a.png", png.Length, new MemoryStream(png));
        var jpg = SmallJpeg();

        var updated = await service.UploadAsync(id, "a.jpg", jpg.Length, new MemoryStream(jpg));

        Assert.Equal($"photos/{id:D}.jpg", updated.PhotoPath);
        Assert.False(File.Exists(Path.Combine(root, $"{id:D}.png")), "eski PNG yetim kaldi");
        Assert.Single(Directory.EnumerateFiles(root));
    }

    /// <summary>
    /// Kayittaki yol photos/ disina cikamaz: bozuk ya da elle duzenlenmis bir deger
    /// ("../../yemekhane.db") baska bir dosyayi OKUTMAMALI ve SILDIRMEMELI.
    /// </summary>
    [Theory]
    [InlineData("../yemekhane.db")]
    [InlineData("photos/../../yemekhane.db")]
    [InlineData("yemekhane.db")]
    [InlineData("photos/alt/klasor.png")]
    public async Task KotuYolCozulmez(string stored)
    {
        var store = new FileStudentPhotoStore(root);
        Assert.Null(await store.OpenAsync(stored, default));
        await store.DeleteAsync(stored, default);   // sessizce hicbir sey yapmali
    }

    private static byte[] SmallPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        .. System.Text.Encoding.ASCII.GetBytes("test-govde")
    ];

    private static byte[] SmallJpeg() => [0xFF, 0xD8, 0xFF, 0xE0, .. System.Text.Encoding.ASCII.GetBytes("jpeg-govde")];

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
