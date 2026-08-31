using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

public sealed class StudentServiceTests
{
    [Fact]
    public async Task CrudSearchAndSoftDeleteWorkEndToEnd()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var service = new StudentService(new EfStudentRepository(context));

        var created = await service.CreateAsync(new SaveStudentRequest(" 6811 ", " Ahmet ", " Yılmaz ", NationalId: "12345678901"));
        var search = await service.SearchAsync(new StudentQuery(StudentNo: "6811"));
        var updated = await service.UpdateAsync(created.Id, new SaveStudentRequest("6811", "Ahmet", "Altay", IsActive: true));
        await service.DeactivateAsync(created.Id);

        Assert.Equal("6811", created.StudentNo);
        Assert.Single(search.Items);
        Assert.Equal("Altay", updated.LastName);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.GetAsync(created.Id));
        var audits = await context.AuditLogs.Where(x => x.EntityId == created.Id.ToString()).ToListAsync();
        Assert.Equal(["StudentCreated", "StudentDeactivated", "StudentUpdated"], audits.Select(x => x.Action).Order());
        Assert.All(audits, x => Assert.DoesNotContain("12345678901", x.AfterJson ?? string.Empty));
    }

    [Fact]
    public async Task DuplicateStudentNumberIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var service = new StudentService(new EfStudentRepository(context));
        var request = new SaveStudentRequest("6811", "Ayşe", "Yılmaz");
        await service.CreateAsync(request);

        await Assert.ThrowsAsync<EntityConflictException>(() => service.CreateAsync(request));
    }

    [Fact]
    public async Task SearchRejectsSingleCharacterAndOversizedPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        var service = new StudentService(new EfStudentRepository(context));

        await Assert.ThrowsAsync<RequestValidationException>(() => service.SearchAsync(new StudentQuery(Search: "A")));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.SearchAsync(new StudentQuery(PageSize: 201)));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
