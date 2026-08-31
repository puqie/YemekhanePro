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

    private static async Task<Student> AddStudent(YemekhaneDbContext context, string studentNo)
    {
        var student = new Student { StudentNo = studentNo, FirstName = "Test", LastName = "Öğrenci" };
        context.Students.Add(student); await context.SaveChangesAsync(); return student;
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
