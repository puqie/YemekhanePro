using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Devices;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.DeviceCards;

/// <summary>
/// "Cihazdaki kartlar" listesi (eski programdaki Cihaz Sicil Listesi'nin sunucu tarafi):
/// kart-cihaz durum tablosundan, arama ve sayfalamayla. Silinmis ogrencinin karti cihazda hala
/// oldugu icin listelenir; cihazdan SILINMIS (Removed) kart listelenmez.
/// </summary>
public sealed class EfDeviceCardListQueryTests
{
    [Fact]
    public async Task ListsCardsOfOneDeviceOrderedByStudentNumberWithoutRemovedOnes()
    {
        await using var db = await Db.CreateAsync();

        var result = await db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id), default);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(["5001", "5002", "5003"], result.Items.Select(x => x.StudentNo));
        var ada = result.Items[0];
        Assert.Equal("ADA YILMAZ", ada.StudentName); Assert.Equal("5A", ada.ClassName);
        Assert.Equal(DeviceCardSyncStatus.Loaded, ada.Status); Assert.NotNull(ada.LastSyncedAt);
        var ali = result.Items[1];
        Assert.Equal(DeviceCardSyncStatus.Failed, ali.Status); Assert.Equal("SF300_FULL", ali.LastError); Assert.Equal(3, ali.AttemptCount);
        // Silinmis ogrencinin karti cihazda: listede kalir ki operator "cihazda ne var" sorusuna tam yanit alsin.
        Assert.Equal("SİLİNMİŞ ÖĞRENCİ", result.Items[2].StudentName);
        Assert.DoesNotContain(result.Items, x => x.StudentNo == "5004");
    }

    [Fact]
    public async Task OtherDeviceSeesOnlyItsOwnStates()
    {
        await using var db = await Db.CreateAsync();

        var result = await db.Query.ListAsync(new DeviceCardListQuery(db.Exit.Id), default);

        var row = Assert.Single(result.Items);
        Assert.Equal("5001", row.StudentNo); Assert.Equal(DeviceCardSyncStatus.Pending, row.Status);
    }

    [Theory]
    [InlineData("ada", "5001")]        // ad, kucuk harf (Turkce normalizasyon)
    [InlineData("yılmaz", "5001")]     // soyad
    [InlineData("8350002", "5002")]    // kart no
    [InlineData("5003", "5003")]       // ogrenci no
    public async Task SearchMatchesNumberNameOrCardFromTheStart(string term, string expectedNo)
    {
        await using var db = await Db.CreateAsync();

        var result = await db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id, term), default);

        var row = Assert.Single(result.Items);
        Assert.Equal(expectedNo, row.StudentNo);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchIsPrefixNotSubstring()
    {
        await using var db = await Db.CreateAsync();

        // "0002" kart 8350002'nin ortasindadir; bastan eslesme istenir (Ogrenciler ekraniyla ayni kural).
        var result = await db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id, "0002"), default);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task PagesCarryTheTotalCount()
    {
        await using var db = await Db.CreateAsync();

        var first = await db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id, Page: 1, PageSize: 2), default);
        var second = await db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id, Page: 2, PageSize: 2), default);

        Assert.Equal(2, first.Items.Count); Assert.Equal(3, first.TotalCount);
        Assert.Equal(["5003"], second.Items.Select(x => x.StudentNo)); Assert.Equal(2, second.Page);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public async Task RejectsInvalidPaging(int page, int pageSize)
    {
        await using var db = await Db.CreateAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            db.Query.ListAsync(new DeviceCardListQuery(db.Entry.Id, Page: page, PageSize: pageSize), default));
    }

    private sealed class Db(SqliteConnection connection, YemekhaneDbContext context, Device entry, Device exit) : IAsyncDisposable
    {
        public Device Entry { get; } = entry;
        public Device Exit { get; } = exit;
        public IDeviceCardListQuery Query { get; } = new EfDeviceCardListQuery(context);

        public static async Task<Db> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            // DeviceType SF300 DEGIL: YemekhaneDbContext.SaveChanges yeni karti aktif SF300 cihazlara
            // kendiliginden kuyruklar; test durumlari acikca kurar.
            var entry = Device("Giriş", "10.0.0.1");
            var exit = Device("Çıkış", "10.0.0.2");
            var class5 = new SchoolClass { Name = "5A" };
            var ada = new Student { StudentNo = "5001", FirstName = "ADA", LastName = "YILMAZ", ClassId = class5.Id };
            var ali = new Student { StudentNo = "5002", FirstName = "ALİ", LastName = "KAYA" };
            var gone = new Student { StudentNo = "5003", FirstName = "SİLİNMİŞ", LastName = "ÖĞRENCİ", IsDeleted = true };
            var removed = new Student { StudentNo = "5004", FirstName = "AYŞE", LastName = "ÇELİK" };
            context.AddRange(entry, exit, class5, ada, ali, gone, removed);
            var cards = new[]
            {
                new StudentCard { StudentId = ada.Id, CardNumber = "8350001", ValidFrom = DateTimeOffset.UtcNow },
                new StudentCard { StudentId = ali.Id, CardNumber = "8350002", ValidFrom = DateTimeOffset.UtcNow },
                new StudentCard { StudentId = gone.Id, CardNumber = "8350003", ValidFrom = DateTimeOffset.UtcNow },
                new StudentCard { StudentId = removed.Id, CardNumber = "8350004", ValidFrom = DateTimeOffset.UtcNow }
            };
            context.AddRange(cards);
            context.AddRange(
                State(entry, cards[0], DeviceCardSyncStatus.Loaded, DateTimeOffset.UtcNow),
                State(exit, cards[0], DeviceCardSyncStatus.Pending, null),
                State(entry, cards[1], DeviceCardSyncStatus.Failed, null, "SF300_FULL", 3),
                State(entry, cards[2], DeviceCardSyncStatus.Loaded, DateTimeOffset.UtcNow),
                State(entry, cards[3], DeviceCardSyncStatus.Removed, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
            return new Db(connection, context, entry, exit);
        }

        private static Device Device(string name, string ip) => new()
        {
            Name = name, DeviceType = "Turnstile", ConnectionType = "Ethernet", Direction = "Entry",
            ConnectionStatus = "Offline", IpAddress = ip, IpPort = 4370
        };

        private static DeviceCardState State(Device device, StudentCard card, string status, DateTimeOffset? syncedAt,
            string? error = null, int attempts = 0) => new()
        {
            DeviceId = device.Id, CardId = card.Id, StudentId = card.StudentId, CardNumber = card.CardNumber,
            Status = status, LastSyncedAt = syncedAt, LastError = error, AttemptCount = attempts
        };

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
