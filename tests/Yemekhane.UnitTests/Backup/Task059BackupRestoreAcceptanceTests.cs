using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Persistence;

namespace Yemekhane.UnitTests.Backup;

[Collection(LocalDatabaseTests.CollectionName)]
[Trait("Category", "Task059")]
public sealed class Task059BackupRestoreAcceptanceTests(ITestOutputHelper output)
{
    private static readonly string[] CriticalTables =
    [
        "students", "student_cards", "parents", "classes", "meal_entitlements", "meal_usage",
        "access_logs", "devices", "device_events", "turnstile_events", "holidays", "meal_transfers",
        "income_types", "income_transactions", "sms_templates", "sms_logs", "users", "roles",
        "permissions", "user_roles", "role_permissions", "audit_logs", "sync_operations",
        "system_settings", "notifications", "notification_receipts", "__EFMigrationsHistory"
    ];

    [Fact]
    public async Task RealFileWalBackupCanRestoreDeletedDatabaseAndAllCriticalData()
    {
        using var fixture = await AcceptanceFixture.CreateAsync(settings: new Dictionary<string, string?>
        {
            ["SchoolName"] = "TASK059 School",
            ["Authentication:Token"] = "external-plain-secret"
        });
        var ids = await SeedCriticalDataAsync(fixture.ConnectionString);
        var writer = StartConcurrentWalWriter(fixture.ConnectionString);
        await writer.Started;

        var backupTimer = Stopwatch.StartNew();
        var backup = await fixture.Service.CreateAsync();
        backupTimer.Stop();
        await writer.StopAsync();
        var expected = await ReadArchiveCountsAsync(backup.ArchivePath, fixture.NewPath("snapshot.sqlite"));

        using (var archive = ZipFile.OpenRead(backup.ArchivePath))
        using (var settingsReader = new StreamReader(archive.GetEntry("settings.json")!.Open()))
        {
            var settingsJson = await settingsReader.ReadToEndAsync();
            Assert.Contains("TASK059 School", settingsJson, StringComparison.Ordinal);
            Assert.Contains("PublicSetting", settingsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("external-plain-secret", settingsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("DPAPI:protected-database-secret", settingsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("HiddenByFlag", settingsJson, StringComparison.Ordinal);
        }

        SqliteConnection.ClearAllPools();
        fixture.DeleteDatabaseAndSidecars();
        var restoreTimer = Stopwatch.StartNew();
        var restored = await fixture.Service.RestoreAsync(backup.ArchivePath);
        restoreTimer.Stop();
        Assert.Equal(string.Empty, restored.SafetyBackupFileName);
        await fixture.ReopenAndMigrateAsync();

        var actual = await ReadCountsAsync(fixture.ConnectionString);
        Assert.Equal(expected.OrderBy(x => x.Key), actual.OrderBy(x => x.Key));
        await AssertDatabaseChecksAsync(fixture.ConnectionString);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("TASK059", (await db.Students.IgnoreQueryFilters().SingleAsync(x => x.Id == ids.StudentId)).StudentNo);
            Assert.Equal("DPAPI:protected-database-secret", (await db.Set<SystemSetting>().SingleAsync(x => x.Id == ids.SecretSettingId)).Value);
            Assert.Equal("PBKDF2:not-a-plaintext-password", (await db.Users.SingleAsync(x => x.Id == ids.UserId)).PasswordHash);
            var audit = await db.AuditLogs.SingleAsync(x => x.Id == ids.AuditId);
            audit.Description = "mutated";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        var archiveBytes = new FileInfo(backup.ArchivePath).Length;
        output.WriteLine($"TASK059 backup: {backupTimer.Elapsed.TotalMilliseconds:F0} ms, archive: {archiveBytes / 1024d:F1} KiB");
        output.WriteLine($"TASK059 restore+verify: {restoreTimer.Elapsed.TotalMilliseconds:F0} ms, critical tables: {CriticalTables.Length}");
    }

    [Fact]
    public async Task CorruptSourceCanBeReplacedButTamperedAndNewerBackupsCannot()
    {
        using var fixture = await AcceptanceFixture.CreateAsync();
        var id = await fixture.AddStudentAsync("SOURCE-CORRUPT");
        var backup = await fixture.Service.CreateAsync();
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(fixture.DatabasePath, RandomNumberGenerator.GetBytes(4096));

        var restored = await fixture.Service.RestoreAsync(backup.ArchivePath);

        Assert.Equal(string.Empty, restored.SafetyBackupFileName);
        Assert.True(await fixture.StudentExistsAsync(id));

        var tampered = fixture.NewPath("tampered.zip");
        RewriteArchive(backup.ArchivePath, tampered, (name, bytes) =>
            name == "database.sqlite" ? bytes.Select((x, i) => i == bytes.Length / 2 ? (byte)(x ^ 0x5a) : x).ToArray() : bytes);
        var currentId = await fixture.AddStudentAsync("PRESERVE-TAMPER");
        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(tampered));
        Assert.True(await fixture.StudentExistsAsync(currentId));

        var newer = await CreateNewerSchemaArchiveAsync(backup.ArchivePath, fixture.NewPath("newer.zip"), fixture.Root);
        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(newer));
        Assert.True(await fixture.StudentExistsAsync(currentId));
    }

    [Fact]
    public async Task InjectedMidRestoreFailureRollsBackAndKeepsSafetyBackup()
    {
        var fail = false;
        using var fixture = await AcceptanceFixture.CreateAsync(hook: (stage, _) =>
        {
            if (fail && stage == BackupRestoreStage.AfterDatabaseReplacement)
                throw new IOException("TASK059 injected replacement failure");
            return Task.CompletedTask;
        });
        var backedUpId = await fixture.AddStudentAsync("BEFORE-BACKUP");
        var backup = await fixture.Service.CreateAsync();
        var originalId = await fixture.AddStudentAsync("ORIGINAL-MUST-SURVIVE");
        fail = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(backup.ArchivePath));

        Assert.True(await fixture.StudentExistsAsync(backedUpId));
        Assert.True(await fixture.StudentExistsAsync(originalId));
        Assert.NotEmpty(Directory.GetFiles(fixture.BackupDirectory, "okulyemek-pre-restore-*.zip"));
        await AssertDatabaseChecksAsync(fixture.ConnectionString);
    }

