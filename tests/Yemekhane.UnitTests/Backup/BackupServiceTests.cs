using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Persistence;

namespace Yemekhane.UnitTests.Backup;

[Collection(LocalDatabaseTests.CollectionName)]
public sealed class BackupServiceTests
{
    [Fact]
    public async Task LiveSqliteBackupRestoresDeletedDataAndOmitsPlaintextSecrets()
    {
        using var fixture = await BackupFixture.CreateAsync(new Dictionary<string, string?>
        {
            ["SchoolName"] = "Test School",
            ["Authentication:Token"] = "plain-secret"
        });
        var studentId = await fixture.AddStudentAsync("backup-001");

        var backup = await fixture.Service.CreateAsync();
        await fixture.DeleteStudentAsync(studentId);
        Assert.False(await fixture.StudentExistsAsync(studentId));

        var restored = await fixture.Service.RestoreAsync(backup.ArchivePath);

        Assert.True(restored.Restored);
        Assert.True(restored.RestartRequired);
        Assert.True(await fixture.StudentExistsAsync(studentId));
        Assert.NotEqual(Guid.Empty, backup.BackupId);
        Assert.NotEqual(backup.Manifest.CreatedAtUtc.Offset, backup.Manifest.CreatedAtIstanbul.Offset);
        using var archive = ZipFile.OpenRead(backup.ArchivePath);
        Assert.Equal(["database.sqlite", "manifest.json", "settings.json"], archive.Entries.Select(x => x.FullName).Order());
        using var settingsReader = new StreamReader(archive.GetEntry("settings.json")!.Open());
        var settings = await settingsReader.ReadToEndAsync();
        Assert.Contains("Test School", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", settings, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.BackupDirectory, restored.SafetyBackupFileName)));
    }

    [Fact]
    public async Task CorruptedChecksumIsRejectedAndOriginalIsPreserved()
    {
        using var fixture = await BackupFixture.CreateAsync();
        var originalId = await fixture.AddStudentAsync("current-001");
        var backup = await fixture.Service.CreateAsync();
        var corrupted = fixture.NewPath("corrupted.zip");
        RewriteArchive(backup.ArchivePath, corrupted, (name, bytes) =>
            name == "database.sqlite" ? bytes.Select((value, index) => index == 0 ? (byte)(value ^ 0xff) : value).ToArray() : bytes);

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(corrupted));
        Assert.True(await fixture.StudentExistsAsync(originalId));
    }

