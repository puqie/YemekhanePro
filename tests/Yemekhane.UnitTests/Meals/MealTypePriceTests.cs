using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Meals;
using Yemekhane.Infrastructure.Meals;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Meals;

/// <summary>
/// Ogun ucreti (eski programdaki "Ucret TL"): kurus hassasiyetiyle saklanir, listede
/// geri okunur, guncellenir; fiyati hic girilmemis ogun 0 ₺ doner.
/// </summary>
public sealed class MealTypePriceTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public MealTypePriceTests()
    {
        connection.Open();
        using var context = Create();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private YemekhaneDbContext Create() =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task UcretKaydedilirGeriOkunurGuncellenir()
    {
        await using var context = Create();
        var service = new MealTypeService(new EfMealTypeRepository(context));

        var created = await service.CreateAsync(new SaveMealTypeRequest("Öğle Yemeği", new TimeOnly(12, 0), new TimeOnly(14, 0), Price: 250.50m), CancellationToken.None);
        Assert.Equal(250.50m, created.Price);

        var listed = Assert.Single(await service.ListAsync(cancellationToken: CancellationToken.None));
        Assert.Equal(250.50m, listed.Price);

        var updated = await service.UpdateAsync(created.Id, new SaveMealTypeRequest("Öğle Yemeği", new TimeOnly(12, 0), new TimeOnly(14, 0), Price: 300m), CancellationToken.None);
        Assert.Equal(300m, updated.Price);
        Assert.Equal(300m, Assert.Single(await service.ListAsync(cancellationToken: CancellationToken.None)).Price);
    }

    [Fact]
    public async Task UcretsizOgunSifirDoner()
    {
        await using var context = Create();
        var service = new MealTypeService(new EfMealTypeRepository(context));
        await service.CreateAsync(new SaveMealTypeRequest("Kahvaltı"), CancellationToken.None);

        Assert.Equal(0m, Assert.Single(await service.ListAsync(cancellationToken: CancellationToken.None)).Price);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    [InlineData(12.345)]
    public async Task GecersizUcretReddedilir(double price)
    {
        await using var context = Create();
        var service = new MealTypeService(new EfMealTypeRepository(context));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(new SaveMealTypeRequest("Akşam", Price: (decimal)price), CancellationToken.None));
    }
}
