using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Infrastructure.Persistence.Seeding;

public sealed class DevelopmentDataSeeder(YemekhaneDbContext dbContext)
{
    private const string SeedVersionKey = "DevelopmentSeedVersion";
    private const string SeedVersion = "1";
    private static readonly string[] MealNames = ["Kahvaltı", "Öğle Yemeği", "Akşam Yemeği", "Ara Öğün"];

    public async Task SeedAsync(string environmentName, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Örnek veri yalnızca Development ortamında oluşturulabilir.");
        }

        if (await dbContext.Set<SystemSetting>().AnyAsync(x => x.Key == SeedVersionKey, cancellationToken))
        {
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var classes = Enumerable.Range(0, 10)
            .Select(index => new SchoolClass { Name = $"{5 + index / 3}{(char)('A' + index % 3)}" })
            .ToArray();
        var mealTypes = MealNames
            .Select(name => new MealType { Name = name })
            .ToArray();
        var devices = new[]
        {
            new Device { Name = "SF300-1", DeviceType = "SF300", ConnectionType = "Ethernet", IpAddress = "192.168.1.201", IpPort = 4370, Direction = "Entry", ConnectionStatus = "Offline", IsActive = true, AutoConnect = true, HasTurnstile = true },
            new Device { Name = "SF300-2", DeviceType = "SF300", ConnectionType = "Ethernet", IpAddress = "192.168.1.202", IpPort = 4370, Direction = "Entry", ConnectionStatus = "Offline", IsActive = true, HasTurnstile = true },
            new Device { Name = "SIM-READER-1", DeviceType = "CardReader", ConnectionType = "Simulator", Direction = "Entry", ConnectionStatus = "Offline", IsActive = true }
        };

        dbContext.AddRange(classes);
        dbContext.AddRange(mealTypes);
        dbContext.AddRange(devices);
        await dbContext.SaveChangesAsync(cancellationToken);

        var students = Enumerable.Range(1, 1_000)
            .Select(index => new Student
            {
                StudentNo = index.ToString("D6", CultureInfo.InvariantCulture),
                FirstName = $"Öğrenci{index:D4}",
                LastName = $"Soyad{index % 100:D2}",
                ClassId = classes[(index - 1) % classes.Length].Id,
                RegisteredOn = new DateOnly(2026, 8, 31)
            })
            .ToArray();
        dbContext.AddRange(students);
        dbContext.AddRange(students.Select((student, index) => new StudentCard
        {
            StudentId = student.Id,
            CardNumber = (8_220_000 + index).ToString(CultureInfo.InvariantCulture),
            ValidFrom = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.FromHours(3))
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var start = new DateTimeOffset(2026, 8, 31, 11, 0, 0, TimeSpan.FromHours(3));
        for (var batchStart = 0; batchStart < 10_000; batchStart += 1_000)
        {
            var logs = Enumerable.Range(batchStart, 1_000).Select(index =>
            {
                var student = students[index % students.Length];
                return new AccessLog
                {
                    Timestamp = start.AddSeconds(index),
                    StudentId = student.Id,
                    DeviceId = devices[index % devices.Length].Id,
                    MealTypeId = mealTypes[index % mealTypes.Length].Id,
                    CardNumber = (8_220_000 + index % students.Length).ToString(CultureInfo.InvariantCulture),
                    Decision = index % 20 == 0 ? "DENY" : "ALLOW",
                    Reason = index % 20 == 0 ? "Bugün yemek hakkı bulunmuyor" : "Yemek hakkı uygun",
                    Direction = "Entry",
                    ReaderSource = devices[index % devices.Length].Name,
                    OperationId = Guid.NewGuid()
                };
            });
            dbContext.AddRange(logs);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        dbContext.Add(new SystemSetting { Key = SeedVersionKey, Value = SeedVersion });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
