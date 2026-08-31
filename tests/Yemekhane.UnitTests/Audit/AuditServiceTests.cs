using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Audit;

public sealed class AuditServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task RecordRedactsSensitiveFieldsAndTruncatesLargePayload()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.Service.Record(new AuditEntry("Updated", "Student", "1", "Öğrenci güncellendi.", After: new
        {
            NationalId = "12345678901", Phone = "05550000000", Address = "Ev", PasswordHash = "hash",
            Token = "token", DeviceKey = "key", Safe = "visible"
        }));
        fixture.Service.Record(new AuditEntry("Bulk", "Student", "2", "Toplu işlem.", After: new { Data = new string('x', 20_000) }));
        await fixture.Context.SaveChangesAsync();

        var rows = await fixture.Context.AuditLogs.ToListAsync();
        var updated = rows.Single(x => x.Action == "Updated");
        var bulk = rows.Single(x => x.Action == "Bulk");
        Assert.Contains("[REDACTED]", updated.AfterJson);
        Assert.DoesNotContain("12345678901", updated.AfterJson);
        Assert.Contains("visible", updated.AfterJson);
        Assert.Contains("sha256", bulk.AfterJson);
        Assert.Contains("truncated", bulk.AfterJson);
    }

    [Fact]
    public async Task AuditRowsCannotBeUpdatedOrDeleted()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.Service.Record(new AuditEntry("Created", "Student", "1", "Öğrenci oluşturuldu."));
        await fixture.Context.SaveChangesAsync();
        var row = await fixture.Context.AuditLogs.SingleAsync();
        row.Description = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());

        fixture.Context.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Context.AuditLogs.ExecuteDeleteAsync());
    }

    [Fact]
    public async Task FiltersAndPaginationAreAppliedServerSide()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var bulkId = Guid.NewGuid();
        fixture.Service.Record(new AuditEntry("Created", "Student", "1", "Birinci.", BulkOperationId: bulkId));
        fixture.Service.Record(new AuditEntry("Updated", "Card", "2", "İkinci."));
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.ListAsync(new AuditLogFilter(UserId: UserId, Action: "Created",
            Entity: "Student", BulkOperationId: bulkId, CorrelationId: "corr", PageSize: 1));

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Student", result.Items[0].EntityName);
    }

    [Fact]
    public async Task RolledBackTransactionDoesNotLeaveOrphanAudit()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await using (var transaction = await fixture.Context.Database.BeginTransactionAsync())
        {
            fixture.Context.Students.Add(new Student { StudentNo = "1", FirstName = "A", LastName = "B" });
            fixture.Service.Record(new AuditEntry("Created", "Student", "1", "Öğrenci oluşturuldu."));
            await fixture.Context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.AuditLogs.ToListAsync());
        Assert.Empty(await fixture.Context.Students.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentContextsAppendWithoutOverwritingEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Default Timeout=15;Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options;
            await using (var setup = new YemekhaneDbContext(options)) await setup.Database.MigrateAsync();

            async Task AppendAsync(string id)
            {
                await using var context = new YemekhaneDbContext(options);
                var service = new AuditService(new EfAuditRepository(context, TimeProvider.System), new TestContext());
                service.Record(new AuditEntry("Concurrent", "Student", id, "Eş zamanlı kayıt."));
                await context.SaveChangesAsync();
            }

            await Task.WhenAll(AppendAsync("1"), AppendAsync("2"));
            await using var verification = new YemekhaneDbContext(options);
            Assert.Equal(2, await verification.AuditLogs.CountAsync(x => x.Action == "Concurrent"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class AuditFixture(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        public YemekhaneDbContext Context { get; } = context;
        public AuditService Service { get; } = new(new EfAuditRepository(context, TimeProvider.System), new TestContext());

        public static async Task<AuditFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            return new AuditFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestContext : IAuditContext
    {
        public Guid? UserId => AuditServiceTests.UserId;
        public string? CorrelationId => "corr";
    }
}
