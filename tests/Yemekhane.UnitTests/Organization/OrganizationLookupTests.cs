using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Organization;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Organization;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Organization;

/// <summary>
/// Sinif / sube / bolum / gorev tanimlari: eski programdaki dort "Tanim" ekraninin
/// API karsiligi. Kullanilan tanim silinemez; ad tekrari 409; yeniden adlandirma
/// sinifin arama adini (SearchName) da gunceller.
/// </summary>
public sealed class OrganizationLookupTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public OrganizationLookupTests()
    {
        connection.Open();
        using var context = Create();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private YemekhaneDbContext Create() =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    private OrganizationService Service(YemekhaneDbContext context) => new(new EfOrganizationRepository(context));

    [Theory]
    [InlineData(LookupKind.Section)]
    [InlineData(LookupKind.Department)]
    [InlineData(LookupKind.Job)]
    [InlineData(LookupKind.Class)]
    public async Task EkleListeleYenidenAdlandirSil(LookupKind kind)
    {
        await using var context = Create();
        var service = Service(context);

        var created = await service.CreateLookupAsync(kind, "  İdare ", CancellationToken.None);
        Assert.Equal("İdare", created.Name);
        Assert.Equal(0, created.StudentCount);

        var listed = Assert.Single(await service.ListLookupsAsync(kind, CancellationToken.None));
        Assert.Equal(created.Id, listed.Id);

        var renamed = await service.RenameLookupAsync(kind, created.Id, "Öğrenci", CancellationToken.None);
        Assert.Equal("Öğrenci", renamed.Name);

        await service.DeleteLookupAsync(kind, created.Id, CancellationToken.None);
        Assert.Empty(await service.ListLookupsAsync(kind, CancellationToken.None));
    }

    [Fact]
    public async Task AyniAdIkinciKezEklenemez()
    {
        await using var context = Create();
        var service = Service(context);
        await service.CreateLookupAsync(LookupKind.Department, "İdare", CancellationToken.None);

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateLookupAsync(LookupKind.Department, "İdare", CancellationToken.None));
    }

    [Fact]
    public async Task BosAdReddedilir() =>
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(Create()).CreateLookupAsync(LookupKind.Job, "   ", CancellationToken.None));

    [Fact]
    public async Task OgrencideKullanilanTanimSilinemezVeSayisiGorunur()
    {
        await using var context = Create();
        var service = Service(context);
        var section = await service.CreateLookupAsync(LookupKind.Section, "A", CancellationToken.None);
        context.Students.Add(new Student { StudentNo = "5001", FirstName = "Ada", LastName = "Katırcı", SectionId = section.Id });
        // Silinmis ogrenci sayilmaz.
        context.Students.Add(new Student { StudentNo = "5002", FirstName = "Ada", LastName = "Söylemez", SectionId = section.Id, IsDeleted = true });
        await context.SaveChangesAsync(CancellationToken.None);

        var listed = Assert.Single(await service.ListLookupsAsync(LookupKind.Section, CancellationToken.None));
        Assert.Equal(1, listed.StudentCount);

        var conflict = await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.DeleteLookupAsync(LookupKind.Section, section.Id, CancellationToken.None));
        Assert.Contains("1 öğrencide", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinifYenidenAdlandirilincaAramaAdiGuncellenir()
    {
        await using var context = Create();
        var service = Service(context);
        var created = await service.CreateLookupAsync(LookupKind.Class, "5A", CancellationToken.None);
        await service.RenameLookupAsync(LookupKind.Class, created.Id, "6A", CancellationToken.None);

        await using var fresh = Create();
        var stored = await fresh.Set<SchoolClass>().SingleAsync(x => x.Id == created.Id, CancellationToken.None);
        Assert.Equal("6A", stored.Name);
        Assert.Equal(TurkishSearchText.Normalize("6A"), stored.SearchName);
    }

    [Fact]
    public void BilinmeyenRotaParcasi400()
    {
        Assert.Equal(LookupKind.Section, OrganizationService.ParseKind("sections"));
        Assert.Throws<RequestValidationException>(() => OrganizationService.ParseKind("groups"));
    }
}