    [Fact]
    public async Task RestoreMaintenanceLockSerializesConcurrentRestores()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        using var fixture = await AcceptanceFixture.CreateAsync(hook: async (stage, token) =>
        {
            if (stage != BackupRestoreStage.BeforeDatabaseReplacement) return;
            var entry = Interlocked.Increment(ref entries);
            if (entry == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
        });
        await fixture.AddStudentAsync("LOCKED-RESTORE");
        var backup = await fixture.Service.CreateAsync();

        var first = fixture.Service.RestoreAsync(backup.ArchivePath);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.Service.RestoreAsync(backup.ArchivePath);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref entries));
        Assert.False(second.IsCompleted);
        releaseFirst.TrySetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(2, entries);
    }

    [Fact]
    public async Task ZipSlipAndCompressionBombAreRejectedBeforeReplacement()
    {
        using var fixture = await AcceptanceFixture.CreateAsync();
        var id = await fixture.AddStudentAsync("ARCHIVE-GUARD");
        var zipSlip = fixture.NewPath("zip-slip.zip");
        using (var archive = ZipFile.Open(zipSlip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "database.sqlite", [1]);
            WriteEntry(archive, "settings.json", [1]);
            WriteEntry(archive, "manifest.json", [1]);
            WriteEntry(archive, "../escaped.txt", [1]);
        }
        var bomb = fixture.NewPath("bomb.zip");
        using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "database.sqlite", new byte[2 * 1024 * 1024]);
            WriteEntry(archive, "settings.json", [1]);
            WriteEntry(archive, "manifest.json", [1]);
        }

        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(zipSlip));
        await Assert.ThrowsAsync<BackupValidationException>(() => fixture.Service.RestoreAsync(bomb));
        Assert.True(await fixture.StudentExistsAsync(id));
    }

    [Fact]
    public async Task LargeArchiveValidationStreamsWithinMemoryBudget()
    {
        using var fixture = await AcceptanceFixture.CreateAsync(maximumBytes: 128L * 1024 * 1024);
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE large_fixture (id INTEGER PRIMARY KEY, payload BLOB NOT NULL); INSERT INTO large_fixture(payload) VALUES(randomblob(12582912));";
            await command.ExecuteNonQueryAsync();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryBefore = process.WorkingSet64;
        var timer = Stopwatch.StartNew();

        var backup = await fixture.Service.CreateAsync();
        await fixture.Service.ValidateAsync(backup.ArchivePath);

        timer.Stop();
        process.Refresh();
        var memoryDelta = Math.Max(0, process.WorkingSet64 - memoryBefore);
        var archiveSize = new FileInfo(backup.ArchivePath).Length;
        Assert.True(archiveSize > 8L * 1024 * 1024, $"Archive unexpectedly small: {archiveSize}");
        Assert.True(memoryDelta < 192L * 1024 * 1024, $"Working-set delta was {memoryDelta} bytes.");
        output.WriteLine($"TASK059 large streaming: {archiveSize / 1024d / 1024d:F2} MiB, {timer.Elapsed.TotalMilliseconds:F0} ms, working-set delta {memoryDelta / 1024d / 1024d:F2} MiB");
    }

    [Theory]
    [InlineData(BackupScheduleFrequency.Daily, DayOfWeek.Monday, "2026-09-01T02:00:00+03:00")]
    [InlineData(BackupScheduleFrequency.Weekly, DayOfWeek.Sunday, "2026-09-06T02:00:00+03:00")]
    public void DailyAndWeeklyAutomaticScheduleRemainDeterministic(BackupScheduleFrequency frequency, DayOfWeek day, string expected)
    {
        var options = new BackupOptions { ScheduleEnabled = true, Schedule = frequency, WeeklyDay = day, Time = new TimeOnly(2, 0) };
        var now = DateTimeOffset.Parse("2026-08-31T04:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            BackupSchedule.GetNextRun(now, options, IstanbulTimeZone));
    }

    [Fact]
    public async Task RetentionKeepsConfiguredRegularAndSafetyBackupsSeparately()
    {
        using var fixture = await AcceptanceFixture.CreateAsync(retentionCount: 2);
        await fixture.AddStudentAsync("RETENTION");
        for (var index = 0; index < 4; index++)
            await fixture.Service.CreateAsync();
        var restoreSource = Directory.GetFiles(fixture.BackupDirectory, "okulyemek-backup-*.zip").Order().Last();
        for (var index = 0; index < 4; index++)
            await fixture.Service.RestoreAsync(restoreSource);

        Assert.Equal(2, Directory.GetFiles(fixture.BackupDirectory, "okulyemek-backup-*.zip").Length);
        Assert.Equal(3, Directory.GetFiles(fixture.BackupDirectory, "okulyemek-pre-restore-*.zip").Length);
    }

    private static async Task<SeedIds> SeedCriticalDataAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        var user = new User { Username = "task059", NormalizedUsername = "TASK059", PasswordHash = "PBKDF2:not-a-plaintext-password" };
        var role = new Role { Name = "Backup Operator", NormalizedName = "BACKUP OPERATOR" };
        var permission = new PermissionDefinition { Code = "backups.manage", Name = "Manage backups" };
        var schoolClass = new SchoolClass { Name = "TASK059 Class" };
        var student = new Student { StudentNo = "TASK059", FirstName = "Backup", LastName = "Acceptance", ClassId = schoolClass.Id };
        var mealType = new MealType { Name = "TASK059 Lunch" };
        var device = new Device { Name = "TASK059 Device", DeviceType = "Turnstile", ConnectionType = "Simulator", Direction = "Entry", ConnectionStatus = "Online" };
        var incomeType = new IncomeType { Name = "TASK059 Income" };
        db.AddRange(user, role, permission, schoolClass, student, mealType, device, incomeType);
        await db.SaveChangesAsync();

        var entitlement = new MealEntitlement { StudentId = student.Id, MealTypeId = mealType.Id, EntitlementDate = new DateOnly(2026, 8, 31), Quantity = 2, ConsumedQuantity = 1, Status = "Active", Source = "TASK059" };
        var access = new AccessLog { Timestamp = DateTimeOffset.UtcNow, StudentId = student.Id, DeviceId = device.Id, MealTypeId = mealType.Id, CardNumber = "CARD-TASK059", Decision = "ALLOW", Reason = "Entitled", Direction = "Entry", ReaderSource = "Acceptance", OperationId = Guid.NewGuid() };
        var template = new SmsTemplate { Name = "TASK059 Template", Body = "Acceptance message" };
        var audit = new AuditLog { UserId = user.Id, Timestamp = DateTimeOffset.UtcNow, Action = "Seed", EntityName = "Acceptance", EntityId = student.Id.ToString(), Description = "Immutable TASK059 audit", AffectedRecords = 1 };
        var notification = new Notification { Severity = "Info", Type = "TASK059", Title = "Backup", Message = "Acceptance", Timestamp = DateTimeOffset.UtcNow, LatestAt = DateTimeOffset.UtcNow, RetainUntil = DateTimeOffset.UtcNow.AddDays(30), AudienceUserId = user.Id };
        var secretSetting = new SystemSetting { Key = "HiddenByFlag", Value = "DPAPI:protected-database-secret", IsSecret = true };
        db.AddRange(
            new StudentCard { StudentId = student.Id, CardNumber = "CARD-TASK059", ValidFrom = DateTimeOffset.UtcNow },
            new Parent { StudentId = student.Id, Name = "TASK059 Parent", NormalizedPhone = "+905551110059" }, entitlement, access,
            new DeviceEvent { DeviceId = device.Id, Timestamp = DateTimeOffset.UtcNow, EventType = "Connected", Severity = "Info", Message = "Acceptance" },
            new TurnstileEvent { DeviceId = device.Id, AccessLogId = access.Id, Timestamp = DateTimeOffset.UtcNow, Command = "Open", Result = "Success" },
            new Holiday { Date = new DateOnly(2026, 9, 1), Name = "TASK059 Holiday", HolidayType = "School", TransferBehavior = "NextDay" },
            new IncomeTransaction { OperationId = Guid.NewGuid(), StudentId = student.Id, IncomeTypeId = incomeType.Id, CardNumber = "CARD-TASK059", TransactionAt = DateTimeOffset.UtcNow, Amount = 59m, CreatedBy = user.Id },
            template, audit, notification, secretSetting,
            new SystemSetting { Key = "PublicSetting", Value = "restored-public-value", IsSecret = false },
            new SyncOperation { OperationId = Guid.NewGuid(), EntityName = "Student", EntityId = student.Id.ToString(), OperationType = "UpdateStudent", Timestamp = DateTimeOffset.UtcNow, DeviceId = "TASK059", Payload = "{}", SyncStatus = "Pending" });
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new RolePermissionAssignment { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        db.AddRange(
            new MealUsage { EntitlementId = entitlement.Id, StudentId = student.Id, MealTypeId = mealType.Id, AccessLogId = access.Id, UsedAt = DateTimeOffset.UtcNow },
            new MealTransfer { StudentId = student.Id, MealTypeId = mealType.Id, SourceEntitlementId = entitlement.Id, OriginalDate = new DateOnly(2026, 8, 31), TargetDate = new DateOnly(2026, 9, 2), Quantity = 1, Reason = "Holiday", CreatedBy = user.Id },
            new SmsLog { StudentId = student.Id, TemplateId = template.Id, Phone = "+905551110059", Message = "Acceptance message", Status = "Queued", IdempotencyKey = "task059-sms" },
            new NotificationReceipt { NotificationId = notification.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        return new SeedIds(student.Id, user.Id, audit.Id, secretSetting.Id);
    }

    private static WalWriter StartConcurrentWalWriter(string connectionString)
    {
        var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            var index = 0;
            while (!cancellation.IsCancellationRequested)
            {
                await using var db = CreateContext(connectionString);
                db.Students.Add(new Student { StudentNo = $"WAL-{index++:D5}", FirstName = "Concurrent", LastName = "Writer" });
                await db.SaveChangesAsync(cancellation.Token);
                started.TrySetResult();
                await Task.Delay(5, cancellation.Token);
            }
        }, cancellation.Token);
        return new WalWriter(cancellation, started.Task, task);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadArchiveCountsAsync(string archivePath, string targetPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        await using (var input = archive.GetEntry("database.sqlite")!.Open())
        await using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(target);
        return await ReadCountsAsync(new SqliteConnectionStringBuilder { DataSource = targetPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadCountsAsync(string connectionString)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        foreach (var table in CriticalTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
            result[table] = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static async Task AssertDatabaseChecksAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await command.ExecuteScalarAsync());
    }

    private static async Task<string> CreateNewerSchemaArchiveAsync(string source, string target, string directory)
    {
        var databasePath = Path.Combine(directory, "newer.sqlite");
        BackupManifest manifest;
        byte[] settings;
        using (var archive = ZipFile.OpenRead(source))
        {
            await using (var input = archive.GetEntry("database.sqlite")!.Open())
            await using (var output = File.Create(databasePath))
                await input.CopyToAsync(output);
            await using var settingsStream = archive.GetEntry("settings.json")!.Open();
            using var memory = new MemoryStream();
            await settingsStream.CopyToAsync(memory);
            settings = memory.ToArray();
            await using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions))!;
        }
        const string newerId = "99999999999999_Task999Future";
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES($id, '99.0.0');";
            command.Parameters.AddWithValue("$id", newerId);
            await command.ExecuteNonQueryAsync();
        }
        var database = await File.ReadAllBytesAsync(databasePath);
        var files = manifest.Files.Select(file => file.Path == "database.sqlite"
            ? file with { Size = database.LongLength, Sha256 = Sha256(database) }
            : file).ToArray();
        manifest = manifest with { SchemaVersion = newerId, Files = files };
        using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "database.sqlite", database);
            WriteEntry(archive, "settings.json", settings);
            WriteEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        }
        return target;
    }

    private static void RewriteArchive(string sourcePath, string targetPath, Func<string, byte[], byte[]> transform)
    {
        using var source = ZipFile.OpenRead(sourcePath);
        using var target = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            WriteEntry(target, entry.FullName, transform(entry.FullName, memory.ToArray()));
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        using var output = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        output.Write(bytes);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static YemekhaneDbContext CreateContext(string connectionString) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static TimeZoneInfo IstanbulTimeZone
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        }
    }

    private sealed record SeedIds(Guid StudentId, Guid UserId, Guid AuditId, Guid SecretSettingId);

    private sealed class WalWriter(CancellationTokenSource cancellation, Task started, Task task)
    {
        public Task Started { get; } = started;
        public async Task StopAsync()
        {
            cancellation.Cancel();
            try { await task; }
            catch (OperationCanceledException) { }
            cancellation.Dispose();
        }
    }

    private sealed class AcceptanceFixture : IDisposable
    {
        private AcceptanceFixture(string root, int retentionCount, long maximumBytes,
            IReadOnlyDictionary<string, string?>? settings, Func<BackupRestoreStage, CancellationToken, Task>? hook)
        {
            Root = root;
            Directory.CreateDirectory(root);
            DatabaseDirectory = Path.Combine(root, "data");
            BackupDirectory = Path.Combine(root, "backups");
            Directory.CreateDirectory(DatabaseDirectory);
            ConnectionString = LocalDatabaseConnection.Resolve(null, DatabaseDirectory);
            DatabasePath = Path.GetFullPath(new SqliteConnectionStringBuilder(ConnectionString).DataSource);
            Service = new BackupService(ConnectionString, new BackupOptions
            {
                Directory = BackupDirectory,
                RetentionCount = retentionCount,
                MaximumArchiveBytes = maximumBytes,
                MaximumExtractedBytes = maximumBytes * 2
            }, settings, hook);
        }

        public string Root { get; }
        public string DatabaseDirectory { get; }
        public string BackupDirectory { get; }
        public string ConnectionString { get; }
        public string DatabasePath { get; }
        public BackupService Service { get; }

        public static async Task<AcceptanceFixture> CreateAsync(int retentionCount = 10, long maximumBytes = 64L * 1024 * 1024,
            IReadOnlyDictionary<string, string?>? settings = null, Func<BackupRestoreStage, CancellationToken, Task>? hook = null)
        {
            var fixture = new AcceptanceFixture(Path.Combine(Path.GetTempPath(), "Yemekhane.Task059", Guid.NewGuid().ToString("N")), retentionCount, maximumBytes, settings, hook);
            await fixture.ReopenAndMigrateAsync();
            return fixture;
        }

        public YemekhaneDbContext CreateContext() => Task059BackupRestoreAcceptanceTests.CreateContext(ConnectionString);
        public string NewPath(string name) => Path.Combine(Root, name);

        public async Task ReopenAndMigrateAsync()
        {
            var services = new ServiceCollection().AddYemekhaneInfrastructure(ConnectionString).BuildServiceProvider();
            await using (services)
            await using (var scope = services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task<Guid> AddStudentAsync(string number)
        {
            var student = new Student { StudentNo = number, FirstName = "Task", LastName = "059" };
            await using var db = CreateContext();
            db.Add(student);
            await db.SaveChangesAsync();
            return student.Id;
        }

        public async Task<bool> StudentExistsAsync(Guid id)
        {
            await using var db = CreateContext();
            return await db.Students.IgnoreQueryFilters().AnyAsync(x => x.Id == id);
        }

        public void DeleteDatabaseAndSidecars()
        {
            foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
