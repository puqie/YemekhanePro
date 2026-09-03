using System.IO;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Arayuz denetimi icin GERCEKCI veri tohumlar. Gercek okul verisindeki gibi
/// ayni ad-soyadli ogrenciler kasitli olarak tekrarlidir (uc ADA, dort ALI):
/// ayirt edicilik sorunlarini ancak boyle bir veri ortaya cikarir.
/// </summary>
/// <remarks>
/// Calistirma: <c>YP_SEED_DB=&lt;db yolu&gt; dotnet test --filter FullyQualifiedName~LiveSeed</c>.
/// Ortam degiskeni yoksa test hicbir sey yapmadan gecer -- normal paket kosusunda
/// yan etkisi yoktur. API'nin DB'yi (migration'lar dahil) onceden olusturmus olmasi
/// beklenir; tohumlama sirasinda API'nin KAPALI olmasi tercih edilir.
/// </remarks>
public class LiveSeed
{
    private static readonly string[] FirstNames =
    [
        "ADA", "ADA", "ADA", "ALİ", "ALİ", "ALİ", "ALİ", "FATİH", "ZEYNEP", "ELİF",
        "MEHMET", "AYŞE", "MUSTAFA", "EMİNE", "AHMET", "HATİCE", "YUSUF", "ZEHRA",
        "ÖMER", "MERYEM", "HÜSEYİN", "ŞEVVAL", "İBRAHİM", "SÜMEYYE", "ÇAĞLA", "GÖKHAN",
    ];

    private static readonly string[] LastNames =
    [
        "KATIRCI", "HAŞLAMACI", "SÖYLEMEZ", "YILDIZ", "DEMİR", "ÇELİK", "ŞAHİN", "SİDAL",
        "KAYA", "YILMAZ", "ÖZTÜRK", "AYDIN", "ÖZDEMİR", "ARSLAN", "DOĞAN", "KILIÇ",
        "ASLAN", "ÇETİN", "KURT", "KOÇ", "ŞİMŞEK", "İLHAN", "GÜNEŞ", "AKGÜN",
    ];

    [Fact]
    public async Task Seed()
    {
        var path = Environment.GetEnvironmentVariable("YP_SEED_DB");
        if (string.IsNullOrWhiteSpace(path)) return;
        await SeedAsync(path);
    }

    /// <summary>
    /// internal: bu sinifin bir [Fact]'i oldugu icin xUnit onu TEST SINIFI sayar ve
    /// public metotlari test zanneder (xUnit1013). Yardimci metot disariya acilmaz.
    /// </summary>
    internal static async Task SeedAsync(string path)
    {
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        await using var db = new YemekhaneDbContext(options);

        if (await db.Students.CountAsync() > 5) { Log("zaten dolu, atlandi"); return; }

        // Tarih CIPASI BUGUNDUR. Onceki hali 2026-09-02'ye sabitlenmisti: yazildigi gun
        // calisiyor, ertesi gun "Gunluk Takip" bugunu gosterirken tohumda bugune ait hic
        // gecis olmadigi icin urun dogru calisirken testler dusuyordu.
        // Random tohumu BILEREK sabit birakildi: onu bugune baglamak ogrenci/sinif
        // dagilimini her kosuda degistirir ve sabit deger bekleyen yolculuklari kirar.
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Saat de GERCEK olmalidir. Sabit 08:00 kullanilsaydi, test gece yarisindan sonra
        // kostugunda bugunun gecisleri GELECEKTE damgalanirdi; en yeniden eskiye sirali
        // Gunluk Takip'in 100 satirlik ilk sayfasi bu gelecek kayitlarla dolar ve testin
        // az once yaptigi gercek gecis goze gorunmez olurdu.
        var now = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(3)).AddMinutes(-5);
        var rng = new Random(20260902);

        var classes = await db.Set<SchoolClass>().ToListAsync();
        if (classes.Count == 0)
        {
            classes = Enumerable.Range(0, 12)
                .Select(i => new SchoolClass { Name = $"{5 + i / 3}{(char)(65 + i % 3)}" })
                .ToList();
            db.AddRange(classes);
        }

