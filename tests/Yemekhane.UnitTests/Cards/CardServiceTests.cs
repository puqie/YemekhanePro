using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Cards;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Cards;

public sealed class CardServiceTests
{
    /// <summary>
    /// Kartin ON yuzundeki basili numara (6296) cipin numarasi degildir ama kayip kart bulununca
    /// sahibini bulmak icin aranir: kart bulma, ogrenci arama ve kart filtresi bununla da eslesir;
    /// kart degistirmeden guncellenebilir.
    /// </summary>
    [Fact]
    public async Task PrintedNumberIsStoredSearchableAndUpdatable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "111");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);

        var card = await service.AssignAsync(student.Id, new AssignCardRequest("8247129", " 6296 "));
        Assert.Equal("6296", card.PrintedNumber);
        Assert.Equal(card.Id, (await service.FindAsync("6296")).Id);
        Assert.Equal(card.Id, (await service.FindAsync("8247129")).Id);

        var students = new Yemekhane.Infrastructure.Students.EfStudentRepository(context);
        Assert.Single((await students.SearchAsync(new Yemekhane.Application.Students.StudentQuery(Search: "6296"), default)).Items);
        Assert.Single((await students.SearchAsync(new Yemekhane.Application.Students.StudentQuery(CardNumber: "6296"), default)).Items);
        Assert.Equal("6296", (await students.SearchAsync(new Yemekhane.Application.Students.StudentQuery(Search: "111"), default)).Items.Single().PrintedNumber);

        var updated = await service.SetPrintedNumberAsync(student.Id, new SetPrintedNumberRequest("6300"));
        Assert.Equal("6300", updated.PrintedNumber);
        Assert.Null((await service.SetPrintedNumberAsync(student.Id, new SetPrintedNumberRequest("  "))).PrintedNumber);
        await Assert.ThrowsAsync<RequestValidationException>(
            () => service.SetPrintedNumberAsync(student.Id, new SetPrintedNumberRequest(new string('9', 33))));
    }

    [Fact]
    public async Task ReplacementPreservesHistoryAndActivatesOnlyNewCard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);

        var oldCard = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        var newCard = await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222705", "Kart hasarlı"));
        var history = await service.GetHistoryAsync(student.Id);

        Assert.Equal(2, history.Count);
        Assert.False(history.Single(x => x.Id == oldCard.Id).IsActive);
        Assert.Equal("Kart hasarlı", history.Single(x => x.Id == oldCard.Id).ReplacementReason);
        Assert.True(history.Single(x => x.Id == newCard.Id).IsActive);
    }

    [Fact]
    public async Task DuplicateReplacementRollsBackAndKeepsOldCardActive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var first = await AddStudent(context, "6811");
        var second = await AddStudent(context, "6812");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var firstCard = await service.AssignAsync(first.Id, new AssignCardRequest("8222704"));
        await service.AssignAsync(second.Id, new AssignCardRequest("8222705"));

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.ReplaceAsync(first.Id, new ReplaceCardRequest("8222705", "Değişim")));

        Assert.True((await service.FindAsync(firstCard.CardNumber)).IsActive);
    }

    /// <summary>
    /// SAHA: kart yanlislikla degistirilip pasife dusunce turnike "Kart pasif" diyordu ve
    /// programda geri acacak yer yoktu; numara tekil oldugu icin yeniden de atanamiyordu.
    /// Geri acma: EN SON pasife dusen kart aktif olur, gecerlilik yeniden baslar, degistirme
    /// nedeni silinir ve kart cihaz kuyruguna (UPDATE_CARD) yeniden girer.
    /// </summary>
    [Fact]
    public async Task ReactivateBringsBackTheLatestPassiveCardAndQueuesItForDevices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var first = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        var second = await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222705", "Kart hasarlı"));
        await service.DeactivateAsync(second.Id, "Yanlışlıkla");

        var reactivated = await service.ReactivateAsync(student.Id);

        Assert.Equal(second.Id, reactivated.Id);
        Assert.True(reactivated.IsActive);
        Assert.Null(reactivated.ValidTo);
        Assert.Null(reactivated.ReplacementReason);
        var history = await service.GetHistoryAsync(student.Id);
        Assert.Single(history, x => x.IsActive);
        Assert.False(history.Single(x => x.Id == first.Id).IsActive);
        // Degistirme (1) + pasiflestirme (1) + geri acma (1): cihaz kuyrugu uc kez bilgilendirilir.
        var entityId = second.Id.ToString("D");
        Assert.Equal(3, await context.SyncOperations.AsNoTracking()
            .CountAsync(x => x.EntityId == entityId && x.OperationType == "UPDATE_CARD"));
    }

    [Fact]
    public async Task ReactivateIsRejectedWhileAnActiveCardExists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));

        await Assert.ThrowsAsync<EntityConflictException>(() => service.ReactivateAsync(student.Id));
    }

    [Fact]
    public async Task ReactivateWithoutAPassiveCardIsNotFound()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.ReactivateAsync(student.Id));
    }

    /// <summary>Ayni ogrencinin pasif kart numarasi yeniden yazilinca kart geri acilir; 409 degil.</summary>
    [Fact]
    public async Task AssigningTheStudentsOwnPassiveNumberReactivatesIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var card = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        await service.DeactivateAsync(card.Id, "Test");

        var again = await service.AssignAsync(student.Id, new AssignCardRequest("8222704", "6296"));

        Assert.Equal(card.Id, again.Id);
        Assert.True(again.IsActive);
        Assert.Equal("6296", again.PrintedNumber);
        Assert.Single(await service.GetHistoryAsync(student.Id));
    }

    /// <summary>Baska ogrencinin karti -- pasif olsa bile -- verilemez; numara sistem genelinde tekildir.</summary>
    [Fact]
    public async Task AssigningAnotherStudentsPassiveNumberStillConflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var first = await AddStudent(context, "6811");
        var second = await AddStudent(context, "6812");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var card = await service.AssignAsync(first.Id, new AssignCardRequest("8222704"));
        await service.DeactivateAsync(card.Id, "Kayıp");

        await Assert.ThrowsAsync<EntityConflictException>(() => service.AssignAsync(second.Id, new AssignCardRequest("8222704")));
    }

    /// <summary>Eski karta geri donus: degistirmede onceki pasif kart yeniden aktif olur, simdiki pasife duser.</summary>
    [Fact]
    public async Task ReplacingBackToAnOldCardReactivatesItAndRetiresTheCurrentOne()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var a = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        var b = await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222705", "Kart hasarlı"));

        var back = await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222704", "Eski kart bulundu"));

        var history = await service.GetHistoryAsync(student.Id);
        Assert.Equal(a.Id, back.Id);
        Assert.Equal(2, history.Count);
        Assert.True(history.Single(x => x.Id == a.Id).IsActive);
        var retired = history.Single(x => x.Id == b.Id);
        Assert.False(retired.IsActive);
        Assert.Equal("Eski kart bulundu", retired.ReplacementReason);
    }
    /// <summary>Kartlar ekrani belirli bir karti (kimligiyle) geri acar: en son pasif olan degil, SECILEN.</summary>
    [Fact]
    public async Task ReactivateByIdBringsBackThatSpecificCard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var a = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        var b = await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222705", "Kart hasarlı"));
        await service.DeactivateAsync(b.Id, "Kayıp");

        // Iki pasif kart var; ESKI olan (a) secilir.
        var reactivated = await service.ReactivateCardAsync(a.Id);

        Assert.Equal(a.Id, reactivated.Id);
        Assert.True(reactivated.IsActive);
        var history = await service.GetHistoryAsync(student.Id);
        Assert.False(history.Single(x => x.Id == b.Id).IsActive);
    }

    [Fact]
    public async Task ReactivateByIdIsRejectedWhileTheStudentHasAnActiveCard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var a = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));
        await service.ReplaceAsync(student.Id, new ReplaceCardRequest("8222705", "Kart hasarlı"));

        await Assert.ThrowsAsync<EntityConflictException>(() => service.ReactivateCardAsync(a.Id));
    }

    [Fact]
    public async Task ReactivateByIdOnAnActiveOrUnknownCardIsNotFound()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = await AddStudent(context, "6811");
        var service = new CardService(new EfCardRepository(context), TimeProvider.System);
        var a = await service.AssignAsync(student.Id, new AssignCardRequest("8222704"));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.ReactivateCardAsync(a.Id));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.ReactivateCardAsync(Guid.NewGuid()));
    }
    private static async Task<Student> AddStudent(YemekhaneDbContext context, string studentNo)
    {
        var student = new Student { StudentNo = studentNo, FirstName = "Test", LastName = "Öğrenci" };
        context.Students.Add(student); await context.SaveChangesAsync(); return student;
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
