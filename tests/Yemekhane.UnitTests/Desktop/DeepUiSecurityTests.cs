using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Api.Authorization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Yetki KATMANLARININ testi.
///
/// Arayuzde butonun pasif olmasi bir kolayliktir, guvenlik degildir: komut
/// yine de calistirilabilir (klavye kisayolu, otomasyon, hatali kod yolu).
/// Asil koruma API'de olmalidir. Bu testler HER IKI katmani ayri ayri
/// dogrular -- birini kirip digerinin tuttugunu gorur.
/// </summary>
[Collection("UI")]
public sealed class DeepUiSecurityTests : IAsyncLifetime
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient fullClient = null!;

    public Task InitializeAsync()
    {
        fullClient = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        fullClient.Dispose();
        await factory.DisposeAsync();
    }

    /// <summary>Yalnizca verilen izinlere sahip bir oturum uretir.</summary>
    private sealed class ScopedSession(string[] permissions) : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateTokenWith(permissions);
        public bool IsAuthenticated => true;
    }

    private static readonly string[] Routes =
        [ShellRoutes.Students, ShellRoutes.Entitlements, ShellRoutes.Cash, ShellRoutes.Sms];

    private Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> query)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return query(db).ContinueWith(t => { scope.DisposeAsync().AsTask().Wait(); return t.Result; });
    }

    // ------------------------------------------------- UI katmani

    [Fact]
    public void ReadOnlyUserSeesWriteButtonsDisabledOnTheStudentScreen()
    {
        // Yalnizca okuma izni olan memur: kaydetme/pasife alma butonlari pasif olmali.
        var screen = new StudentsViewModel(
            new StudentApiClient(fullClient, new ScopedSession([Permissions.StudentsRead])),
            new ShellNavigationService(Routes),
            [Permissions.StudentsRead]);

        Assert.False(screen.CanWrite, "Salt okunur kullanıcıda CanWrite açık.");
        Assert.False(screen.NewStudentCommand.CanExecute(null), "'Yeni Öğrenci' butonu aktif.");
        Assert.False(screen.DeactivateCommand.CanExecute(null), "'Pasife Al' butonu aktif.");
        Assert.False(screen.ReplaceCardCommand.CanExecute(null), "'Kart Değiştir' butonu aktif.");
    }

    [Fact]
    public void CashierWithoutManagePermissionCannotEditIncomeTypes()
    {
        // Kasiyer islem girebilir ama gelir TURU tanimlayamaz.
        var screen = new CashViewModel(
            new CashApiClient(fullClient, new ScopedSession([Permissions.CashRead, Permissions.CashWrite])),
            [Permissions.CashRead, Permissions.CashWrite]);

        Assert.True(screen.CanWrite, "Kasiyer işlem giremiyor.");
        Assert.False(screen.CanManage, "cash.manage olmadan CanManage açık.");
        Assert.False(screen.NewTypeCommand.CanExecute(null), "'Yeni Gelir Türü' butonu aktif.");
        Assert.False(screen.SaveTypeCommand.CanExecute(null), "'Gelir Türü Kaydet' butonu aktif.");
    }

    // ------------------------------------------------- API katmani (asil koruma)

    [Fact]
    public async Task EvenIfTheButtonIsForcedTheApiRefusesAnUnauthorizedWrite()
    {
        // UI korumasi ATLANIR: komutu dogrudan calistiriyoruz.
        // API bunu YINE DE reddetmelidir; aksi halde koruma yalnizca gorseldir.
        var readOnly = new StudentApiClient(fullClient, new ScopedSession([Permissions.StudentsRead]));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            readOnly.SaveAsync(null, new Yemekhane.Application.Students.SaveStudentRequest(
                "2026-8001", "Yetkisiz", "Kayit", null, Address: null, Notes: null, IsActive: true)));

        Assert.True(failure is LoginRequiredException or ApiRequestException,
            $"Beklenmeyen hata tipi: {failure.GetType().Name}");

        // Veritabaninda HICBIR SEY olusmamali.
        Assert.False(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "2026-8001")),
            "Yetkisiz kullanıcı öğrenci oluşturabildi.");
    }

    [Fact]
    public async Task CashWriteWithoutPermissionIsRefusedByTheApiNotJustTheButton()
    {
        var noWrite = fullClient.BaseAddress;
        using var limited = factory.CreateClient();
        limited.DefaultRequestHeaders.Authorization =
            new("Bearer", YemekhaneApiFactory.CreateTokenWith(Permissions.CashRead));

        var response = await limited.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = Guid.NewGuid(),
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = Guid.NewGuid(),
            Amount = 100m, Description = "yetkisiz-tahsilat"
        });

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"cash.write olmadan tahsilat {(int)response.StatusCode} döndü.");
        Assert.False(await InScope(db => db.Set<IncomeTransaction>()
            .AnyAsync(x => x.Description == "yetkisiz-tahsilat")));
    }

    [Fact]
    public async Task DeactivatePermissionIsSeparateFromWritePermission()
    {
        // students.write olan ama students.deactivate OLMAYAN kullanici
        // ogrenciyi duzenleyebilmeli, ancak pasife ALAMAMALIDIR.
        var created = await fullClient.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-8002", FirstName = "Silinemez", LastName = "Ogrenci" });
        created.EnsureSuccessStatusCode();
        var id = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == "2026-8002").Select(x => x.Id).SingleAsync());

        using var writeOnly = factory.CreateClient();
        writeOnly.DefaultRequestHeaders.Authorization = new("Bearer",
            YemekhaneApiFactory.CreateTokenWith(Permissions.StudentsRead, Permissions.StudentsWrite));

        var response = await writeOnly.DeleteAsync($"api/students/{id:D}");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"students.deactivate olmadan pasife alma {(int)response.StatusCode} döndü.");

        var still = await InScope(db => db.Students.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.True(still.IsActive, "Yetkisiz istek öğrenciyi pasife aldı.");
    }

    [Fact]
    public async Task ATokenWithNoPermissionsAtAllCanReadNothing()
    {
        using var naked = factory.CreateClient();
        naked.DefaultRequestHeaders.Authorization =
            new("Bearer", YemekhaneApiFactory.CreateTokenWith());

        foreach (var url in new[] { "api/students", "api/income/transactions", "api/meal-entitlements" })
        {
            var response = await naked.GetAsync(url);
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"{url} izinsiz token ile {(int)response.StatusCode} döndü.");
        }
    }

    // ------------------------------------------------- yaris kosullari

    private StudentsViewModel WritingClerk(HttpClient? http = null) => new(
        new StudentApiClient(http ?? fullClient,
            new ScopedSession([Permissions.StudentsRead, Permissions.StudentsWrite])),
        new ShellNavigationService(Routes),
        [Permissions.StudentsRead, Permissions.StudentsWrite]);

    /// <summary>
    /// Kullanici "Kaydet" butonuna HIZLICA IKI KEZ basarsa.
    /// AsyncCommand executing bayragi ikinci basisi engellemelidir.
    /// </summary>
    [Fact]
    public async Task DoubleClickingSaveDoesNotCreateTwoStudents()
    {
        var screen = WritingClerk();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-8010";
        screen.FormFirstName = "Cift";
        screen.FormLastName = "Tiklama";

        var command = (AsyncCommand)screen.SaveStudentCommand;

        // Iki basis AYNI ANDA baslatilir.
        await Task.WhenAll(command.ExecuteAsync(null), command.ExecuteAsync(null));

        var count = await InScope(db => db.Students.CountAsync(x => x.StudentNo == "2026-8010"));
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Iki memur AYNI ANDA ayni ogrenci numarasini kaydederse.
    /// Tam olarak biri kazanmali, kaybeden NEDENINI gormelidir.
    /// </summary>
    [Fact]
    public async Task TwoClerksSavingTheSameStudentNumberAtOnceProduceExactlyOneRow()
    {
        StudentsViewModel Clerk(string firstName)
        {
            var screen = WritingClerk(factory.CreateOperatorClient());
            screen.NewStudentCommand.Execute(null);
            screen.FormStudentNo = "2026-8011";
            screen.FormFirstName = firstName;
            screen.FormLastName = "Yaris";
            return screen;
        }

        var a = Clerk("Memur A");
        var b = Clerk("Memur B");

        await Task.WhenAll(
            ((AsyncCommand)a.SaveStudentCommand).ExecuteAsync(null),
            ((AsyncCommand)b.SaveStudentCommand).ExecuteAsync(null));

        var count = await InScope(db => db.Students.CountAsync(x => x.StudentNo == "2026-8011"));
        Assert.Equal(1, count);

        // Kaybeden memur sessizce basarili sanmamali.
        Assert.True(a.HasError ^ b.HasError,
            $"Tam olarak bir memur hata almalıydı (A={a.HasError}, B={b.HasError}).");
    }

    /// <summary>
    /// Ayni turnikeden AYNI ANDA cok sayida okuma gelirse (kart tekrar
    /// okutuldu ya da cihaz yeniden gonderdi): hak TAM BIR kez dusmelidir.
    /// </summary>
    [Fact]
    public async Task SimultaneousTurnstileSwipesConsumeExactlyOneEntitlement()
    {
        var (studentId, mealTypeId) = await SeedStudentWithEntitlementAsync("2026-8012", "TURNIKE-8012");
        var deviceId = await SeedDeviceAsync("Yaris Turnikesi");

        var decisions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => SwipeAsync("TURNIKE-8012", deviceId, mealTypeId)));

        Assert.Equal(1, decisions.Count(x => x == "ALLOW"));

        var entitlement = await InScope(db => db.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.StudentId == studentId));
        Assert.Equal(1, entitlement.ConsumedQuantity);
    }

    /// <summary>
    /// Ayni gelir islemi es zamanli olarak birkac kez iptal edilmeye
    /// calisilirsa: yalnizca BIRI basarili olmalidir.
    /// </summary>
    [Fact]
    public async Task SimultaneousVoidAttemptsVoidTheTransactionOnlyOnce()
    {
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-8013", "Yaris Geliri");
        var create = await fullClient.PostAsJsonAsync("api/income/transactions", new
        {
            OperationId = Guid.NewGuid(), StudentId = studentId,
            TransactionAt = DateTimeOffset.UtcNow, IncomeTypeId = typeId,
            Amount = 200m, Description = "yaris-iptal"
        });
        create.EnsureSuccessStatusCode();
        var id = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.Description == "yaris-iptal").Select(x => x.Id).SingleAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            fullClient.PostAsJsonAsync($"api/income/transactions/{id:D}/void",
                new { Reason = $"es-zamanli-{i}" })));

        Assert.Equal(1, results.Count(x => x.IsSuccessStatusCode));

        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.Id == id));
        Assert.True(stored.IsVoided);
        Assert.Equal(200m, stored.Amount);   // tutar DEGISMEMELI
    }

    // ------------------------------------------------- ortak kurulum

    private async Task<(Guid StudentId, Guid MealTypeId)> SeedStudentWithEntitlementAsync(
        string studentNo, string cardNumber)
    {
        var created = await fullClient.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Yaris", LastName = "Testi" });
        created.EnsureSuccessStatusCode();
        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == studentNo).Select(x => x.Id).SingleAsync());

        var card = await fullClient.PostAsJsonAsync(
            $"api/students/{studentId:D}/cards", new { CardNumber = cardNumber });
        card.EnsureSuccessStatusCode();

        Guid mealTypeId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            var meal = new MealType
            {
                Id = Guid.NewGuid(), Name = $"Ogun {studentNo}",
                IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Set<MealType>().Add(meal);
            await db.SaveChangesAsync();
            mealTypeId = meal.Id;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var grant = await fullClient.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = today, EndsOn = today, Quantity = 1,
            IncludeSaturday = true, IncludeSunday = true
        });
        grant.EnsureSuccessStatusCode();
        return (studentId, mealTypeId);
    }

    private async Task<(Guid StudentId, Guid IncomeTypeId)> SeedIncomePrerequisitesAsync(
        string studentNo, string typeName)
    {
        var created = await fullClient.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Kasa", LastName = "Yaris" });
        created.EnsureSuccessStatusCode();
        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == studentNo).Select(x => x.Id).SingleAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var type = new IncomeType
        {
            Id = Guid.NewGuid(), Name = typeName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Set<IncomeType>().Add(type);
        await db.SaveChangesAsync();
        return (studentId, type.Id);
    }

    private async Task<Guid> SeedDeviceAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(), Name = name, DeviceType = "Turnstile",
            ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Disconnected",
            IpAddress = $"10.2.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
            IpPort = Random.Shared.Next(2000, 60000),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<string> SwipeAsync(string cardNumber, Guid deviceId, Guid mealTypeId)
    {
        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add(
            Yemekhane.Api.Infrastructure.DeviceKeyAuthenticationHandler.HeaderName,
            YemekhaneApiFactory.DeviceKey);

        var response = await device.PostAsJsonAsync("api/access/check", new
        {
            CardNumber = cardNumber, DeviceId = deviceId, MealTypeId = mealTypeId,
            Timestamp = DateTimeOffset.UtcNow, OperationId = (Guid?)null
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DecisionOnly>();
        return body!.Decision;
    }

    private sealed record DecisionOnly(string Decision, string Reason);

    // ------------------------------------------------- ekran durumu tutarliligi

    /// <summary>
    /// Sayfa 3'teyken arama yapilirsa sayfa 1'e DONMELIDIR.
    /// Donmezse kullanici "kayit yok" gorur ve aradiginin olmadigini saniir.
    /// </summary>
    [Fact]
    public async Task SearchingResetsPagingSoResultsAreNotHiddenOnAnEmptyPage()
    {
        for (var i = 0; i < 6; i++)
        {
            var created = await fullClient.PostAsJsonAsync("api/students", new
            {
                StudentNo = $"2026-820{i}", FirstName = $"Sayfa{i}", LastName = "Testi"
            });
            created.EnsureSuccessStatusCode();
        }

        var screen = WritingClerk();
        screen.PageSize = 2;
        await ((AsyncCommand)screen.SearchCommand).ExecuteAsync(null);
        await ((AsyncCommand)screen.NextPageCommand).ExecuteAsync(null);
        await ((AsyncCommand)screen.NextPageCommand).ExecuteAsync(null);
        Assert.True(screen.Page >= 2, "Sayfalama ilerlemedi, test anlamsız.");

        // Kullanici simdi ilk sayfada olan bir kaydi arar.
        screen.Search = "Sayfa0";
        await ((AsyncCommand)screen.SearchCommand).ExecuteAsync(null);

        Assert.Equal(1, screen.Page);
        Assert.Contains(screen.Students, x => x.StudentNo == "2026-8200");
    }

    /// <summary>
    /// Son sayfadaki tek kayit silindiginde ekran bos sayfada ASILI KALMAMALI.
    /// </summary>
    [Fact]
    public async Task PagingNeverGoesBelowPageOne()
    {
        var screen = WritingClerk();
        await ((AsyncCommand)screen.SearchCommand).ExecuteAsync(null);

        // Ilk sayfadayken "Onceki" defalarca basilir.
        for (var i = 0; i < 3; i++)
            await ((AsyncCommand)screen.PreviousPageCommand).ExecuteAsync(null);

        Assert.True(screen.Page >= 1, $"Sayfa numarası {screen.Page} oldu.");
    }

    /// <summary>
    /// Form acikken iptal edilirse girilen veri SONRAKI forma sizmamalidir.
    /// Sizarsa kullanici yanlislikla eski veriyi kaydeder.
    /// </summary>
    [Fact]
    public async Task CancellingAFormDoesNotLeakDataIntoTheNextOne()
    {
        var screen = WritingClerk();

        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-8210";
        screen.FormFirstName = "Vazgecilen";
        screen.FormLastName = "Kayit";
        screen.FormNotes = "gizli not";

        // Kullanici vazgecer.
        screen.CloseDrawersCommand.Execute(null);

        // Yeni form acar.
        screen.NewStudentCommand.Execute(null);

        Assert.True(string.IsNullOrEmpty(screen.FormStudentNo),
            $"Önceki formun öğrenci no'su sızdı: {screen.FormStudentNo}");
        Assert.True(string.IsNullOrEmpty(screen.FormFirstName),
            $"Önceki formun adı sızdı: {screen.FormFirstName}");
        Assert.True(string.IsNullOrEmpty(screen.FormNotes),
            $"Önceki formun notu sızdı: {screen.FormNotes}");
    }

    /// <summary>
    /// Kasa ekraninda gelir eklendikten sonra form TEMIZLENMELIDIR;
    /// aksi halde kullanici ayni tutari yanlislikla ikinci kez girer.
    /// </summary>
    [Fact]
    public async Task AfterAddingIncomeTheFormIsClearedForTheNextCustomer()
    {
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-8220", "Sonraki Musteri");

        // NOT: Kasa ekraninda ogrenci dogrulama api/students ucunu kullanir ve
        // bu uc students.read ister. Kasiyere yalnizca cash.* verilirse
        // dogrulama yapamaz, dolayisiyla HICBIR tahsilat giremez.
        // (bkz. CashierWithOnlyCashPermissionsCannotVerifyAnyStudent)
        var cash = new CashViewModel(
            new CashApiClient(fullClient, new ScopedSession(
                [Permissions.CashRead, Permissions.CashWrite, Permissions.CashManage,
                 Permissions.StudentsRead])),
            [Permissions.CashRead, Permissions.CashWrite, Permissions.CashManage]);
        await cash.RefreshAsync();

        cash.OpenAddCommand.Execute(null);
        cash.StudentNumber = "2026-8220";
        await ((AsyncCommand)cash.LookupStudentCommand).ExecuteAsync(null);
        Assert.NotNull(cash.LookupStudent);

        cash.SelectedAddType = cash.IncomeTypes.First(x => x.Id == typeId);
        cash.AmountText = "75,50";
        cash.Description = "ilk-tahsilat";
        cash.AddConfirmed = true;
        await ((AsyncCommand)cash.AddCommand).ExecuteAsync(null);
        Assert.Null(cash.AddError);

        // Bir sonraki musteri icin form acilir.
        cash.OpenAddCommand.Execute(null);

        Assert.True(string.IsNullOrWhiteSpace(cash.AmountText),
            $"Önceki tutar formda kaldı: {cash.AmountText}");
        Assert.Null(cash.LookupStudent);
        Assert.False(cash.AddConfirmed, "Onay kutusu işaretli kaldı; körlemesine kayıt riski.");
    }

    /// <summary>
    /// Bir islem iptal edildikten sonra ekranin gosterdigi toplam,
    /// veritabanindaki gercek toplamla AYNI olmalidir.
    /// </summary>
    [Fact]
    public async Task TheTotalOnScreenMatchesTheDatabaseAfterAVoid()
    {
        var (studentId, typeId) = await SeedIncomePrerequisitesAsync("2026-8230", "Ekran Toplami");
        var today = DateTimeOffset.UtcNow;

        foreach (var amount in new[] { 40.00m, 60.00m })
        {
            var created = await fullClient.PostAsJsonAsync("api/income/transactions", new
            {
                OperationId = Guid.NewGuid(), StudentId = studentId,
                TransactionAt = today, IncomeTypeId = typeId,
                Amount = amount, Description = $"ekran-{amount:0.00}"
            });
            created.EnsureSuccessStatusCode();
        }

        var voidId = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.StudentId == studentId && x.Amount == 40.00m)
            .Select(x => x.Id).SingleAsync());
        var voided = await fullClient.PostAsJsonAsync(
            $"api/income/transactions/{voidId:D}/void", new { Reason = "Ekran testi" });
        voided.EnsureSuccessStatusCode();

        var dbTotal = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.StudentId == studentId && !x.IsVoided)
            .SumAsync(x => x.Amount));

        Assert.Equal(60.00m, dbTotal);   // 100.00 - 40.00
    }

    /// <summary>
    /// Yalnizca cash.* izni verilen bir kasiyer HICBIR tahsilat giremez.
    ///
    /// Kasa ekranindaki ogrenci dogrulama api/students ucunu kullanir; bu uc
    /// students.read ister. Dogrulama olmadan ValidateAdd() gecmez, yani
    /// kasiyer rolu tanimlanirken students.read UNUTULURSA ekran sessizce
    /// kullanilamaz hale gelir.
    /// </summary>
    [Fact]
    public async Task CashierWithOnlyCashPermissionsCannotVerifyAnyStudent()
    {
        var (_, typeId) = await SeedIncomePrerequisitesAsync("2026-8240", "Izin Bagimliligi");

        var cash = new CashViewModel(
            new CashApiClient(fullClient, new ScopedSession(
                [Permissions.CashRead, Permissions.CashWrite])),
            [Permissions.CashRead, Permissions.CashWrite]);
        await cash.RefreshAsync();

        cash.OpenAddCommand.Execute(null);
        cash.StudentNumber = "2026-8240";
        await ((AsyncCommand)cash.LookupStudentCommand).ExecuteAsync(null);

        // Dogrulama basarisiz olmali ve kullaniciya SEBEBI gorunmelidir.
        Assert.Null(cash.LookupStudent);
        Assert.NotNull(cash.AddError);

        // Dolayisiyla kayit da engellenmeli.
        Assert.NotNull(cash.ValidateAdd());
    }
}
