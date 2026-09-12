using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Cards;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Cards;

/// <summary>
/// Kartlar ekraninin sunucu tarafi: TUM kartlar (aktif + pasif) tek listede, arama, durum
/// suzgeci, sayfalama. Sayaclar suzgecten bagimsizdir; silinmis ogrencinin karti da listelenir.
/// </summary>
public sealed class EfCardListQueryTests
{
    [Fact]
    public async Task ListsEveryCardWithStatusAndFilterIndependentCounts()
    {
        await using var db = await Db.CreateAsync();

        var result = await db.Query.ListAsync(new CardListQuery(), default);

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(3, result.ActiveCount);
        Assert.Equal(1, result.PassiveCount);
        Assert.Equal(["8350001", "8350002", "8350005", "8350003"], result.Items.Select(x => x.CardNumber));
        var ada = result.Items[0];
        Assert.Equal("ADA YILMAZ", ada.StudentName); Assert.Equal("5A", ada.ClassName); Assert.Equal("6296", ada.PrintedNumber);
        Assert.True(ada.IsActive); Assert.True(ada.StudentActive); Assert.Null(ada.ValidTo);
        var lost = result.Items[1];
        Assert.False(lost.IsActive); Assert.Equal("Kayıp", lost.ReplacementReason); Assert.NotNull(lost.ValidTo);
        // Silinmis ogrencinin karti listede kalir ama ogrencinin pasif oldugu soylenir.
        Assert.False(result.Items[3].StudentActive);
    }

    [Fact]
    public async Task StatusFilterNarrowsRowsButNotTheCounts()
    {
        await using var db = await Db.CreateAsync();

        var passive = await db.Query.ListAsync(new CardListQuery(IsActive: false), default);
        var active = await db.Query.ListAsync(new CardListQuery(IsActive: true), default);

        Assert.Equal("8350002", Assert.Single(passive.Items).CardNumber);
        Assert.Equal(1, passive.TotalCount); Assert.Equal(3, passive.ActiveCount); Assert.Equal(1, passive.PassiveCount);
        Assert.Equal(3, active.TotalCount);
        Assert.All(active.Items, x => Assert.True(x.IsActive));
    }

    [Theory]
    [InlineData("ada", "8350001")]        // ad, kucuk harf (Turkce normalizasyon)
    [InlineData("yılmaz", "8350001")]     // soyad
    [InlineData("6296", "8350001")]       // baski no
    [InlineData("8350005", "8350005")]    // kart no
    [InlineData("5003", "8350003")]       // ogrenci no
    public async Task SearchMatchesNumberNameCardOrPrintedNumberFromTheStart(string term, string expectedCard)
    {
        await using var db = await Db.CreateAsync();

        var result = await db.Query.ListAsync(new CardListQuery(term), default);

        Assert.Equal(expectedCard, Assert.Single(result.Items).CardNumber);
    }

    [Fact]
    public async Task PagingIsValidatedAndSlicesTheOrderedList()
    {
        await using var db = await Db.CreateAsync();

        var second = await db.Query.ListAsync(new CardListQuery(Page: 2, PageSize: 3), default);

        Assert.Equal("8350003", Assert.Single(second.Items).CardNumber);
        Assert.Equal(4, second.TotalCount);
        await Assert.ThrowsAsync<RequestValidationException>(() => db.Query.ListAsync(new CardListQuery(Page: 0), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => db.Query.ListAsync(new CardListQuery(PageSize: 201), default));
    }

    private sealed class Db(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        public ICardListQuery Query { get; } = new EfCardListQuery(context);

        public static async Task<Db> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var class5 = new SchoolClass { Name = "5A" };
            var ada = new Student { StudentNo = "5001", FirstName = "ADA", LastName = "YILMAZ", ClassId = class5.Id, IsActive = true };
            var ali = new Student { StudentNo = "5002", FirstName = "ALİ", LastName = "KAYA", IsActive = true };
            var gone = new Student { StudentNo = "5003", FirstName = "SİLİNMİŞ", LastName = "ÖĞRENCİ", IsDeleted = true, IsActive = false };
            context.AddRange(class5, ada, ali, gone);
            var now = DateTimeOffset.UtcNow;
            context.AddRange(
                new StudentCard { StudentId = ada.Id, CardNumber = "8350001", PrintedNumber = "6296", ValidFrom = now, IsActive = true },
                // Ali'nin eski karti kayip: pasif, nedeniyle. Yeni karti aktif.
                new StudentCard { StudentId = ali.Id, CardNumber = "8350002", ValidFrom = now.AddDays(-30), ValidTo = now.AddDays(-1), ReplacementReason = "Kayıp", IsActive = false },
                new StudentCard { StudentId = ali.Id, CardNumber = "8350005", ValidFrom = now, IsActive = true },
                new StudentCard { StudentId = gone.Id, CardNumber = "8350003", ValidFrom = now, IsActive = true });
            await context.SaveChangesAsync();
            return new Db(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
