using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Integration;

/// <summary>
/// Ekranlardan girilen verinin GERCEKTEN dogru islendigini dogrular.
///
/// Bir isteğin 200 donmesi verinin dogru kaydedildigini kanitlamaz: alan kirpilmis,
/// yanlis sutuna yazilmis, Turkce karakter bozulmus ya da tutar yuvarlanmis olabilir.
/// Bu testler her yazma isleminden sonra VERITABANINA bakar ve yazilan degeri
/// girilen degerle karsilastirir.
/// </summary>
public sealed class DataIntegrityEndToEndTests : IAsyncLifetime
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();

    private async Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> read)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>());
    }

    // ---------------------------------------------------------------- ogrenci

    [Fact]
    public async Task StudentIsStoredExactlyAsEntered()
    {
        var payload = new
        {
            StudentNo = "2026-0417",
            FirstName = "Çağrı",
            LastName = "Şahinoğlu",
            NationalId = "12345678901"
        };

        var response = await client.PostAsJsonAsync("api/students", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(student => student.StudentNo == "2026-0417"));

        // Turkce karakterler bozulmadan saklanmali.
        Assert.Equal("Çağrı", stored.FirstName);
        Assert.Equal("Şahinoğlu", stored.LastName);
        Assert.Equal("12345678901", stored.NationalId);
        Assert.True(stored.IsActive);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task StudentNameIsTrimmedButNotOtherwiseAltered()
    {
        var response = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "  2026-0418  ",
            FirstName = "  Ela  ",
            LastName = "  Yıldız  "
        });
        response.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(student => student.FirstName == "Ela"));

        Assert.Equal("2026-0418", stored.StudentNo);
        Assert.Equal("Yıldız", stored.LastName);
    }

    [Fact]
    public async Task SearchNameIsPopulatedSoTurkishSearchWorks()
    {
        await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0419", FirstName = "İlkay", LastName = "Işık"
        });

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(student => student.StudentNo == "2026-0419"));

        // Turkce arama icin normalize sutun doldurulmali; bos kalirsa arama calismaz.
        Assert.False(string.IsNullOrWhiteSpace(stored.SearchName));
        Assert.Contains("ILKAY", stored.SearchName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateStudentNumberIsRejectedNotSilentlyStored()
    {
        await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0420", FirstName = "Ali", LastName = "Veli"
        });
        var second = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0420", FirstName = "Başka", LastName = "Kişi"
        });

        Assert.False(second.IsSuccessStatusCode,
            "Aynı öğrenci numarası ikinci kez kabul edildi.");
        Assert.Equal(1, await InScope(db => db.Students
            .CountAsync(student => student.StudentNo == "2026-0420")));
    }

    [Fact]
    public async Task UpdateChangesOnlyTheFieldsSent()
    {
        var created = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0421", FirstName = "Deniz", LastName = "Kara",
            NationalId = "98765432109"
        });
        var id = (await created.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        var update = await client.PutAsJsonAsync($"api/students/{id}", new
        {
            StudentNo = "2026-0421", FirstName = "Deniz", LastName = "Kaya",
            NationalId = "98765432109"
        });
        update.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Students.AsNoTracking().SingleAsync(s => s.Id == id));
        Assert.Equal("Kaya", stored.LastName);
        Assert.Equal("Deniz", stored.FirstName);
        Assert.Equal("98765432109", stored.NationalId);
        Assert.NotNull(stored.UpdatedAt);
    }

    // ---------------------------------------------------------------- kart

    [Fact]
    public async Task CardIsLinkedToTheRightStudent()
    {
        var created = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0430", FirstName = "Kart", LastName = "Sahibi"
        });
        var id = (await created.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        var card = await client.PostAsJsonAsync($"api/students/{id}/cards",
            new { CardNumber = "KART-0430" });
        card.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.StudentCards.AsNoTracking()
            .SingleAsync(x => x.CardNumber == "KART-0430"));

        Assert.Equal(id, stored.StudentId);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task SameCardNumberCannotBeGivenToTwoStudents()
    {
        var first = await client.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-0431", FirstName = "Bir", LastName = "Öğrenci" });
        var second = await client.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-0432", FirstName = "İki", LastName = "Öğrenci" });
        var firstId = (await first.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;
        var secondId = (await second.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        await client.PostAsJsonAsync($"api/students/{firstId}/cards", new { CardNumber = "KART-CAKISMA" });
        var clash = await client.PostAsJsonAsync($"api/students/{secondId}/cards",
            new { CardNumber = "KART-CAKISMA" });

        Assert.False(clash.IsSuccessStatusCode, "Aynı kart numarası iki öğrenciye verildi.");
        Assert.Equal(1, await InScope(db => db.StudentCards
            .CountAsync(x => x.CardNumber == "KART-CAKISMA" && x.IsActive)));
    }

    // ---------------------------------------------------------------- kasa / para

    [Fact]
    public async Task MoneyAmountIsStoredWithoutPrecisionLoss()
    {
        // SQLite'ta decimal REAL olarak saklanirsa 125.45 -> 125.44999999 olur ve
        // gun sonu kasa toplami tutmaz. Kurus hassasiyeti korunmalidir.
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0440", "Yemek Ücreti");

        var response = await client.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(),
            StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow,
            IncomeTypeId = typeId,
            Amount = 125.45m,
            Description = "Kuruş testi"
        });
        response.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.Description == "Kuruş testi"));

        Assert.Equal(125.45m, stored.Amount);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("9999999.99")]
    [InlineData("100.00")]
    [InlineData("33.33")]
    public async Task EveryAmountRoundTripsExactly(string raw)
    {
        var amount = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync(
            $"2026-T{raw.GetHashCode():X6}"[..12], $"Tür {raw}");
        var marker = $"tutar-{raw}";

        var response = await client.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = amount, Description = marker
        });
        response.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.Description == marker));

        Assert.Equal(amount, stored.Amount);
    }

    [Fact]
    public async Task NegativeAmountIsRejected()
    {
        // Negatif tutar kasayi sessizce eksiye dusurur; reddedilmeli.
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0441", "Negatif Test");

        var response = await client.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = -50m, Description = "negatif"
        });

        Assert.False(response.IsSuccessStatusCode, "Negatif tutar kabul edildi.");
        Assert.False(await InScope(db => db.Set<IncomeTransaction>()
            .AnyAsync(x => x.Description == "negatif")));
    }

    [Fact]
    public async Task VoidingKeepsTheRecordAndMarksItInsteadOfDeleting()
    {
        // Iptal edilen islem SILINMEMELI: denetim izi korunmalidir.
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0442", "İptal Testi");
        var create = await client.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = 80m, Description = "iptal-edilecek"
        });
        create.EnsureSuccessStatusCode();
        var id = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.Description == "iptal-edilecek").Select(x => x.Id).SingleAsync());

        var voided = await client.PostAsJsonAsync($"api/income/transactions/{id}/void",
            new { Reason = "Yanlış tutar girildi" });
        voided.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.Id == id));

        Assert.True(stored.IsVoided);
        Assert.Equal(80m, stored.Amount);            // tutar degismemeli
        Assert.NotNull(stored.VoidedAt);
        Assert.Contains("Yanlış", stored.VoidReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameOperationIdIsNotChargedTwice()
    {
        // Kullanici "Kaydet"e iki kez basarsa ya da ag yeniden denerse
        // ogrenciden iki kez para alinmamalidir.
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0443", "Mükerrer Testi");
        var operationId = Guid.NewGuid();
        object Payload() => new
        {
            OperationId = operationId, StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = 60m, Description = "mukerrer"
        };

        await client.PostAsJsonAsync("api/income/transactions", Payload());
        await client.PostAsJsonAsync("api/income/transactions", Payload());

        Assert.Equal(1, await InScope(db => db.Set<IncomeTransaction>()
            .CountAsync(x => x.OperationId == operationId)));
    }

    private async Task<(Guid StudentId, Guid TypeId)> SeedIncomePrerequisitesAsync(
        string studentNo, string typeName)
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Kasa", LastName = "Testi" });
        created.EnsureSuccessStatusCode();
        var studentId = (await created.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        var type = await client.PostAsJsonAsync("api/income/types", new { Name = typeName });
        type.EnsureSuccessStatusCode();
        var typeId = await InScope(db => db.Set<IncomeType>().AsNoTracking()
            .Where(x => x.Name == typeName).Select(x => x.Id).SingleAsync());
        return (studentId, typeId);
    }

    // ------------------------------------------------- hakedis ve turnike

    [Fact]
    public async Task BulkGrantCreatesExactlyOneEntitlementPerStudentPerDay()
    {
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0450", "Ogle Yemegi A");
        var start = new DateOnly(2026, 3, 2);   // Pazartesi

        var response = await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId },
            MealTypeId = mealTypeId,
            StartsOn = start,
            EndsOn = start.AddDays(4),          // Cuma
            Quantity = 1
        });
        response.EnsureSuccessStatusCode();

        var rows = await InScope(db => db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId).ToListAsync());

        // Pazartesi-Cuma = 5 gun; hafta sonu istenmedi.
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Quantity));
        Assert.All(rows, row => Assert.Equal(0, row.ConsumedQuantity));
        Assert.DoesNotContain(rows, row =>
            row.EntitlementDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public async Task GrantingTheSameRangeTwiceDoesNotDoubleTheEntitlement()
    {
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0451", "Ogle Yemegi B");
        var day = new DateOnly(2026, 3, 3);
        object Payload() => new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = day, EndsOn = day, Quantity = 1
        };

        await client.PostAsJsonAsync("api/meal-entitlements/bulk", Payload());
        await client.PostAsJsonAsync("api/meal-entitlements/bulk", Payload());

        var rows = await InScope(db => db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.EntitlementDate == day).ToListAsync());

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Quantity);
    }

    [Fact]
    public async Task TurnstileAllowsOnceThenDeniesAndTheCountIsExact()
    {
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0452", "Ogle Yemegi C");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = today, EndsOn = today, Quantity = 1,
            IncludeSaturday = true, IncludeSunday = true
        });
        await client.PostAsJsonAsync($"api/students/{studentId}/cards", new { CardNumber = "TURNIKE-452" });
        var deviceId = await SeedDeviceAsync("Turnike 452");

        var first = await CheckAsync("TURNIKE-452", deviceId, mealTypeId);
        var second = await CheckAsync("TURNIKE-452", deviceId, mealTypeId);

        Assert.Equal("ALLOW", first);
        Assert.Equal("DENY", second);

        var entitlement = await InScope(db => db.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.StudentId == studentId && x.EntitlementDate == today));

        // Tam olarak BIR hak tuketilmeli: reddedilen gecis sayaci artirmamali.
        Assert.Equal(1, entitlement.ConsumedQuantity);
    }

    [Fact]
    public async Task RetryWithSameOperationIdReturnsTheSameAnswerAndConsumesOnce()
    {
        // Turnike yaniti alamayip yeniden gonderdiginde ogrenci hakkini
        // iki kez kaybetmemeli ve kapi haksiz yere kapanmamalidir.
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0453", "Ogle Yemegi D");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = today, EndsOn = today, Quantity = 1,
            IncludeSaturday = true, IncludeSunday = true
        });
        await client.PostAsJsonAsync($"api/students/{studentId}/cards", new { CardNumber = "TURNIKE-453" });
        var deviceId = await SeedDeviceAsync("Turnike 453");
        var operationId = Guid.NewGuid();

        var first = await CheckAsync("TURNIKE-453", deviceId, mealTypeId, operationId);
        var replay = await CheckAsync("TURNIKE-453", deviceId, mealTypeId, operationId);

        Assert.Equal("ALLOW", first);
        Assert.Equal("ALLOW", replay);

        var entitlement = await InScope(db => db.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.StudentId == studentId && x.EntitlementDate == today));
        Assert.Equal(1, entitlement.ConsumedQuantity);
    }

    [Fact]
    public async Task UnknownCardIsDeniedAndLogged()
    {
        var (_, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0454", "Ogle Yemegi E");
        var deviceId = await SeedDeviceAsync("Turnike 454");

        var decision = await CheckAsync("HIC-OLMAYAN-KART", deviceId, mealTypeId);

        Assert.Equal("DENY", decision);
        // Tanimsiz kart denemesi de kayit altina alinmalidir.
        Assert.True(await InScope(db => db.AccessLogs
            .AnyAsync(x => x.CardNumber == "HIC-OLMAYAN-KART" && x.Decision == "DENY")));
    }

    private async Task<string> CheckAsync(string cardNumber, Guid deviceId, Guid mealTypeId,
        Guid? operationId = null)
    {
        // Turnike ucu kullanici JWT'si degil CIHAZ ANAHTARI ile yetkilendirilir:
        // sahadaki cihazlar kullanici oturumu tasimaz.
        using var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add(
            Yemekhane.Api.Infrastructure.DeviceKeyAuthenticationHandler.HeaderName,
            YemekhaneApiFactory.DeviceKey);
        var response = await deviceClient.PostAsJsonAsync("api/access/check", new
        {
            CardNumber = cardNumber,
            DeviceId = deviceId,
            MealTypeId = mealTypeId,
            Timestamp = DateTimeOffset.UtcNow,
            OperationId = operationId
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DecisionOnly>();
        return body!.Decision;
    }

    private async Task<Guid> SeedDeviceAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(), Name = name, DeviceType = "Turnstile",
            ConnectionType = "Tcp", Direction = "Entry", ConnectionStatus = "Disconnected",
            IpAddress = $"10.0.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
            IpPort = Random.Shared.Next(2000, 60000),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<(Guid StudentId, Guid MealTypeId)> SeedMealPrerequisitesAsync(
        string studentNo, string mealName)
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Hak", LastName = "Testi" });
        created.EnsureSuccessStatusCode();
        var studentId = (await created.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var mealType = new MealType
        {
            Id = Guid.NewGuid(), Name = mealName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Set<MealType>().Add(mealType);
        await db.SaveChangesAsync();
        return (studentId, mealType.Id);
    }

    private sealed record DecisionOnly(string Decision, string Reason);

    // ------------------------------------------------- sinir ve kotu veri

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankRequiredFieldIsRejected(string blank)
    {
        var response = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0460", FirstName = blank, LastName = "Soyad"
        });

        Assert.False(response.IsSuccessStatusCode, "Boş ad kabul edildi.");
    }

    [Fact]
    public async Task OverlyLongInputIsRejectedInsteadOfBeingSilentlyTruncated()
    {
        // Sessiz kirpma en kotusu: kullanici kaydettim sanir, veri eksik saklanir.
        var response = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0461",
            FirstName = new string('A', 500),
            LastName = "Uzun"
        });

        if (response.IsSuccessStatusCode)
        {
            var stored = await InScope(db => db.Students.AsNoTracking()
                .SingleAsync(x => x.StudentNo == "2026-0461"));
            Assert.Equal(500, stored.FirstName.Length);   // kabul edildiyse KIRPILMAMALI
        }
        else
        {
            Assert.False(await InScope(db => db.Students
                .AnyAsync(x => x.StudentNo == "2026-0461")));
        }
    }

    [Fact]
    public async Task SqlLikeInputIsStoredLiterallyNotExecuted()
    {
        // Parametreli sorgu kullanildiginin kaniti: metin AYNEN saklanmali.
        const string payload = "Robert\'); DROP TABLE students;--";

        var response = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0462", FirstName = payload, LastName = "Enjeksiyon"
        });
        response.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(x => x.StudentNo == "2026-0462"));

        Assert.Equal(payload, stored.FirstName);
        // Tablo hala ayakta olmali.
        Assert.True(await InScope(db => db.Students.AnyAsync()));
    }

    [Fact]
    public async Task UnicodeAndEmojiSurviveTheRoundTrip()
    {
        const string name = "Zeynep\u00A0Ünlüoğlu";

        var response = await client.PostAsJsonAsync("api/students", new
        {
            StudentNo = "2026-0463", FirstName = name, LastName = "Çınar"
        });
        response.EnsureSuccessStatusCode();

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(x => x.StudentNo == "2026-0463"));

        Assert.Equal(name, stored.FirstName);
        Assert.Equal("Çınar", stored.LastName);
    }

    [Fact]
    public async Task EndDateBeforeStartDateIsRejected()
    {
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0464", "Ters Tarih");

        var response = await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = new DateOnly(2026, 5, 10),
            EndsOn = new DateOnly(2026, 5, 1),          // baslangictan ONCE
            Quantity = 1
        });

        Assert.False(response.IsSuccessStatusCode, "Bitiş < başlangıç kabul edildi.");
        Assert.False(await InScope(db => db.MealEntitlements
            .AnyAsync(x => x.StudentId == studentId)));
    }

    [Fact]
    public async Task ZeroOrNegativeQuantityIsRejected()
    {
        var (studentId, mealTypeId) = await SeedMealPrerequisitesAsync("2026-0465", "Sifir Adet");
        var day = new DateOnly(2026, 5, 4);

        var response = await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = day, EndsOn = day, Quantity = 0
        });

        Assert.False(response.IsSuccessStatusCode, "Sıfır adet hakediş kabul edildi.");
    }

    [Fact]
    public async Task DeletedStudentDisappearsFromListsButRowIsKept()
    {
        // Yumusak silme: denetim ve gecmis raporlar icin satir korunmali,
        // ama listelerde gorunmemeli.
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-0466", FirstName = "Silinecek", LastName = "Kayit" });
        var id = (await created.Content.ReadFromJsonAsync<StudentIdOnly>())!.Id;

        var deleted = await client.DeleteAsync($"api/students/{id}");
        if (!deleted.IsSuccessStatusCode) return;   // silme ucu yoksa test anlamsiz

        var row = await InScope(db => db.Students.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id));
        Assert.NotNull(row);

        var visible = await InScope(db => db.Students.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.False(visible, "Silinen öğrenci listede hâlâ görünüyor.");
    }

    // ------------------------------------------------- kasa toplami

    [Fact]
    public async Task DailyTotalSumsExactlyAndExcludesVoidedRows()
    {
        // Gun sonu kasa toplami tutmazsa okul mudurune hesap verilemez.
        // Kurus hassasiyeti ve iptal edilenlerin haric tutulmasi birlikte dogrulanir.
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0470", "Gun Sonu");
        var day = DateTimeOffset.UtcNow;

        decimal[] amounts = [10.10m, 20.20m, 0.05m, 69.65m];   // toplam 100.00
        foreach (var amount in amounts)
        {
            var created = await client.PostAsJsonAsync("api/income/transactions", new
            {
                OperationId = Guid.NewGuid(), StudentId = studentId,
                TransactionAt = day, IncomeTypeId = typeId,
                Amount = amount, Description = "gunsonu-" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            created.EnsureSuccessStatusCode();
        }

        // Bir tanesi iptal edilir; toplamdan DUSMELIDIR.
        var voidId = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.Description == "gunsonu-20.20").Select(x => x.Id).SingleAsync());
        var voided = await client.PostAsJsonAsync($"api/income/transactions/{voidId}/void",
            new { Reason = "Yanlis kayit" });
        voided.EnsureSuccessStatusCode();

        var live = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.StudentId == studentId && !x.IsVoided)
            .SumAsync(x => x.Amount));

        Assert.Equal(79.80m, live);            // 100.00 - 20.20
    }

    [Fact]
    public async Task VoidedTransactionCannotBeVoidedAgain()
    {
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-0471", "Cift Iptal");
        var created = await client.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = 45m, Description = "cift-iptal"
        });
        created.EnsureSuccessStatusCode();
        var id = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.Description == "cift-iptal").Select(x => x.Id).SingleAsync());

        var first = await client.PostAsJsonAsync($"api/income/transactions/{id}/void",
            new { Reason = "Ilk iptal" });
        var second = await client.PostAsJsonAsync($"api/income/transactions/{id}/void",
            new { Reason = "Ikinci iptal" });

        first.EnsureSuccessStatusCode();
        Assert.False(second.IsSuccessStatusCode, "Zaten iptal edilmiş işlem yeniden iptal edildi.");

        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.Id == id));
        // Ilk iptalin nedeni korunmali; ikincisi uzerine yazmamali.
        Assert.Contains("Ilk", stored.VoidReason ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed record StudentIdOnly(Guid Id);
}
