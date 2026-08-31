using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Backup;

public sealed class BackupService : IDisposable
{
    public const int CurrentFormatVersion = 1;
    private const string DatabaseEntryName = "database.sqlite";
    private const string SettingsEntryName = "settings.json";
    private const string ManifestEntryName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SecretFragments = ["password", "secret", "token", "key", "credential", "connectionstring"];
    private readonly string connectionString;
    private readonly BackupOptions options;
    private readonly IReadOnlyDictionary<string, string?> systemSettings;
    private readonly SemaphoreSlim maintenanceLock = new(1, 1);
    private readonly Func<BackupRestoreStage, CancellationToken, Task>? restoreHook;

    public BackupService(
        string connectionString,
        BackupOptions options,
        IReadOnlyDictionary<string, string?>? systemSettings = null)
        : this(connectionString, options, systemSettings, null)
    {
    }

    internal BackupService(
        string connectionString,
        BackupOptions options,
        IReadOnlyDictionary<string, string?>? systemSettings,
        Func<BackupRestoreStage, CancellationToken, Task>? restoreHook)
    {
        this.connectionString = connectionString;
        this.options = options;
        this.systemSettings = systemSettings ?? new Dictionary<string, string?>();
        this.restoreHook = restoreHook;
        ValidateOptions(options);
    }