        var sections = await db.Set<Section>().ToListAsync();
        if (sections.Count == 0)
        {
            sections = new[] { "A", "B", "C", "D", "E" }.Select(n => new Section { Name = n }).ToList();
            db.AddRange(sections);
        }

        var meals = await db.Set<MealType>().ToListAsync();
        if (meals.Count == 0)
        {
            meals =
            [
                new MealType { Name = "Kahvaltı", IsActive = true },
                new MealType { Name = "Öğle Yemeği", IsActive = true },
                new MealType { Name = "İkindi Kahvaltısı", IsActive = true },
            ];
            db.AddRange(meals);
        }

        var incomeTypes = await db.Set<IncomeType>().ToListAsync();
        if (incomeTypes.Count == 0)
        {
            incomeTypes =
            [
                new IncomeType { Name = "Aylık Yemek Ücreti", IsActive = true },
                new IncomeType { Name = "Günlük Yemek", IsActive = true },
                new IncomeType { Name = "Servis Ücreti", IsActive = true },
            ];
            db.AddRange(incomeTypes);
        }

        if (!await db.Set<Device>().AnyAsync())
        {
            db.AddRange(
                new Device { Name = "Yemekhane Giriş", DeviceType = "SF300", ConnectionType = "Ethernet", IpAddress = "192.168.1.201", IpPort = 4370, Direction = "Entry", ConnectionStatus = "Online", IsActive = true, AutoConnect = true, HasTurnstile = true },
                new Device { Name = "Yemekhane Çıkış", DeviceType = "SF300", ConnectionType = "Ethernet", IpAddress = "192.168.1.202", IpPort = 4370, Direction = "Exit", ConnectionStatus = "Offline", IsActive = true, HasTurnstile = true },
                new Device { Name = "Kantin Okuyucu", DeviceType = "CardReader", ConnectionType = "Simulator", Direction = "Entry", ConnectionStatus = "Error", IsActive = true });
        }
        await db.SaveChangesAsync();

        // Ada gore SIRALI okunur: EF, GUID anahtarli satirlari her kosuda farkli sirayla
        // ekler ve sirasiz ToListAsync farkli sira dondurur. O zaman ayni Random tohumuyla
        // bile 5252 numarali ogrenci bir veritabaninda 6C, digerinde 7B oluyordu ve sabit
        // deger bekleyen yolculuk testleri rastgele dusuyordu.
        classes = await db.Set<SchoolClass>().OrderBy(x => x.Name).ToListAsync();
        sections = await db.Set<Section>().OrderBy(x => x.Name).ToListAsync();
        meals = await db.Set<MealType>().OrderBy(x => x.Name).ToListAsync();
        incomeTypes = await db.Set<IncomeType>().OrderBy(x => x.Name).ToListAsync();

