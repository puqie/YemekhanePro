using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Persistence;

/// <summary>
/// Bu testler <see cref="SqliteConnection.ClearAllPools"/> çağırır; bu işlem process genelinde
/// tüm SQLite bağlantı havuzlarını etkilediğinden testler paralel koleksiyonlardan ayrı tutulur.
/// Aksi hâlde eşzamanlılık testi, başka bir sınıfın havuz temizliği yüzünden hatalı şekilde kırılır.
/// </summary>
[Collection(LocalDatabaseTests.CollectionName)]
public sealed class LocalDatabaseTests
{
    public const string CollectionName = "LocalDatabase";

    [Fact]
    public void DefaultConnectionUsesOverriddenApplicationDataDirectory()
    {
        using var database = new TemporaryDatabase();

        var connectionString = LocalDatabaseConnection.Resolve(null, database.DirectoryPath);
        var builder = new SqliteConnectionStringBuilder(connectionString);

        Assert.Equal(Path.Combine(database.DirectoryPath, "yemekhane.db"), builder.DataSource);
        Assert.True(builder.ForeignKeys);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, builder.Mode);
    }

    [Fact]
    public async Task InitializationMigratesAndConfiguresWalAndForeignKeys()
    {
        using var database = new TemporaryDatabase();
        await using var services = CreateServices(database.ConnectionString);

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
        var context = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();

        Assert.True(result.IsHealthy);
        Assert.Equal("ok", result.IntegrityResult, ignoreCase: true);
        Assert.Equal("wal", result.JournalMode, ignoreCase: true);
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal("1", await ReadPragmaAsync(context, "foreign_keys"));
    }

    [Fact]
    public async Task DataPersistsAfterDatabaseIsReopened()
    {
        using var database = new TemporaryDatabase();
        var studentId = Guid.NewGuid();

        await using (var services = CreateServices(database.ConnectionString))
        {
            await InitializeAsync(services);
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            context.Students.Add(new Student
            {
                Id = studentId,
                StudentNo = "offline-001",
                FirstName = "Offline",
                LastName = "Student"
            });
            await context.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();
        await using var reopenedServices = CreateServices(database.ConnectionString);
        await InitializeAsync(reopenedServices);
        await using var reopenedScope = reopenedServices.CreateAsyncScope();
        var reopenedContext = reopenedScope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        Assert.True(await reopenedContext.Students.IgnoreQueryFilters().AnyAsync(x => x.Id == studentId));
    }

    [Fact]
    public async Task ForeignKeyViolationsAreRejected()
    {
        using var database = new TemporaryDatabase();
        await using var services = CreateServices(database.ConnectionString);
        await InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        context.StudentCards.Add(new StudentCard
        {
            StudentId = Guid.NewGuid(),
            CardNumber = "orphan-card",
            ValidFrom = DateTimeOffset.UtcNow
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public async Task ConcurrentWriterWaitsForLockAndThenPersists()
    {
        using var database = new TemporaryDatabase();
        await using var services = CreateServices(database.ConnectionString, busyTimeoutSeconds: 3);
        await InitializeAsync(services);
        await using var firstScope = services.CreateAsyncScope();
        await using var secondScope = services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();

        await using var transaction = await first.Database.BeginTransactionAsync();
        first.Students.Add(CreateStudent("concurrent-1"));
        await first.SaveChangesAsync();

        second.Students.Add(CreateStudent("concurrent-2"));
        var secondWrite = Task.Run(() => second.SaveChangesAsync());
        await Task.Delay(150);
        Assert.False(secondWrite.IsCompleted);
        await transaction.CommitAsync();
        await secondWrite;

        await using var verificationScope = services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        Assert.Equal(2, await verification.Students.CountAsync(x => x.StudentNo.StartsWith("concurrent-")));
    }

    private static ServiceProvider CreateServices(string connectionString, int busyTimeoutSeconds = 1)
    {
        var services = new ServiceCollection();
        services.AddYemekhaneInfrastructure(connectionString, busyTimeoutSeconds);
        return services.BuildServiceProvider();
    }

    private static async Task InitializeAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
    }

    private static Student CreateStudent(string number) => new()
    {
        StudentNo = number,
        FirstName = "Concurrent",
        LastName = "Student"
    };

    private static async Task<string> ReadPragmaAsync(YemekhaneDbContext context, string pragma)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "Yemekhane.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            ConnectionString = LocalDatabaseConnection.Resolve(null, DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string ConnectionString { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(DirectoryPath)) return;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }
}
