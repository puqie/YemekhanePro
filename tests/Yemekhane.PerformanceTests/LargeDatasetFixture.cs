using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.PerformanceTests;

internal sealed class LargeDatasetFixture : IAsyncDisposable
{
    public const int StudentCount = 100_000;
    public const int AccessLogCount = 1_000_000;
    private const int BatchSize = 10_000;

    private readonly string connectionString;

    private LargeDatasetFixture(string path)
    {
        Path = path;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 120
        }.ToString();
    }

    public string Path { get; }
    public DateOnly Date { get; } = new(2026, 8, 31);
    public DateTimeOffset DayStart { get; } = new(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
    public static Guid MealTypeId => Key(1, 4);
    public static Guid DeviceId => Key(1, 5);
    public static Guid TargetStudentId => Key(StudentCount - 1, 1);
    public static Guid TargetEntitlementId => Key(StudentCount - 1, 3);
    public static Guid TargetClassId => Key((StudentCount - 1) % 100, 6);
    public static string TargetStudentNo => Number(StudentCount - 1);
    public static string TargetCard => "CARD-" + TargetStudentNo;
    public TimeSpan SeedDuration { get; private set; }

    public static async Task<LargeDatasetFixture> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yemekhane-task058-{Guid.NewGuid():N}.db");
        var fixture = new LargeDatasetFixture(path);
        try
        {
            var started = Stopwatch.GetTimestamp();
            await using (var db = fixture.CreateContext())
                await db.Database.MigrateAsync();
            await fixture.SeedAsync();
            fixture.SeedDuration = Stopwatch.GetElapsedTime(started);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public YemekhaneDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new YemekhaneDbContext(options);
    }

    public async Task<IReadOnlyList<string>> ExplainAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(3));
        return result;
    }

    public async Task CheckpointAsync()
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public static Guid StudentId(int index) => Key(index, 1);
    public static Guid OperationId(int index) => Key(index, 8);
    public DateTimeOffset LogTimestamp(int index) => DayStart.AddTicks(index * 450_000L);

    private async Task SeedAsync()
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-131072;");

        await SeedReferenceDataAsync(connection);
        await SeedStudentsAsync(connection);
        await SeedAccessLogsAsync(connection);
        await ExecuteAsync(connection, "ANALYZE; PRAGMA optimize;");
    }

    private async Task SeedReferenceDataAsync(SqliteConnection connection)
    {
        await using var transaction = connection.BeginTransaction();
        await using var classes = Command(connection, transaction,
            "INSERT INTO classes (Id, Name, SearchName, IsActive, CreatedAt) VALUES ($id, $name, $search, 1, $created)",
            "$id", "$name", "$search", "$created");
        for (var i = 0; i < 100; i++)
        {
            Set(classes, Key(i, 6), $"Class-{i:D3}", $"CLASS-{i:D3}", DayStart);
            classes.ExecuteNonQuery();
        }

        await using var meal = Command(connection, transaction,
            "INSERT INTO meal_types (Id, Name, IsActive, CreatedAt) VALUES ($id, $name, 1, $created)",
            "$id", "$name", "$created");
        Set(meal, MealTypeId, "Lunch", DayStart);
        meal.ExecuteNonQuery();

        await using var devices = Command(connection, transaction,
            "INSERT INTO devices (Id, Name, DeviceType, ConnectionType, IsActive, AutoConnect, HasTurnstile, Direction, ConnectionStatus, CreatedAt) VALUES ($id, $name, 'Reader', 'TCP', 1, 0, 1, 'Entry', 'Online', $created)",
            "$id", "$name", "$created");
        for (var i = 0; i < 4; i++)
        {
            Set(devices, Key(i + 1, 5), $"Gate-{i + 1}", DayStart);
            devices.ExecuteNonQuery();
        }
        await transaction.CommitAsync();
    }

    private async Task SeedStudentsAsync(SqliteConnection connection)
    {
        await using var student = Command(connection, null,
            "INSERT INTO students (Id, student_no, FirstName, LastName, SearchName, ClassId, IsActive, IsDeleted, RegisteredOn, CreatedAt) VALUES ($id, $number, $first, $last, $search, $class, 1, 0, $registered, $created)",
            "$id", "$number", "$first", "$last", "$search", "$class", "$registered", "$created");
        await using var card = Command(connection, null,
            "INSERT INTO student_cards (Id, StudentId, card_number, ValidFrom, IsActive, CreatedAt) VALUES ($id, $student, $number, $valid, 1, $created)",
            "$id", "$student", "$number", "$valid", "$created");
        await using var entitlement = Command(connection, null,
            "INSERT INTO meal_entitlements (Id, StudentId, MealTypeId, EntitlementDate, Quantity, ConsumedQuantity, Status, Source, Version, CreatedAt) VALUES ($id, $student, $meal, $date, $quantity, $consumed, 'Active', 'LargeFixture', 0, $created)",
            "$id", "$student", "$meal", "$date", "$quantity", "$consumed", "$created");

        for (var batch = 0; batch < StudentCount; batch += BatchSize)
        {
            await using var transaction = connection.BeginTransaction();
            student.Transaction = transaction;
            card.Transaction = transaction;
            entitlement.Transaction = transaction;
            for (var i = batch; i < Math.Min(batch + BatchSize, StudentCount); i++)
            {
                var studentId = StudentId(i);
                var number = Number(i);
                var first = $"Name{i % 1000:D3}";
                var last = $"Surname{i:D6}";
                Set(student, studentId, number, first, last, $"{first} {last}".ToUpperInvariant(), Key(i % 100, 6), Date, DayStart);
                student.ExecuteNonQuery();
                Set(card, Key(i, 2), studentId, "CARD-" + number, DayStart, DayStart);
                card.ExecuteNonQuery();
                var isAtomicTarget = i == StudentCount - 1;
                Set(entitlement, Key(i, 3), studentId, MealTypeId, Date, isAtomicTarget ? 2 : 10,
                    isAtomicTarget ? 0 : i % 10, DayStart);
                entitlement.ExecuteNonQuery();
            }
            await transaction.CommitAsync();
        }
    }

    private async Task SeedAccessLogsAsync(SqliteConnection connection)
    {
        await using var command = Command(connection, null,
            "INSERT INTO access_logs (Id, Timestamp, StudentId, DeviceId, MealTypeId, CardNumber, Decision, Reason, Direction, ReaderSource, OperationId, CreatedAt) VALUES ($id, $timestamp, $student, $device, $meal, $card, $decision, $reason, 'Entry', 'LargeFixture', $operation, $created)",
            "$id", "$timestamp", "$student", "$device", "$meal", "$card", "$decision", "$reason", "$operation", "$created");
        for (var batch = 0; batch < AccessLogCount; batch += BatchSize)
        {
            await using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            for (var i = batch; i < Math.Min(batch + BatchSize, AccessLogCount); i++)
            {
                var studentIndex = i % StudentCount;
                var denied = i % 10 == 0;
                Set(command, Key(i, 7), LogTimestamp(i), StudentId(studentIndex), Key(i % 4 + 1, 5), MealTypeId,
                    "CARD-" + Number(studentIndex), denied ? "DENY" : "ALLOW", denied ? "No entitlement" : "Granted",
                    OperationId(i), DayStart);
                command.ExecuteNonQuery();
            }
            await transaction.CommitAsync();
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params string[] parameterNames)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameterNames) command.Parameters.Add(new SqliteParameter(name, null));
        command.Prepare();
        return command;
    }

    private static void Set(SqliteCommand command, params object[] values)
    {
        for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static Guid Key(int value, short kind) => new(value, kind, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static string Number(int value) => value.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