        var students = new List<Student>();
        var cards = new List<StudentCard>();
        for (var i = 0; i < 420; i++)
        {
            var student = new Student
            {
                Id = Guid.NewGuid(),
                StudentNo = (5000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FirstName = FirstNames[rng.Next(FirstNames.Length)],
                LastName = LastNames[rng.Next(LastNames.Length)],
                ClassId = classes[rng.Next(classes.Count)].Id,
                SectionId = sections[rng.Next(sections.Count)].Id,
                IsActive = i % 17 != 0,            // her 17'de bir pasif
                CreatedAt = now.AddDays(-rng.Next(30, 400)),
            };
            students.Add(student);
            if (i % 9 != 0)                        // her 9'da birinin karti YOK
                cards.Add(new StudentCard
                {
                    Id = Guid.NewGuid(),
                    StudentId = student.Id,
                    CardNumber = (8350000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsActive = true,
                    ValidFrom = student.CreatedAt,
                });
        }
        db.AddRange(students);
        db.AddRange(cards);
        await db.SaveChangesAsync();
        Log($"{students.Count} ogrenci, {cards.Count} kart");

        // Turkiye cep telefonu 11 hane: 05xx xxx xx xx
        var parents = students.Where((_, i) => i % 3 != 0).Select((s, i) => new Parent
        {
            Id = Guid.NewGuid(),
            StudentId = s.Id,
            Name = $"{s.LastName} VELİSİ",
            NormalizedPhone = $"05{rng.Next(30, 56)}{rng.Next(1000000, 9999999)}",
            Relationship = i % 2 == 0 ? "Anne" : "Baba",
        }).ToList();
        db.AddRange(parents);
        await db.SaveChangesAsync();
        Log($"{parents.Count} veli");

        var lunch = meals.First(m => m.Name.Contains("Öğle", StringComparison.Ordinal));
        var entitlements = new List<MealEntitlement>();
        var activeStudents = students.Where(s => s.IsActive).ToList();
        for (var day = 0; day < 20; day++)
        {
            // Pencere bugunde BITER: son gun bugundur, boylece "bugun" ekranlari hep doludur.
            var date = today.AddDays(day - 19);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var s in activeStudents)
                entitlements.Add(new MealEntitlement
                {
                    Id = Guid.NewGuid(), StudentId = s.Id, MealTypeId = lunch.Id,
                    EntitlementDate = date, Quantity = 1, ConsumedQuantity = 0,
                    Status = "Active", Source = "Manual", CreatedAt = now.AddDays(-10),
                });
        }
        db.AddRange(entitlements);
        await db.SaveChangesAsync();
        Log($"{entitlements.Count} hakedis");

        var tx = new List<IncomeTransaction>();
        for (var i = 0; i < 260; i++)
        {
            var s = activeStudents[rng.Next(activeStudents.Count)];
            var card = cards.FirstOrDefault(c => c.StudentId == s.Id);
            tx.Add(new IncomeTransaction
            {
                Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), StudentId = s.Id,
                CardNumber = card?.CardNumber,
                IncomeTypeId = incomeTypes[rng.Next(incomeTypes.Count)].Id,
                Amount = new[] { 250m, 500m, 750m, 1200m, 1500m }[rng.Next(5)],
                TransactionAt = now.AddDays(-rng.Next(0, 25)).AddHours(rng.Next(-4, 6)),
                Description = "Eylül ayı ödemesi",
                IsVoided = i % 23 == 0,
                VoidReason = i % 23 == 0 ? "Yanlış tutar girildi" : null,
                CreatedAt = now.AddDays(-rng.Next(0, 25)),
            });
        }
        db.AddRange(tx);
        await db.SaveChangesAsync();
        Log($"{tx.Count} gelir islemi");

        var devices = await db.Set<Device>().OrderBy(x => x.Name).ToListAsync();
        var logs = new List<AccessLog>();
        for (var day = 0; day < 12; day++)
        {
            var date = now.AddDays(-day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var s in activeStudents.OrderBy(_ => rng.Next()).Take(180))
            {
                var card = cards.FirstOrDefault(c => c.StudentId == s.Id);
                var denied = rng.Next(100) < 8;
                logs.Add(new AccessLog
                {
                    Id = Guid.NewGuid(), StudentId = s.Id,
                    CardNumber = card?.CardNumber ?? "-",
                    DeviceId = devices[rng.Next(devices.Count)].Id, MealTypeId = lunch.Id,
                    Decision = denied ? "DENY" : "ALLOW",
                    Reason = denied ? new[] { "Hakediş yok", "Kart pasif", "Öğün saati dışı" }[rng.Next(3)] : "OK",
                    Direction = "Entry", ReaderSource = "Device", OperationId = Guid.NewGuid(),
                    Timestamp = date,
                });
            }
        }
        db.AddRange(logs);
        await db.SaveChangesAsync();
        Log($"{logs.Count} erisim logu");
        Log("TAMAM");
    }

    private static void Log(string m) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "seed.txt"), m + Environment.NewLine);
}