    public async Task<BackupResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        await maintenanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateCoreAsync("backup", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            maintenanceLock.Release();
        }
    }

    public async Task<RestoreResult> RestoreAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        await maintenanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var databaseDirectory = Path.GetDirectoryName(GetDatabasePath())!;
        var workDirectory = Path.Combine(databaseDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var validated = await ValidateArchiveAsync(archivePath, workDirectory, cancellationToken).ConfigureAwait(false);
            if (!IsCompatibleAppVersion(validated.Manifest.AppVersion, GetAppVersion()))
                throw new BackupValidationException($"Backup uygulama sürümü uyumsuz: {validated.Manifest.AppVersion}.");
            var latestSchema = GetLatestSchemaVersion();
            if (string.CompareOrdinal(validated.Manifest.SchemaVersion, latestSchema) > 0)
                throw new BackupValidationException($"Backup şema sürümü uygulamadan yeni: {validated.Manifest.SchemaVersion} (desteklenen: {latestSchema}).");

            var databasePath = GetDatabasePath();
            BackupResult? safety = null;
            var currentDatabaseIsHealthy = await IsCurrentDatabaseHealthyAsync(databasePath, cancellationToken).ConfigureAwait(false);
            if (currentDatabaseIsHealthy)
                safety = await CreateCoreAsync("pre-restore", cancellationToken, alsoProtect: archivePath).ConfigureAwait(false);
            var rollbackPath = Path.Combine(workDirectory, "original.sqlite");
            var replaced = false;
            var originalExisted = File.Exists(databasePath);
            try
            {
                if (currentDatabaseIsHealthy)
                    await CheckpointAsync(cancellationToken).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                await InvokeRestoreHookAsync(BackupRestoreStage.BeforeDatabaseReplacement, cancellationToken).ConfigureAwait(false);
                if (originalExisted)
                    File.Replace(validated.DatabasePath, databasePath, rollbackPath, ignoreMetadataErrors: true);
                else
                    File.Move(validated.DatabasePath, databasePath);
                replaced = true;
                DeleteSidecar(databasePath + "-wal");
                DeleteSidecar(databasePath + "-shm");
                await InvokeRestoreHookAsync(BackupRestoreStage.AfterDatabaseReplacement, cancellationToken).ConfigureAwait(false);
                await VerifyIntegrityAsync(connectionString, cancellationToken).ConfigureAwait(false);
                await using (var context = new YemekhaneDbContext(
                                 new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connectionString).Options))
                    await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await VerifyIntegrityAsync(connectionString, cancellationToken).ConfigureAwait(false);
                var restoredSchema = await ReadSchemaVersionAsync(connectionString, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(restoredSchema, latestSchema, StringComparison.Ordinal))
                    throw new BackupValidationException($"Restore sonrası şema sürümü güncellenemedi: {restoredSchema}.");
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (replaced && File.Exists(rollbackPath))
                    File.Replace(rollbackPath, databasePath, null, ignoreMetadataErrors: true);
                else if (replaced && !originalExisted)
                    DeleteSidecar(databasePath);
                DeleteSidecar(databasePath + "-wal");
                DeleteSidecar(databasePath + "-shm");
                throw;
            }

            return new RestoreResult(validated.Manifest.BackupId, true, true, safety?.FileName ?? string.Empty);
        }
        finally
        {
            maintenanceLock.Release();
            TryDeleteDirectory(workDirectory);
        }
    }

    public async Task<ValidatedBackup> ValidateAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var workDirectory = Path.Combine(Path.GetTempPath(), "YemekhanePro", ".validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var value = await ValidateArchiveAsync(archivePath, workDirectory, cancellationToken).ConfigureAwait(false);
            return new ValidatedBackup(value.Manifest.BackupId, value.Manifest.CreatedAtUtc,
                value.Manifest.SchemaVersion, value.Manifest.AppVersion);
        }
        finally { TryDeleteDirectory(workDirectory); }
    }

    private const string PreRestorePrefix = "okulyemek-pre-restore-";
    private const int PreRestoreRetentionCount = 3;

    public void ApplyRetention() => ApplyRetention(null, null);

    /// <param name="protectedPath">Yeni yazilan arsiv.</param>
    /// <param name="alsoProtect">Restore edilmekte olan kaynak arsiv; silinirse ikinci deneme imkansiz olur.</param>
    private void ApplyRetention(string? protectedPath, string? alsoProtect = null)
    {
        var directory = ResolveBackupDirectory();
        if (!System.IO.Directory.Exists(directory)) return;

        // Glob hem "okulyemek-backup-*" hem "okulyemek-pre-restore-*" dosyalarini yakalardi;
        // her restore bir kota yiyip gercek yedek gecmisini eritiyordu. Iki tur ayri saklanir.
        // protectedPath: az once yazilan (veya restore edilen) arsiv asla silinmez.
        foreach (var group in new DirectoryInfo(directory).EnumerateFiles("okulyemek-*.zip")
                     .GroupBy(x => x.Name.StartsWith(PreRestorePrefix, StringComparison.Ordinal)))
        {
            var keep = group.Key ? PreRestoreRetentionCount : options.RetentionCount;
            foreach (var file in group
                         .OrderByDescending(x => x.LastWriteTimeUtc)
                         .ThenByDescending(x => x.Name, StringComparer.Ordinal)
                         .Skip(keep))
            {
                if (IsProtected(file.FullName, protectedPath) || IsProtected(file.FullName, alsoProtect))
                {
                    continue;
                }

                file.Delete();
            }
        }
    }

    private static bool IsProtected(string path, string? candidate) =>
        candidate is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(candidate),
            StringComparison.OrdinalIgnoreCase);

    private async Task<BackupResult> CreateCoreAsync(string kind, CancellationToken cancellationToken,
        string? alsoProtect = null)
    {
        var backupId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var directory = ResolveBackupDirectory();
        System.IO.Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"okulyemek-{kind}-{now:yyyyMMdd-HHmmss}-{backupId:N}.zip");
        var temporaryPath = finalPath + ".tmp";
        var databaseSnapshot = Path.Combine(directory, $".{backupId:N}.sqlite.tmp");
        try
        {
            var sourceBuilder = new SqliteConnectionStringBuilder(connectionString);
            var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = databaseSnapshot, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
            await using (var source = new SqliteConnection(sourceBuilder.ToString()))
            await using (var destination = new SqliteConnection(destinationBuilder.ToString()))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            await VerifyIntegrityAsync(destinationBuilder.ToString(), cancellationToken).ConfigureAwait(false);
            var schemaVersion = await ReadSchemaVersionAsync(destinationBuilder.ToString(), cancellationToken).ConfigureAwait(false);
            var settings = (await ReadSystemSettingsAsync(destinationBuilder.ToString(), cancellationToken).ConfigureAwait(false))
                .Concat(systemSettings)
                .Where(x => !IsSecret(x.Key))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last().Value, StringComparer.Ordinal);
            var settingsBytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            var databaseInfo = new FileInfo(databaseSnapshot);
            var files = new List<BackupFileManifest>
            {
                new(DatabaseEntryName, databaseInfo.Length, await ComputeSha256Async(databaseSnapshot, cancellationToken).ConfigureAwait(false)),
                new(SettingsEntryName, settingsBytes.LongLength, Convert.ToHexString(SHA256.HashData(settingsBytes)).ToLowerInvariant())
            };
            var manifest = new BackupManifest(CurrentFormatVersion, backupId, now,
                TimeZoneInfo.ConvertTime(now, IstanbulTimeZone), GetAppVersion(), schemaVersion, files);

            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.Optimal);
                await using (var target = dbEntry.Open())
                await using (var source = new FileStream(databaseSnapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

                var settingsEntry = archive.CreateEntry(SettingsEntryName, CompressionLevel.Optimal);
                await using (var target = settingsEntry.Open())
                    await target.WriteAsync(settingsBytes, cancellationToken).ConfigureAwait(false);

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using (var target = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(target, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, finalPath);
            ApplyRetention(finalPath, alsoProtect);
            if (!File.Exists(finalPath))
            {
                throw new InvalidOperationException(
                    $"Yedek dosyasi olusturulduktan sonra bulunamadi: {Path.GetFileName(finalPath)}");
            }

            return new BackupResult(backupId, Path.GetFileName(finalPath), finalPath, manifest);
        }
        finally
        {
            DeleteSidecar(databaseSnapshot);
            DeleteSidecar(temporaryPath);
        }
    }

    private async Task<(BackupManifest Manifest, string DatabasePath)> ValidateArchiveAsync(
        string archivePath, string workDirectory, CancellationToken cancellationToken)
    {
        var archiveInfo = new FileInfo(archivePath);
        if (!archiveInfo.Exists || archiveInfo.Length == 0 || archiveInfo.Length > options.MaximumArchiveBytes)
            throw new BackupValidationException("Backup arşivi boş, bulunamadı veya izin verilen boyutu aşıyor.");

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count != 3 || archive.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count() != 3)
                throw new BackupValidationException("Backup arşivi beklenen dosya kümesini içermiyor.");
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is not (DatabaseEntryName or SettingsEntryName or ManifestEntryName) ||
                    entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
                    throw new BackupValidationException("Backup arşivinde geçersiz dosya yolu var.");
                if (entry.Length < 0 || entry.Length > options.MaximumExtractedBytes)
                    throw new BackupValidationException("Backup arşivi açılmış boyut sınırını aşıyor.");
                if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 200)
                    throw new BackupValidationException("Backup arşivinde güvenli olmayan sıkıştırma oranı var.");
            }
            if (archive.Entries.Sum(x => x.Length) > options.MaximumExtractedBytes)
                throw new BackupValidationException("Backup arşivi toplam açılmış boyut sınırını aşıyor.");

            var manifestEntry = archive.GetEntry(ManifestEntryName)!;
            if (manifestEntry.Length > 1024 * 1024)
                throw new BackupValidationException("Backup manifest boyutu geçersiz.");
            BackupManifest manifest;
            await using (var stream = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new BackupValidationException("Backup manifest okunamadı.");
            ValidateManifest(manifest);

            var databasePath = Path.Combine(workDirectory, DatabaseEntryName);
            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry(file.Path) ?? throw new BackupValidationException($"Backup dosyası eksik: {file.Path}.");
                var targetPath = file.Path == DatabaseEntryName ? databasePath : Path.Combine(workDirectory, SettingsEntryName);
                await using var input = entry.Open();
                await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > options.MaximumExtractedBytes || written > file.Size)
                        throw new BackupValidationException("Backup dosyası bildirilen boyutu aşıyor.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (written != file.Size || !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualHash), Convert.FromHexString(file.Sha256)))
                    throw new BackupValidationException($"Backup checksum doğrulaması başarısız: {file.Path}.");
            }

            await VerifyIntegrityAsync(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString(), cancellationToken).ConfigureAwait(false);
            var databaseSchema = await ReadSchemaVersionAsync(
                new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString(),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(databaseSchema, manifest.SchemaVersion, StringComparison.Ordinal))
                throw new BackupValidationException("Backup manifest şema sürümü veritabanı ile eşleşmiyor.");
            return (manifest, databasePath);
        }
        catch (BackupValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or FormatException or SqliteException)
        {
            throw new BackupValidationException($"Geçersiz backup arşivi: {exception.Message}");
        }
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != CurrentFormatVersion)
            throw new BackupValidationException($"Desteklenmeyen backup format sürümü: {manifest.FormatVersion}.");
        if (manifest.BackupId == Guid.Empty || string.IsNullOrWhiteSpace(manifest.AppVersion) || string.IsNullOrWhiteSpace(manifest.SchemaVersion))
            throw new BackupValidationException("Backup manifest zorunlu alanları eksik.");
        if (manifest.Files.Count != 2 || manifest.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal)
                .SetEquals([DatabaseEntryName, SettingsEntryName]) is false)
            throw new BackupValidationException("Backup manifest dosya listesi geçersiz.");
        foreach (var file in manifest.Files)
        {
            if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new BackupValidationException("Backup manifest checksum veya boyut alanı geçersiz.");
        }
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyIntegrityAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new BackupValidationException($"SQLite bütünlük kontrolü başarısız: {result}.");

        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new BackupValidationException($"SQLite foreign key kontrolü başarısız: {reader.GetString(0)}.");
    }

    private static async Task<string> ReadSchemaVersionAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture)
            ?? throw new BackupValidationException("Veritabanı şema sürümü okunamadı.");
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadSystemSettingsAsync(
        string targetConnectionString, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM system_settings WHERE IsSecret = 0 ORDER BY Key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    private string ResolveBackupDirectory() => Path.GetFullPath(string.IsNullOrWhiteSpace(options.Directory)
        ? Path.Combine(Path.GetDirectoryName(GetDatabasePath())!, "Backups")
        : options.Directory);

    private string GetDatabasePath()
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
            throw new InvalidOperationException("Backup yalnızca dosya tabanlı SQLite veritabanlarında desteklenir.");
        return Path.GetFullPath(path);
    }

    private static string GetAppVersion() =>
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BackupService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static bool IsCompatibleAppVersion(string backupVersion, string currentVersion)
    {
        var backupCore = backupVersion.Split(['+', '-'], 2)[0];
        var currentCore = currentVersion.Split(['+', '-'], 2)[0];
        return Version.TryParse(backupCore, out var backup) && Version.TryParse(currentCore, out var current)
            ? backup.Major == current.Major
            : string.Equals(backupVersion, currentVersion, StringComparison.Ordinal);
    }

    private static TimeZoneInfo IstanbulTimeZone
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        }
    }

    private static bool IsSecret(string key) => SecretFragments.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string GetLatestSchemaVersion() => typeof(YemekhaneDbContext).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
        .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Order(StringComparer.Ordinal)
        .LastOrDefault() ?? throw new BackupValidationException("Uygulama şema sürümü belirlenemedi.");

    private async Task<bool> IsCurrentDatabaseHealthyAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
            return false;
        try
        {
            await VerifyIntegrityAsync(connectionString, cancellationToken).ConfigureAwait(false);
            _ = await ReadSchemaVersionAsync(connectionString, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is BackupValidationException or SqliteException or IOException)
        {
            return false;
        }
    }

    private Task InvokeRestoreHookAsync(BackupRestoreStage stage, CancellationToken cancellationToken) =>
        restoreHook?.Invoke(stage, cancellationToken) ?? Task.CompletedTask;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void ValidateOptions(BackupOptions value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.RetentionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.MaximumArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.MaximumExtractedBytes);
    }

    private static void DeleteSidecar(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => maintenanceLock.Dispose();
}

internal enum BackupRestoreStage
{
    BeforeDatabaseReplacement,
    AfterDatabaseReplacement
}
