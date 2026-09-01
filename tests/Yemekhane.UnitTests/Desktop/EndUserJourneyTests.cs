using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;
using Yemekhane.Application.Cash;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Son kullanici testleri: ARAYUZDEN veri girilir, GERCEK veritabani kontrol edilir.
///
/// Buradaki testler API'yi dogrudan cagirmaz. Kullanicinin yaptigini yapar:
/// ekranin bagli oldugu ozelligi yazar, butonun komutunu calistirir. Aradaki
/// her katman -- baglama, komut, HTTP istemcisi, denetleyici, veritabani --
/// gercektir. Boylece "her katman tek basina calisiyor ama arasindaki kablo
/// yanlis" hatalari yakalanir.
/// </summary>
[Collection("UI")]
public sealed class EndUserJourneyTests : IAsyncLifetime, IDisposable
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

    /// <summary>
    /// xUnit bazi hata senaryolarinda DisposeAsync'i atlayabilir; fabrika
    /// atilmazsa web sunucusu ve SQLite havuzu sizar ve test host'u kilitlenir.
    /// </summary>
    public void Dispose() => factory.Dispose();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    private static readonly string[] AllRoutes =
        [ShellRoutes.Students, ShellRoutes.Entitlements, ShellRoutes.Sms, ShellRoutes.Cash];

    private StudentsViewModel NewStudentsScreen() => new(
        new StudentApiClient(client, new OperatorSession()),
        new ShellNavigationService(AllRoutes),
        ["students.read", "students.write", "students.deactivate", "cards.manage"]);

    private Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> query)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return query(db).ContinueWith(t => { scope.DisposeAsync().AsTask().Wait(); return t.Result; });
    }

    [Fact]
    public async Task UserFillsTheFormClicksSaveAndTheStudentIsInTheDatabase()
    {
        var screen = NewStudentsScreen();

        // Kullanici "Yeni Ogrenci" butonuna basar.
        screen.NewStudentCommand.Execute(null);
        Assert.True(screen.IsFormOpen, "Yeni öğrenci butonu formu açmadı.");

        // Kullanici alanlari doldurur.
        screen.FormStudentNo = "2026-9001";
        screen.FormFirstName = "Çağrı";
        screen.FormLastName = "Şahinoğlu";

        // Kullanici "Kaydet" butonuna basar.
        await Execute(screen.SaveStudentCommand);

        // Veritabaninda GERCEKTEN olmali.
        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleOrDefaultAsync(x => x.StudentNo == "2026-9001"));

        Assert.NotNull(stored);
        Assert.Equal("Çağrı", stored!.FirstName);
        Assert.Equal("Şahinoğlu", stored.LastName);
    }

    /// <summary>
    /// Butona basmayi taklit eder. AsyncCommand hatalari UnhandledError olayina
    /// yonlendirir; test bunu YUTMAMALIDIR, yoksa "kaydettim" der ama kaydetmez.
    /// </summary>
    private static async Task Execute(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "Komut çalıştırılabilir değil (buton pasif).");

        Exception? escaped = null;
        void Capture(object? _, Exception error) => escaped = error;
        AsyncCommand.UnhandledError += Capture;
        try
        {
            if (command is AsyncCommand asyncCommand) await asyncCommand.ExecuteAsync(null);
            else command.Execute(null);
        }
        finally { AsyncCommand.UnhandledError -= Capture; }

        if (escaped is not null)
            Assert.Fail($"Buton komutu hata firlatti: {escaped.GetType().Name}: {escaped.Message}");
    }

    /// <summary>Giris yapmis operatorun oturumu -- gercek JWT tasir.</summary>
    // ------------------------------------------------- ogrenci ekrani

    [Fact]
    public async Task SavingWithAnEmptyNameShowsAnErrorAndWritesNothing()
    {
        var screen = NewStudentsScreen();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-9002";
        screen.FormFirstName = "   ";              // kullanici bosluk birakti
        screen.FormLastName = "Soyad";

        await Execute(screen.SaveStudentCommand);

        // Ekranda hata gorunmeli...
        Assert.True(screen.HasError, "Boş ad için ekranda hata mesajı yok.");
        // ...ve veritabanina HICBIR SEY yazilmamali.
        Assert.False(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "2026-9002")));
        // Form acik kalmali ki kullanici duzeltebilsin.
        Assert.True(screen.IsFormOpen, "Hatalı kayıtta form kapandı, kullanıcı verisini kaybetti.");
    }

    [Fact]
    public async Task TheSavedStudentAppearsInTheGridWithoutARestart()
    {
        // Kaydettikten sonra listede gorunmezse kullanici "kaydolmadi" saniyor.
        var screen = NewStudentsScreen();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-9003";
        screen.FormFirstName = "Listede";
        screen.FormLastName = "Gorunmeli";

        await Execute(screen.SaveStudentCommand);

        Assert.Contains(screen.Students, row => row.StudentNo == "2026-9003");
        Assert.False(screen.IsFormOpen, "Başarılı kayıttan sonra form kapanmadı.");
    }

    [Fact]
    public async Task SearchingByNameFindsTheStudentTheUserJustAdded()
    {
        var screen = NewStudentsScreen();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-9004";
        screen.FormFirstName = "Arama";
        screen.FormLastName = "Testi";
        await Execute(screen.SaveStudentCommand);

        // Kullanici arama kutusuna yazip Ara butonuna basar.
        screen.Search = "Arama";
        await Execute(screen.SearchCommand);

        Assert.Contains(screen.Students, row => row.StudentNo == "2026-9004");
    }

    [Fact]
    public async Task DuplicateStudentNumberIsRefusedWithAMessageNotACrash()
    {
        var screen = NewStudentsScreen();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = "2026-9005";
        screen.FormFirstName = "Ilk";
        screen.FormLastName = "Kayit";
        await Execute(screen.SaveStudentCommand);

        // Ayni numara ile ikinci kez.
        var second = NewStudentsScreen();
        second.NewStudentCommand.Execute(null);
        second.FormStudentNo = "2026-9005";
        second.FormFirstName = "Ikinci";
        second.FormLastName = "Kayit";
        await Execute(second.SaveStudentCommand);

        Assert.True(second.HasError, "Çift öğrenci numarası sessizce kabul edildi.");
        // Kullanici NEDEN basarisiz oldugunu gormeli; "bir hata olustu" yeterli degil.
        Assert.Contains("numara", second.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var count = await InScope(db => db.Students.CountAsync(x => x.StudentNo == "2026-9005"));
        Assert.Equal(1, count);
        // Form acik kalmali ki kullanici numarayi duzeltebilsin.
        Assert.True(second.IsFormOpen, "Hata sonrası form kapandı, girilen veri kayboldu.");
    }

    // ------------------------------------------------- kasa ekrani

    private CashViewModel NewCashScreen() => new(
        new CashApiClient(client, new OperatorSession()),
        ["cash.read", "cash.write", "cash.manage"]);

    /// <summary>Kasa ekraninin calismasi icin gereken ogrenci + gelir turunu hazirlar.</summary>
    private async Task<CashViewModel> OpenCashWithStudentAsync(string studentNo, string typeName)
    {
        var students = NewStudentsScreen();
        students.NewStudentCommand.Execute(null);
        students.FormStudentNo = studentNo;
        students.FormFirstName = "Kasa";
        students.FormLastName = "Musterisi";
        await Execute(students.SaveStudentCommand);

        var cash = NewCashScreen();
        await Execute(cash.RefreshCommand);

        // Gelir turu yoksa olustur (yonetici ekranindan yapilan is).
        if (cash.IncomeTypes.Count == 0)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            db.Set<IncomeType>().Add(new IncomeType
            {
                Id = Guid.NewGuid(), Name = typeName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await Execute(cash.RefreshCommand);
        }

        // Kullanici "Gelir Ekle" butonuna basar.
        cash.OpenAddCommand.Execute(null);
        Assert.True(cash.IsAddOpen, "Gelir ekle butonu formu açmadı.");

        // Ogrenci numarasini yazip "Doğrula" butonuna basar.
        cash.StudentNumber = studentNo;
        await Execute(cash.LookupStudentCommand);
        Assert.NotNull(cash.LookupStudent);

        cash.SelectedAddType = cash.IncomeTypes.First(x => x.IsActive);
        return cash;
    }

    [Fact]
    public async Task ClerkTypesAnAmountAndTheExactKurusReachesTheDatabase()
    {
        var cash = await OpenCashWithStudentAsync("2026-9010", "Yemek Ucreti");

        // Kullanici tutari TURKCE bicimde yazar: virgul ondalik ayraci.
        cash.AmountText = "125,45";
        cash.Description = "Eylul taksiti";
        cash.AddConfirmed = true;                 // onay kutusu isaretlenir

        await Execute(cash.AddCommand);

        Assert.Null(cash.AddError);
        var stored = await InScope(db => db.Set<IncomeTransaction>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Description == "Eylul taksiti"));

        Assert.NotNull(stored);
        // Kurus TAM olmali: 125,45 -> 125.45m
        Assert.Equal(125.45m, stored!.Amount);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12,345")]      // uc ondalik
    [InlineData("-50")]
    [InlineData("0")]
    public async Task InvalidAmountIsRefusedWithAMessageAndNothingIsWritten(string typed)
    {
        var cash = await OpenCashWithStudentAsync($"2026-901{Math.Abs(typed.GetHashCode()) % 9 + 1}", "Yemek Ucreti");
        var before = await InScope(db => db.Set<IncomeTransaction>().CountAsync());

        cash.AmountText = typed;
        cash.Description = $"gecersiz-{typed}";
        cash.AddConfirmed = true;

        await Execute(cash.AddCommand);   // COKMEMELI

        Assert.NotNull(cash.AddError);
        var after = await InScope(db => db.Set<IncomeTransaction>().CountAsync());
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task WithoutTickingTheConfirmBoxTheSaveButtonStaysDisabled()
    {
        // Para kaydinda onay kutusu bir guvenlik agidir; isaretlenmeden buton pasif kalmali.
        var cash = await OpenCashWithStudentAsync("2026-9020", "Yemek Ucreti");
        cash.AmountText = "50,00";
        cash.AddConfirmed = false;

        Assert.False(cash.AddCommand.CanExecute(null),
            "Onay kutusu işaretlenmeden kaydet butonu aktif.");
    }

    [Fact]
    public async Task AnUnknownStudentNumberIsReportedAndBlocksTheSave()
    {
        var cash = NewCashScreen();
        await Execute(cash.RefreshCommand);
        cash.OpenAddCommand.Execute(null);

        cash.StudentNumber = "BOYLE-BIR-OGRENCI-YOK";
        await Execute(cash.LookupStudentCommand);

        Assert.Null(cash.LookupStudent);
        Assert.NotNull(cash.AddError);
        // Dogrulanmamis ogrenciyle kayit yapilamamali.
        Assert.NotNull(cash.ValidateAdd());
    }

    // ------------------------------------------------- hakedis ekrani

    private MealEntitlementsViewModel NewEntitlementsScreen() => new(
        new MealEntitlementApiClient(client, new OperatorSession()),
        ["entitlements.manage", "entitlements.bulk"]);

    private async Task<(Guid StudentId, Guid MealTypeId)> SeedStudentAndMealAsync(
        string studentNo, string mealName)
    {
        var students = NewStudentsScreen();
        students.NewStudentCommand.Execute(null);
        students.FormStudentNo = studentNo;
        students.FormFirstName = "Hakedis";
        students.FormLastName = "Alan";
        await Execute(students.SaveStudentCommand);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var studentId = await db.Students.Where(x => x.StudentNo == studentNo)
            .Select(x => x.Id).SingleAsync();
        var meal = new MealType
        {
            Id = Guid.NewGuid(), Name = mealName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Set<MealType>().Add(meal);
        await db.SaveChangesAsync();
        return (studentId, meal.Id);
    }

    [Fact]
    public async Task GrantingEntitlementsRequiresAPreviewFirst()
    {
        // Yuzlerce ogrenciyi etkileyen islem, kullanici ONIZLEMEDEN uygulanmamalidir.
        var screen = NewEntitlementsScreen();
        screen.OpenGrantCommand.Execute(null);

        Assert.False(screen.ApplyCommand.CanExecute(null),
            "Önizleme yapılmadan 'Uygula' butonu aktif; toplu işlem körlemesine çalıştırılabilir.");
    }

    [Fact]
    public async Task PreviewThenApplyCreatesTheEntitlementsTheUserSaw()
    {
        var (studentId, mealTypeId) = await SeedStudentAndMealAsync("2026-9030", "Ogle Yemegi UI");
        var screen = NewEntitlementsScreen();
        await screen.InitializeAsync();

        screen.OpenGrantCommand.Execute(null);
        screen.TargetType = "Manual";
        screen.ManualStudentIds = studentId.ToString();
        screen.GrantMeal = screen.MealTypes.Single(x => x.Id == mealTypeId);
        screen.GrantStartsOn = new DateTime(2026, 3, 2);   // Pazartesi
        screen.GrantEndsOn = new DateTime(2026, 3, 6);     // Cuma
        screen.Quantity = 1;

        // Kullanici once "Onizle" butonuna basar.
        await Execute(screen.PreviewCommand);
        Assert.NotNull(screen.Preview);

        // Sonra "Uygula" butonu aktiflesir.
        Assert.True(screen.ApplyCommand.CanExecute(null), "Önizlemeden sonra 'Uygula' hâlâ pasif.");
        await Execute(screen.ApplyCommand);

        var rows = await InScope(db => db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId).ToListAsync());

        Assert.Equal(5, rows.Count);                       // Pazartesi-Cuma
        Assert.All(rows, row => Assert.Equal(1, row.Quantity));
        Assert.All(rows, row => Assert.Equal(0, row.ConsumedQuantity));
    }

    [Fact]
    public async Task ChangingTheFormAfterPreviewInvalidatesItSoStaleDataCannotBeApplied()
    {
        // Kullanici onizler, sonra tarihi degistirir: ESKI onizleme uygulanmamalidir.
        var (studentId, mealTypeId) = await SeedStudentAndMealAsync("2026-9031", "Ogle Yemegi UI2");
        var screen = NewEntitlementsScreen();
        await screen.InitializeAsync();

        screen.OpenGrantCommand.Execute(null);
        screen.TargetType = "Manual";
        screen.ManualStudentIds = studentId.ToString();
        screen.GrantMeal = screen.MealTypes.Single(x => x.Id == mealTypeId);
        screen.GrantStartsOn = new DateTime(2026, 4, 6);
        screen.GrantEndsOn = new DateTime(2026, 4, 10);
        screen.Quantity = 1;
        await Execute(screen.PreviewCommand);
        Assert.NotNull(screen.Preview);

        // Kullanici fikrini degistirir.
        screen.GrantEndsOn = new DateTime(2026, 4, 24);

        Assert.Null(screen.Preview);
        Assert.False(screen.ApplyCommand.CanExecute(null),
            "Form değiştikten sonra eski önizlemeyle uygulama yapılabiliyor.");
    }

    [Fact]
    public async Task InvalidManualIdsAreReportedInsteadOfCrashing()
    {
        var screen = NewEntitlementsScreen();
        await screen.InitializeAsync();
        screen.OpenGrantCommand.Execute(null);
        screen.TargetType = "Manual";
        screen.ManualStudentIds = "bu-bir-guid-degil";
        screen.GrantMeal = screen.MealTypes.FirstOrDefault();

        await Execute(screen.PreviewCommand);   // COKMEMELI

        Assert.Null(screen.Preview);
        Assert.NotNull(screen.PreviewMessage);
    }

    // ------------------------------------------------- uctan uca gunluk akis

    [Fact]
    public async Task AFullSchoolDayFromTheScreensStudentCardEntitlementThenTheTurnstile()
    {
        // Gercek bir is gunu: memur ogrenciyi kaydeder, kart tanimlar, hakedis verir;
        // ogrenci turnikeden gecer. Her adim EKRANDAN yapilir.

        // 1) Ogrenci ekraninda yeni kayit
        var students = NewStudentsScreen();
        students.NewStudentCommand.Execute(null);
        students.FormStudentNo = "2026-9100";
        students.FormFirstName = "Elif";
        students.FormLastName = "Yıldırım";
        await Execute(students.SaveStudentCommand);

        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == "2026-9100").Select(x => x.Id).SingleAsync());

        // 2) Ilk kart atanir.
        // NOT: Masaustu ekraninda ILK kart atama yolu YOK (bkz.
        // NewStudentCannotBeGivenTheirFirstCardFromTheDesktopScreens). Akisin geri
        // kalanini test edebilmek icin kart API ucundan atanir.
        await AssignFirstCardAsync(studentId, "UI-KART-9100");

        var cardCount = await InScope(db => db.StudentCards.AsNoTracking()
            .CountAsync(x => x.StudentId == studentId && x.CardNumber == "UI-KART-9100"));
        Assert.Equal(1, cardCount);

        // 3) Hakedis ekranindan bugun icin hak verilir
        var mealTypeId = await CreateMealTypeAsync("Ogle Yemegi Gunluk");
        var today = DateTime.Today;
        var entitlements = NewEntitlementsScreen();
        await entitlements.InitializeAsync();
        entitlements.OpenGrantCommand.Execute(null);
        entitlements.TargetType = "Manual";
        entitlements.ManualStudentIds = studentId.ToString();
        entitlements.GrantMeal = entitlements.MealTypes.Single(x => x.Id == mealTypeId);
        entitlements.GrantStartsOn = today;
        entitlements.GrantEndsOn = today;
        entitlements.Quantity = 1;
        entitlements.IncludeSaturday = true;    // bugun hafta sonuysa da calissin
        entitlements.IncludeSunday = true;
        await Execute(entitlements.PreviewCommand);
        Assert.NotNull(entitlements.Preview);
        await Execute(entitlements.ApplyCommand);

        var granted = await InScope(db => db.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.StudentId == studentId));
        Assert.Equal(1, granted.Quantity);
        Assert.Equal(0, granted.ConsumedQuantity);

        // 4) Ogrenci turnikeden gecer (cihaz X-Device-Key ile konusur)
        var deviceId = await CreateDeviceAsync("Ana Turnike UI");
        var first = await SwipeAsync("UI-KART-9100", deviceId, mealTypeId);
        var second = await SwipeAsync("UI-KART-9100", deviceId, mealTypeId);

        Assert.Equal("ALLOW", first);
        Assert.Equal("DENY", second);     // ayni ogun icin ikinci gecis

        var afterSwipe = await InScope(db => db.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.StudentId == studentId));
        Assert.Equal(1, afterSwipe.ConsumedQuantity);   // TAM BIR hak dusuldu
    }

    [Fact]
    public async Task DeactivatingAStudentFromTheScreenStopsThemAtTheTurnstile()
    {
        // Okuldan ayrilan ogrencinin karti kapida CALISMAMALIDIR.
        var students = NewStudentsScreen();
        students.NewStudentCommand.Execute(null);
        students.FormStudentNo = "2026-9101";
        students.FormFirstName = "Ayrilan";
        students.FormLastName = "Ogrenci";
        await Execute(students.SaveStudentCommand);

        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == "2026-9101").Select(x => x.Id).SingleAsync());

        await AssignFirstCardAsync(studentId, "UI-KART-9101");

        var mealTypeId = await CreateMealTypeAsync("Ogle Yemegi Ayrilan");
        var today = DateTime.Today;
        var entitlements = NewEntitlementsScreen();
        await entitlements.InitializeAsync();
        entitlements.OpenGrantCommand.Execute(null);
        entitlements.TargetType = "Manual";
        entitlements.ManualStudentIds = studentId.ToString();
        entitlements.GrantMeal = entitlements.MealTypes.Single(x => x.Id == mealTypeId);
        entitlements.GrantStartsOn = today;
        entitlements.GrantEndsOn = today;
        entitlements.Quantity = 1;
        entitlements.IncludeSaturday = true;
        entitlements.IncludeSunday = true;
        await Execute(entitlements.PreviewCommand);
        await Execute(entitlements.ApplyCommand);

        // Memur ogrenciyi ekrandan pasife alir.
        await Execute(students.SearchCommand);
        students.SelectedStudent = students.Students.Single(x => x.StudentNo == "2026-9101");
        await Execute(students.OpenStudentDetailCommand);
        Assert.NotNull(students.Details);
        await Execute(students.DeactivateCommand);

        var deviceId = await CreateDeviceAsync("Turnike Ayrilan");
        var decision = await SwipeAsync("UI-KART-9101", deviceId, mealTypeId);

        Assert.Equal("DENY", decision);
    }

    private async Task<Guid> CreateMealTypeAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var meal = new MealType
        {
            Id = Guid.NewGuid(), Name = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Set<MealType>().Add(meal);
        await db.SaveChangesAsync();
        return meal.Id;
    }

    private async Task<Guid> CreateDeviceAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(), Name = name, DeviceType = "Turnstile",
            ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Disconnected",
            IpAddress = $"10.1.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
            IpPort = Random.Shared.Next(2000, 60000),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>Turnikede kart okutur. Cihaz JWT degil X-Device-Key ile kimliklenir.</summary>
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

    /// <summary>
    /// ILK kart atama. Masaustu ekranlari yalnizca "degistir" ucunu kullanir;
    /// karti olmayan ogrenci icin bu uc hata verir.
    /// </summary>
    private async Task AssignFirstCardAsync(Guid studentId, string cardNumber)
    {
        var response = await client.PostAsJsonAsync(
            $"api/students/{studentId:D}/cards", new { CardNumber = cardNumber });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NewStudentCannotBeGivenTheirFirstCardFromTheDesktopScreens()
    {
        // EKSIK OZELLIK: Ekranda yalnizca "Kart Degistir" var. Yeni kaydedilen
        // ogrencinin aktif karti olmadigi icin bu islem basarisiz olur ve
        // ogrenciye masaustunden kart TANIMLANAMAZ.
        var students = NewStudentsScreen();
        students.NewStudentCommand.Execute(null);
        students.FormStudentNo = "2026-9110";
        students.FormFirstName = "Kartsiz";
        students.FormLastName = "Ogrenci";
        await Execute(students.SaveStudentCommand);

        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == "2026-9110").Select(x => x.Id).SingleAsync());

        students.NewCardNumber = "UI-KART-9110";
        await Execute(students.ReplaceCardCommand);

        // Kart olusmaz...
        var created = await InScope(db => db.StudentCards.AsNoTracking()
            .AnyAsync(x => x.StudentId == studentId));
        Assert.False(created, "İlk kart atanabiliyorsa bu eksiklik giderilmiş demektir; testi güncelleyin.");

        // ...ve kullanici NEDENINI ekranda gormelidir (sessizce kaybolmamalidir).
        Assert.True(students.HasError,
            "İlk kart atanamadı ama kullanıcıya hiçbir açıklama gösterilmedi.");
    }

    // ------------------------------------------------- sunucu coktugunde ekranlar

    /// <summary>
    /// Sunucu ulasilamaz oldugunda HICBIR ekran cokmemelidir.
    ///
    /// AsyncCommand yakalanmamis hatayi UnhandledError'a tasir; masaustunde bu
    /// kullaniciya "beklenmeyen hata" penceresi olarak doner. Her ekran kendi
    /// hatasini yakalamali ve anlasilir bir mesaj gostermelidir.
    /// </summary>
    [Fact]
    public async Task NoScreenCrashesWhenTheServerIsUnreachable()
    {
        // Kapali bir porta bakan istemci: her istek baglanti hatasiyla doner.
        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        dead.Timeout = TimeSpan.FromSeconds(2);
        var session = new OperatorSession();

        var failures = new List<string>();
        void Record(object? sender, Exception error) =>
            failures.Add($"{sender?.GetType().Name}: {error.GetType().Name}");

        AsyncCommand.UnhandledError += Record;
        try
        {
            var students = new StudentsViewModel(new StudentApiClient(dead, session),
                new ShellNavigationService(AllRoutes), ["students.read", "students.write", "cards.manage"]);
            await RunQuietly(students.SearchCommand);
            students.NewStudentCommand.Execute(null);
            students.FormStudentNo = "2026-9200";
            students.FormFirstName = "Cevrimdisi";
            students.FormLastName = "Deneme";
            await RunQuietly(students.SaveStudentCommand);

            var cash = new CashViewModel(new CashApiClient(dead, session),
                ["cash.read", "cash.write", "cash.manage"]);
            await RunQuietly(cash.RefreshCommand);
            await RunQuietly(cash.ApplyFiltersCommand);

            var entitlements = new MealEntitlementsViewModel(
                new MealEntitlementApiClient(dead, session), ["entitlements.manage", "entitlements.bulk"]);
            await RunQuietly(entitlements.SearchCommand);
        }
        finally { AsyncCommand.UnhandledError -= Record; }

        Assert.True(failures.Count == 0,
            "Sunucu kapalıyken ekranlar çöktü: " + string.Join(", ", failures));
    }

    [Fact]
    public async Task WhenTheServerIsUnreachableTheUserIsToldNotLeftGuessing()
    {
        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        dead.Timeout = TimeSpan.FromSeconds(2);

        var screen = new StudentsViewModel(new StudentApiClient(dead, new OperatorSession()),
            new ShellNavigationService(AllRoutes), ["students.read", "students.write"]);

        await RunQuietly(screen.SearchCommand);

        Assert.True(screen.HasError || screen.IsOffline,
            "Sunucuya ulaşılamıyor ama ekran boş liste gösteriyor; kullanıcı kayıt yok sanır.");
    }

    /// <summary>Komutu calistirir ama hatayi test hatasi saymaz; cagiran karar verir.</summary>
    private static async Task RunQuietly(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null)) return;
        if (command is AsyncCommand asyncCommand) await asyncCommand.ExecuteAsync(null);
        else command.Execute(null);
    }

    private sealed record DecisionOnly(string Decision, string Reason);

    private sealed class OperatorSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateOperatorToken();
        public bool IsAuthenticated => true;
    }

}
