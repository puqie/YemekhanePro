using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Income;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Income;

public sealed class IncomeServiceTests
{
    private static readonly Guid ActorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task TypeCrudPersistsAndDeactivateIsSoft()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var created = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest(" Kart Yenileme "), ActorId);
        var updated = await fixture.Service.UpdateTypeAsync(created.Id, new SaveIncomeTypeRequest("Kart Bedeli"), ActorId);
        await fixture.Service.DeactivateTypeAsync(created.Id, ActorId);

        Assert.Equal("Kart Bedeli", updated.Name);
        Assert.Empty(await fixture.Service.ListTypesAsync());
        Assert.False(Assert.Single(await fixture.Service.ListTypesAsync(true)).IsActive);
    }

    [Fact]
    public async Task DuplicateTypeNameIsRejectedIgnoringCase()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Nakit"), ActorId);

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("nakit"), ActorId));
    }

    [Fact]
    public async Task PaymentTransactionValidatesStudentAndIsAudited()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var type = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Kart Yenileme"), ActorId);
        var student = new Student { StudentNo = "1001", FirstName = "Ece", LastName = "Kaya" };
        fixture.Context.Students.Add(student);
        await fixture.Context.SaveChangesAsync();

        var item = await fixture.Service.RecordAsync(new CreateIncomeTransactionRequest(
            Guid.NewGuid(), student.Id, " 9911 ", new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.FromHours(3)),
            type.Id, 125.50m, " Kart bedeli "), ActorId);

        Assert.Equal(125.50m, item.Amount);
        Assert.Equal("9911", item.CardNumber);
        Assert.Equal("Ece Kaya", item.StudentName);
        Assert.Equal(ActorId, item.CreatedBy);
        Assert.Contains(await fixture.Context.Set<AuditLog>().ToListAsync(),
            x => x.EntityName == nameof(IncomeTransaction) && x.EntityId == item.Id.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.001")]
    public async Task InvalidAmountIsRejected(string amount)
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var type = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Nakit"), ActorId);

        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.RecordAsync(
            new CreateIncomeTransactionRequest(Guid.NewGuid(), null, null, DateTimeOffset.UtcNow,
                type.Id, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)), ActorId));
    }

    [Fact]
    public async Task OperationIdMakesPaymentIdempotent()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var type = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Havale"), ActorId);
        var operationId = Guid.NewGuid();
        var request = new CreateIncomeTransactionRequest(operationId, null, null, DateTimeOffset.UtcNow, type.Id, 40m);

        var first = await fixture.Service.RecordAsync(request, ActorId);
        var second = await fixture.Service.RecordAsync(request, ActorId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await fixture.Context.Set<IncomeTransaction>().CountAsync());
    }

    [Fact]
    public async Task VoidPreservesOriginalAmountAndStoresReason()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var type = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Nakit"), ActorId);
        var item = await fixture.Service.RecordAsync(new CreateIncomeTransactionRequest(
            Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, type.Id, 80m), ActorId);

        var voided = await fixture.Service.VoidAsync(item.Id, " Hatalı tahsilat ", ActorId);

        Assert.True(voided.IsVoided);
        Assert.Equal(80m, voided.Amount);
        Assert.Equal("Hatalı tahsilat", voided.VoidReason);
        Assert.Equal(ActorId, voided.VoidedBy);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.VoidAsync(item.Id, "Tekrar", ActorId));
    }

    [Fact]
    public async Task ListAppliesDateTypeStudentAndPaginationFilters()
    {
        await using var fixture = await IncomeFixture.CreateAsync();
        var cash = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Nakit"), ActorId);
        var transfer = await fixture.Service.CreateTypeAsync(new SaveIncomeTypeRequest("Havale"), ActorId);
        var student = new Student { StudentNo = "1002", FirstName = "Can", LastName = "Ak" };
        fixture.Context.Students.Add(student); await fixture.Context.SaveChangesAsync();
        var firstDate = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.FromHours(3));
        await fixture.Service.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student.Id, null, firstDate, cash.Id, 10m), ActorId);
        await fixture.Service.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), student.Id, null, firstDate.AddDays(1), cash.Id, 20m), ActorId);
        await fixture.Service.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), null, null, firstDate.AddDays(2), transfer.Id, 30m), ActorId);

        var filtered = await fixture.Service.ListAsync(new IncomeTransactionFilter(
            firstDate, firstDate.AddDays(1), cash.Id, student.Id, Page: 2, PageSize: 1));

        Assert.Equal(2, filtered.TotalCount);
        Assert.Single(filtered.Items);
        Assert.Equal(10m, filtered.Items[0].Amount);
    }

    private sealed class IncomeFixture(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        public YemekhaneDbContext Context { get; } = context;
        public IncomeService Service { get; } = new(new EfIncomeRepository(context, TimeProvider.System));

        public static async Task<IncomeFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>()
                .UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            return new IncomeFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