    [Fact]
    public async Task InvalidZipIsRejected()
    {
        using var fixture = await BackupFixture.CreateAsync();
        var path = fixture.NewPath("invalid.zip");
        await File.WriteAllTextAsync(path, "not a zip");

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(path));
    }

    [Fact]
    public async Task IncompatibleManifestVersionIsRejected()
    {
        using var fixture = await BackupFixture.CreateAsync();
        var backup = await fixture.Service.CreateAsync();
        var incompatible = fixture.NewPath("incompatible.zip");
        RewriteArchive(backup.ArchivePath, incompatible, (name, bytes) =>
        {
            if (name != "manifest.json") return bytes;
            var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions)!;
            return JsonSerializer.SerializeToUtf8Bytes(manifest with { FormatVersion = 999 }, JsonOptions);
        });

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(incompatible));
    }

    [Fact]
    public async Task IncompatibleSchemaVersionIsRejected()
    {
        using var fixture = await BackupFixture.CreateAsync();
        var backup = await fixture.Service.CreateAsync();
        var incompatible = fixture.NewPath("incompatible-schema.zip");
        RewriteArchive(backup.ArchivePath, incompatible, (name, bytes) =>
        {
            if (name != "manifest.json") return bytes;
            var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions)!;
            return JsonSerializer.SerializeToUtf8Bytes(manifest with { SchemaVersion = "future-schema" }, JsonOptions);
        });

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(incompatible));
    }

    [Fact]
    public async Task InvalidDatabaseWithMatchingChecksumFailsBeforeOriginalIsReplaced()
    {
        using var fixture = await BackupFixture.CreateAsync();
        var currentId = await fixture.AddStudentAsync("preserved-001");
        var backup = await fixture.Service.CreateAsync();
        var invalidDatabase = fixture.NewPath("invalid-database.zip");
        byte[] brokenDatabase = [];
        RewriteArchive(backup.ArchivePath, invalidDatabase, (name, bytes) =>
        {
            if (name == "database.sqlite")
            {
                brokenDatabase = new byte[bytes.Length];
                RandomNumberGenerator.Fill(brokenDatabase);
                return brokenDatabase;
            }
            if (name != "manifest.json") return bytes;
            var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions)!;
            var files = manifest.Files.Select(x => x.Path == "database.sqlite"
                ? x with { Sha256 = Convert.ToHexString(SHA256.HashData(brokenDatabase)).ToLowerInvariant() }
                : x).ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(manifest with { Files = files }, JsonOptions);
        }, databaseFirst: true);

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(invalidDatabase));
        Assert.True(await fixture.StudentExistsAsync(currentId));
    }

    [Fact]
    public void RetentionKeepsNewestConfiguredNumber()
    {
        using var fixture = BackupFixture.CreateUninitialized(retentionCount: 2);
        Directory.CreateDirectory(fixture.BackupDirectory);
        var oldest = Path.Combine(fixture.BackupDirectory, "okulyemek-oldest.zip");
        var middle = Path.Combine(fixture.BackupDirectory, "okulyemek-middle.zip");
        var newest = Path.Combine(fixture.BackupDirectory, "okulyemek-newest.zip");
        File.WriteAllBytes(oldest, [1]);
        File.WriteAllBytes(middle, [1]);
        File.WriteAllBytes(newest, [1]);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(-1));

        fixture.Service.ApplyRetention();

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Theory]
    [InlineData(BackupScheduleFrequency.Daily, DayOfWeek.Sunday, "2026-09-01T02:00:00+03:00")]
    [InlineData(BackupScheduleFrequency.Weekly, DayOfWeek.Sunday, "2026-09-06T02:00:00+03:00")]
    public void ScheduleCalculatesNextIstanbulRun(BackupScheduleFrequency frequency, DayOfWeek day, string expected)
    {
        var options = new BackupOptions { Schedule = frequency, WeeklyDay = day, Time = new TimeOnly(2, 0) };
        var zone = GetIstanbulTimeZone();
        var now = DateTimeOffset.Parse("2026-08-31T04:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture);

        var next = BackupSchedule.GetNextRun(now, options, zone);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), next);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static void RewriteArchive(string sourcePath, string targetPath, Func<string, byte[], byte[]> transform, bool databaseFirst = false)
    {
        using var source = ZipFile.OpenRead(sourcePath);
        using var target = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        IEnumerable<ZipArchiveEntry> entries = databaseFirst
            ? source.Entries.OrderBy(x => x.FullName == "database.sqlite" ? 0 : x.FullName == "manifest.json" ? 2 : 1)
            : source.Entries;
        foreach (var entry in entries)
        {
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = transform(entry.FullName, memory.ToArray());
            using var output = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
            output.Write(bytes);
        }
    }

    private static TimeZoneInfo GetIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }

    private sealed class BackupFixture : IDisposable
    {
        private BackupFixture(string root, int retentionCount, IReadOnlyDictionary<string, string?>? settings)
        {
            Root = root;
            Directory.CreateDirectory(root);
            DatabaseDirectory = Path.Combine(root, "data");
            BackupDirectory = Path.Combine(root, "backups");
            Directory.CreateDirectory(DatabaseDirectory);
            ConnectionString = LocalDatabaseConnection.Resolve(null, DatabaseDirectory);
            Service = new BackupService(ConnectionString, new BackupOptions
            {
                Directory = BackupDirectory,
                RetentionCount = retentionCount,
                MaximumArchiveBytes = 64 * 1024 * 1024,
                MaximumExtractedBytes = 128 * 1024 * 1024
            }, settings);
        }

        public string Root { get; }
        public string DatabaseDirectory { get; }
        public string BackupDirectory { get; }
        public string ConnectionString { get; }
        public BackupService Service { get; }

        public static BackupFixture CreateUninitialized(int retentionCount = 10, IReadOnlyDictionary<string, string?>? settings = null) =>
            new(Path.Combine(Path.GetTempPath(), "Yemekhane.BackupTests", Guid.NewGuid().ToString("N")), retentionCount, settings);

        public static async Task<BackupFixture> CreateAsync(IReadOnlyDictionary<string, string?>? settings = null)
        {
            var fixture = CreateUninitialized(settings: settings);
            var services = new ServiceCollection().AddYemekhaneInfrastructure(fixture.ConnectionString).BuildServiceProvider();
            await using (services)
            await using (var scope = services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
            SqliteConnection.ClearAllPools();
            return fixture;
        }

        public string NewPath(string name) => Path.Combine(Root, name);

        public async Task<Guid> AddStudentAsync(string number)
        {
            var id = Guid.NewGuid();
            await using var context = CreateContext();
            context.Students.Add(new Student { Id = id, StudentNo = number, FirstName = "Backup", LastName = "Test" });
            await context.SaveChangesAsync();
            return id;
        }

        public async Task DeleteStudentAsync(Guid id)
        {
            await using var context = CreateContext();
            var student = await context.Students.SingleAsync(x => x.Id == id);
            context.Students.Remove(student);
            await context.SaveChangesAsync();
        }

        public async Task<bool> StudentExistsAsync(Guid id)
        {
            await using var context = CreateContext();
            return await context.Students.IgnoreQueryFilters().AnyAsync(x => x.Id == id);
        }

        private YemekhaneDbContext CreateContext() => new(new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite(ConnectionString).Options);

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
