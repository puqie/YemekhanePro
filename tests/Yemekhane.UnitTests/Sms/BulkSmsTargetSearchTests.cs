using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// SMS alici aramasi ve veli cozumlemesi: canli denetimde "ada" aramasi BOS donuyordu
/// (sicil buyuk harf, EF Contains -> SQLite instr harf duyarli) ve birincil isaretlenmemis
/// velisi olan ogrenci "telefon yok" sayiliyordu.
/// </summary>
public sealed class BulkSmsTargetSearchTests
{
    [Theory]
    [InlineData("ada")]
    [InlineData("ADA")]
    [InlineData("Ada")]
    [InlineData("akgün")]
    [InlineData("AKGÜN")]
    public async Task AramaBuyukKucukHarfVeTurkceHarfeDuyarsizdir(string search)
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Repository.TargetsAsync(search, CancellationToken.None);

        var names = result.Students.Select(x => x.Name).ToArray();
        Assert.Equal(["ADA AKGÜN"], names);
    }

    [Theory]
    [InlineData("ırmak")]
    [InlineData("IRMAK")]
    [InlineData("irmak")]
    public async Task NoktaliNoktasizIAyrimiAramayiEngellemez(string search)
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Repository.TargetsAsync(search, CancellationToken.None);

        Assert.Contains(result.Students, x => x.Name == "Irmak Yıldız");
    }

    [Fact]
    public async Task OgrenciNumarasiylaAramaCalisir()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Repository.TargetsAsync("5002", CancellationToken.None);

        Assert.Equal(["5002"], result.Students.Select(x => x.StudentNo).ToArray());
    }

    [Fact]
    public async Task FiltreKapsamiDaAyniAramaKuraliniKullanir()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Repository.ResolveAsync(new BulkSmsScope("Filter", Search: "ada"), CancellationToken.None);

        Assert.Equal(["ADA AKGÜN"], result.Select(x => x.StudentName).ToArray());
    }

    [Fact]
    public async Task BirincilVeliYoksaAktifVeliTelefonuKullanilir()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Repository.ResolveAsync(new BulkSmsScope("All"), CancellationToken.None);

        // 5001: yalnizca birincil OLMAYAN aktif veli -> yine de telefon bulunmali.
        var ada = result.Single(x => x.StudentName == "ADA AKGÜN");
        Assert.Equal("+905321110001", ada.Phone);
        Assert.Equal("Yedek Veli", ada.ParentName);
        // 5002: birincil veli varken ikincil veli tercih edilmemeli.
        var irmak = result.Single(x => x.StudentName == "Irmak Yıldız");
        Assert.Equal("+905321110002", irmak.Phone);
        Assert.Equal("Asıl Veli", irmak.ParentName);
        // 5003: yalnizca PASIF veli -> telefon yok.
        var mert = result.Single(x => x.StudentName == "Mert Kaya");
        Assert.Null(mert.Phone);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private Fixture(SqliteConnection connection, YemekhaneDbContext db)
        {
            this.connection = connection; Db = db;
            Repository = new EfBulkSmsRepository(db, TimeProvider.System);
        }
        public YemekhaneDbContext Db { get; }
        public EfBulkSmsRepository Repository { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var ada = new Student { StudentNo = "5001", FirstName = "ADA", LastName = "AKGÜN", IsActive = true };
            var irmak = new Student { StudentNo = "5002", FirstName = "Irmak", LastName = "Yıldız", IsActive = true };
            var mert = new Student { StudentNo = "5003", FirstName = "Mert", LastName = "Kaya", IsActive = true };
            db.Students.AddRange(ada, irmak, mert);
            db.Parents.AddRange(
                new Parent { StudentId = ada.Id, Name = "Yedek Veli", NormalizedPhone = "+905321110001", IsPrimary = false },
                new Parent { StudentId = irmak.Id, Name = "Asıl Veli", NormalizedPhone = "+905321110002", IsPrimary = true },
                new Parent { StudentId = irmak.Id, Name = "İkincil Veli", NormalizedPhone = "+905321110009", IsPrimary = false },
                new Parent { StudentId = mert.Id, Name = "Eski Veli", NormalizedPhone = "+905321110003", IsPrimary = true, IsActive = false });
            await db.SaveChangesAsync();
            return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
