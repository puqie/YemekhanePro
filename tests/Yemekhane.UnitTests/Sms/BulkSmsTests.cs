using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

public sealed class BulkSmsTests
{
    [Fact]
    public async Task PreviewResolvesPrimaryPhonesAndCountsMissingAndDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(Fixture.Request("dupe-count"));

        Assert.Equal(3, preview.MatchedStudents);
        Assert.Equal(1, preview.RecipientCount);
        Assert.Equal(1, preview.NoPhoneCount);
        Assert.Equal(1, preview.DuplicatePhoneCount);
        Assert.Equal("+905321112233", preview.Examples[0].Phone);
    }

    [Fact]
    public async Task ApplyRejectsChangedPreviewAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Fixture.Request("stable-batch");
        var preview = await fixture.Service.PreviewAsync(request);
        // Fixture ayni ogrenci icin iki veli tutuyor; OrderBy olmadan hangisinin dondugu
        // belirsizdi ve test rastgele kiriliyordu. Onizlemedeki alici bu birincil veli.
        var parent = await fixture.Db.Parents.SingleAsync(x => x.IsPrimary && x.NormalizedPhone == "+905321112233");
        parent.NormalizedPhone = "+905551112233";
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(request, preview.PreviewToken)));

        preview = await fixture.Service.PreviewAsync(request);
        var first = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken));
        var replay = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken));

        Assert.Equal(2, first.QueuedCount);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(2, replay.ExistingCount);
        Assert.Equal(2, await fixture.Db.SmsLogs.CountAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private Fixture(SqliteConnection connection, YemekhaneDbContext db)
        {
            this.connection = connection; Db = db;
            Service = new BulkSmsService(new EfBulkSmsRepository(db, TimeProvider.System),
                new EfSmsTemplateRepository(db), new SmsPreviewTokenProtector(), TimeProvider.System);
        }
        public YemekhaneDbContext Db { get; }
        public BulkSmsService Service { get; }
        public static BulkSmsRequest Request(string key) => new(key, new("All"), Message: "Duyuru");

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var students = Enumerable.Range(1, 3).Select(i => new Student
                { StudentNo = $"S{i}", FirstName = $"Ad{i}", LastName = "Soyad", IsActive = true }).ToArray();
            db.Students.AddRange(students);
            db.Parents.AddRange(
                new Parent { StudentId = students[0].Id, Name = "Veli 1", NormalizedPhone = "+905321112233", IsPrimary = true },
                new Parent { StudentId = students[0].Id, Name = "Eski", NormalizedPhone = "+905009999999", IsPrimary = false },
                new Parent { StudentId = students[1].Id, Name = "Veli 2", NormalizedPhone = "0532 111 22 33", IsPrimary = true });
            await db.SaveChangesAsync();
            return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
