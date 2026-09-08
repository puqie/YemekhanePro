using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Organization;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Organization;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Organization;

/// <summary>
/// Sinif turu: okul anasinifini normal siniflardan ayirmak istedi. Tur sinif tanimina
/// yazilir (Normal / Anasinifi), bos birakilinca Normal'dir, yeniden adlandirmada bos tur
/// mevcut turu KORUR, bilinmeyen tur 400 verir. Sube/bolum/gorev tur tasimaz.
/// </summary>
public sealed class ClassKindTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ClassKindTests()
    {
        connection.Open();
        using var context = Create();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private YemekhaneDbContext Create() =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    private static OrganizationService Service(YemekhaneDbContext context) => new(new EfOrganizationRepository(context));

    [Fact]
    public async Task PreschoolKindIsStoredListedAndLabelled()
    {
        await using var context = Create();
        var service = Service(context);

        var created = await service.CreateLookupAsync(LookupKind.Class, "Anasınıfı A", "Anasinifi", CancellationToken.None);

        Assert.Equal(ClassKinds.Preschool, created.Kind);
        Assert.Equal("Anasınıfı", created.KindLabel);
        var listed = Assert.Single(await service.ListLookupsAsync(LookupKind.Class, CancellationToken.None));
        Assert.Equal(ClassKinds.Preschool, listed.Kind);
        var classRecord = Assert.Single(await service.ListClassesAsync(CancellationToken.None));
        Assert.Equal(ClassKinds.Preschool, classRecord.Kind);
        await using var fresh = Create();
        Assert.Equal(ClassKinds.Preschool, (await fresh.Set<SchoolClass>().SingleAsync()).Kind);
    }

    [Fact]
    public async Task BlankKindDefaultsToNormalAndOldStringRouteStaysNormal()
    {
        await using var context = Create();
        var service = Service(context);

        var viaLookup = await service.CreateLookupAsync(LookupKind.Class, "5A", "  ", CancellationToken.None);
        var viaLegacy = await service.CreateClassAsync("5B", CancellationToken.None);

        Assert.Equal(ClassKinds.Normal, viaLookup.Kind);
        Assert.Equal("Normal sınıf", viaLookup.KindLabel);
        Assert.Equal(ClassKinds.Normal, viaLegacy.Kind);
    }

    [Fact]
    public async Task KindIsMatchedCaseInsensitivelyAndUnknownIsRejected()
    {
        await using var context = Create();
        var service = Service(context);

        var created = await service.CreateLookupAsync(LookupKind.Class, "Anasınıfı B", "anasinifi", CancellationToken.None);
        Assert.Equal(ClassKinds.Preschool, created.Kind);

        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateLookupAsync(LookupKind.Class, "Kreş", "Kres", CancellationToken.None));
        Assert.Contains("Anasinifi", error.Message, StringComparison.Ordinal);
        Assert.Single(await service.ListLookupsAsync(LookupKind.Class, CancellationToken.None));
    }

    [Fact]
    public async Task RenameWithoutKindKeepsTheKindAndWithKindChangesIt()
    {
        await using var context = Create();
        var service = Service(context);
        var created = await service.CreateLookupAsync(LookupKind.Class, "Anasınıfı A", "Anasinifi", CancellationToken.None);

        var renamedOnly = await service.RenameLookupAsync(LookupKind.Class, created.Id, "Anasınıfı Sabah", null, CancellationToken.None);
        Assert.Equal(ClassKinds.Preschool, renamedOnly.Kind);

        var retyped = await service.RenameLookupAsync(LookupKind.Class, created.Id, "1-A", "Normal", CancellationToken.None);
        Assert.Equal(ClassKinds.Normal, retyped.Kind);
        Assert.Equal("1-A", retyped.Name);
        await using var fresh = Create();
        Assert.Equal(ClassKinds.Normal, (await fresh.Set<SchoolClass>().SingleAsync()).Kind);
    }

    [Theory]
    [InlineData(LookupKind.Section)]
    [InlineData(LookupKind.Department)]
    [InlineData(LookupKind.Job)]
    public async Task NonClassLookupsIgnoreTheKindAndReportNone(LookupKind kind)
    {
        await using var context = Create();
        var service = Service(context);

        var created = await service.CreateLookupAsync(kind, "Deneme", "Anasinifi", CancellationToken.None);
        var listed = Assert.Single(await service.ListLookupsAsync(kind, CancellationToken.None));

        Assert.Null(created.Kind);
        Assert.Null(listed.Kind);
        Assert.Equal("", listed.KindLabel);
    }

    [Fact]
    public void KindHelpersNormalizeAndLabel()
    {
        Assert.Equal(ClassKinds.Normal, ClassKinds.Normalize(null));
        Assert.Equal(ClassKinds.Normal, ClassKinds.Normalize(""));
        Assert.Equal(ClassKinds.Preschool, ClassKinds.Normalize(" ANASINIFI "));
        Assert.Null(ClassKinds.Normalize("Anasınıfı"));
        Assert.Equal("", ClassKinds.Label("Bilinmeyen"));
        Assert.Equal(["Normal", "Anasinifi"], ClassKinds.All);
    }
}
