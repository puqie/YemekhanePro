using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

// Sahte istemcilerde bazi olaylar arabirim geregi vardir ama tetiklenmez.
#pragma warning disable CS0067

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// HER giris alanina gercekci KOTU VERI girilmesi.
///
/// Bir alan yanlis veriyi kabul ederse iki sonuc olur: ya veritabanina
/// bozuk kayit gider, ya da uygulama coker. Ikisi de kullanicinin guvenini
/// yok eder. Buradaki her test bir alani zorlar ve UC seyi birden dogrular:
///   1) islem reddedilir,
///   2) kullaniciya SEBEBI gosterilir,
///   3) veritabanina hicbir sey yazilmaz.
/// </summary>
[Collection("UI")]
public sealed class InputValidationJourneyTests : IAsyncLifetime, IDisposable
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

    public void Dispose() => factory.Dispose();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    private sealed class OperatorSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateOperatorToken();
        public bool IsAuthenticated => true;
    }

    private sealed class SilentRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly string[] Routes =
        [ShellRoutes.Students, ShellRoutes.Entitlements, ShellRoutes.Cash, ShellRoutes.Sms];

    private Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> query)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return query(db).ContinueWith(t => { scope.DisposeAsync().AsTask().Wait(); return t.Result; });
    }

    private static async Task Run(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null)) return;   // buton pasifse zaten korunmus
        if (command is AsyncCommand asyncCommand) await asyncCommand.ExecuteAsync(null);
        else command.Execute(null);
    }

    // ================================================= OGRENCI EKRANI

    private StudentsViewModel NewStudentsScreen() => new(
        new StudentApiClient(client, new OperatorSession()),
        new ShellNavigationService(Routes),
        ["students.read", "students.write", "students.deactivate", "cards.manage"]);

    [Theory]
    [InlineData("", "Ad", "Soyad", "boş öğrenci numarası")]
    [InlineData("   ", "Ad", "Soyad", "boşluktan ibaret numara")]
    [InlineData("2026-6001", "", "Soyad", "boş ad")]
    [InlineData("2026-6002", "Ad", "", "boş soyad")]
    [InlineData("2026-6003", "   ", "   ", "boşluktan ibaret ad ve soyad")]
    public async Task BadStudentInputIsRefusedAndNothingIsWritten(
        string studentNo, string firstName, string lastName, string what)
    {
        var before = await InScope(db => db.Students.CountAsync());
        var screen = NewStudentsScreen();

        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = studentNo;
        screen.FormFirstName = firstName;
        screen.FormLastName = lastName;

        await Run(screen.SaveStudentCommand);

        Assert.True(screen.HasError, $"{what} kabul edildi, hata gösterilmedi.");
        var after = await InScope(db => db.Students.CountAsync());
        Assert.Equal(before, after);
        Assert.True(screen.IsFormOpen, $"{what}: hata sonrası form kapandı, girilen veri kayboldu.");
    }

    [Fact]
    public async Task AnOverlyLongStudentNumberIsRefusedNotSilentlyTruncated()
    {
        // Sessiz kirpma en kotusudur: kullanici kaydettim sanir, veri eksiktir.
        var screen = NewStudentsScreen();
        screen.NewStudentCommand.Execute(null);
        screen.FormStudentNo = new string('9', 200);   // sutun siniri 32
        screen.FormFirstName = "Uzun";
        screen.FormLastName = "Numara";

        await Run(screen.SaveStudentCommand);

        var stored = await InScope(db => db.Students.AsNoTracking()
            .FirstOrDefaultAsync(x => x.FirstName == "Uzun" && x.LastName == "Numara"));

        if (stored is not null)
            Assert.Equal(200, stored.StudentNo.Length);   // kabul edildiyse KIRPILMAMALI
        else
            Assert.True(screen.HasError, "Uzun numara reddedildi ama sebep gösterilmedi.");
    }

    [Fact]
    public async Task ACardNumberThatIsOnlyWhitespaceIsRefused()
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-6010", FirstName = "Kart", LastName = "Testi" });
        created.EnsureSuccessStatusCode();
        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == "2026-6010").Select(x => x.Id).SingleAsync());
        var assign = await client.PostAsJsonAsync(
            $"api/students/{studentId:D}/cards", new { CardNumber = "KART-6010" });
        assign.EnsureSuccessStatusCode();

        var screen = NewStudentsScreen();
        await Run(screen.SearchCommand);
        screen.SelectedStudent = screen.Students.Single(x => x.StudentNo == "2026-6010");
        await Run(screen.OpenStudentDetailCommand);
        Assert.NotNull(screen.Details);

        screen.NewCardNumber = "   ";
        await Run(screen.ReplaceCardCommand);

        Assert.True(screen.HasError, "Boşluktan ibaret kart numarası kabul edildi.");
        var cards = await InScope(db => db.StudentCards.AsNoTracking()
            .CountAsync(x => x.StudentId == studentId));
        Assert.Equal(1, cards);   // yeni kart olusmamali
    }

    // ================================================= KASA EKRANI

    private CashViewModel NewCashScreen() => new(
        new CashApiClient(client, new OperatorSession()),
        ["cash.read", "cash.write", "cash.manage", "students.read"]);

    /// <summary>
    /// Tutar dogrulamasi TEK BASINA sinanir.
    ///
    /// ValidateAdd ilk hatada durur (once ogrenci, sonra tur, sonra saat).
    /// Onceki adimlar tamamlanmadan cagrilirsa TUTARA hic bakilmaz ve test
    /// bos yere yesil doner -- bu yuzden once gecerli bir baglam kurulur.
    /// </summary>
    private async Task<CashViewModel> CashScreenReadyForAmountAsync(string studentNo)
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Tutar", LastName = "Testi" });
        created.EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            if (!await db.Set<Yemekhane.Domain.Entities.IncomeType>().AnyAsync())
            {
                db.Set<Yemekhane.Domain.Entities.IncomeType>().Add(new()
                {
                    Id = Guid.NewGuid(), Name = "Yemek Ucreti",
                    IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        var screen = NewCashScreen();
        await screen.RefreshAsync();
        screen.OpenAddCommand.Execute(null);
        screen.StudentNumber = studentNo;
        await Run(screen.LookupStudentCommand);
        Assert.NotNull(screen.LookupStudent);

        screen.SelectedAddType = screen.IncomeTypes.First(x => x.IsActive);
        screen.TransactionTime = "12:30";
        screen.AddConfirmed = true;
        return screen;
    }

    [Theory]
    [InlineData("", "boş tutar")]
    [InlineData("   ", "boşluktan ibaret tutar")]
    [InlineData("abc", "harf içeren tutar")]
    [InlineData("-1", "negatif tutar")]
    [InlineData("0", "sıfır tutar")]
    [InlineData("0,00", "sıfır kuruş")]
    [InlineData("12,345", "üç ondalıklı tutar")]
    public async Task BadAmountsAreRejectedAndNoMoneyIsRecorded(string amount, string what)
    {
        var before = await InScope(db => db.Set<Yemekhane.Domain.Entities.IncomeTransaction>().CountAsync());
        var screen = await CashScreenReadyForAmountAsync($"2026-62{Math.Abs(what.GetHashCode()) % 90 + 10}");

        screen.AmountText = amount;

        // Dogrulama tutari REDDETMELIDIR.
        var failure = screen.ValidateAdd();
        Assert.True(failure is not null, $"{what} geçerli sayıldı.");
        Assert.Contains("Tutar", failure!, StringComparison.Ordinal);

        // Kaydetmeye zorlandiginda da para YAZILMAMALIDIR.
        await Run(screen.AddCommand);
        var after = await InScope(db => db.Set<Yemekhane.Domain.Entities.IncomeTransaction>().CountAsync());
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("125,45")]
    [InlineData("0,01")]
    [InlineData("9999999,99")]
    public async Task ValidAmountsPassValidationSoTheRuleIsNotTooStrict(string amount)
    {
        // Dogrulamanin FAZLA siki olmadiginin kaniti; aksi halde kasiyer
        // gecerli tahsilati giremez.
        var screen = await CashScreenReadyForAmountAsync($"2026-63{Math.Abs(amount.GetHashCode()) % 90 + 10}");
        screen.AmountText = amount;

        Assert.Null(screen.ValidateAdd());
    }

    [Theory]
    [InlineData("", "boş saat")]
    [InlineData("25:00", "geçersiz saat")]
    [InlineData("12:99", "geçersiz dakika")]
    [InlineData("abc", "harf içeren saat")]
    [InlineData("12.30", "nokta ayraçlı saat")]
    public async Task BadTransactionTimesAreRejected(string time, string what)
    {
        var screen = await CashScreenReadyForAmountAsync($"2026-64{Math.Abs(what.GetHashCode()) % 90 + 10}");
        screen.AmountText = "50,00";
        screen.TransactionTime = time;

        var failure = screen.ValidateAdd();
        Assert.True(failure is not null, $"{what} geçerli sayıldı.");
        Assert.Contains("Saat", failure!, StringComparison.Ordinal);
    }

    // ================================================= CIHAZ EKRANI

    private DevicesViewModel NewDevicesScreen() => new(
        new DeviceApiClient(client, new OperatorSession()),
        new SilentRealtime(),
        new HashSet<string>(StringComparer.Ordinal) { "devices.manage" });

    [Theory]
    [InlineData("", "192.168.1.10", 4370, "boş cihaz adı")]
    [InlineData("   ", "192.168.1.11", 4370, "boşluktan ibaret ad")]
    [InlineData("Cihaz", "bu-ip-degil", 4370, "geçersiz IP")]
    [InlineData("Cihaz", "999.999.999.999", 4370, "aralık dışı IP")]
    [InlineData("Cihaz", "192.168.1.12", 0, "sıfır port")]
    [InlineData("Cihaz", "192.168.1.13", -1, "negatif port")]
    [InlineData("Cihaz", "192.168.1.14", 70000, "aralık dışı port")]
    public async Task BadDeviceInputIsRefusedAndNoDeviceIsCreated(
        string name, string ip, int port, string what)
    {
        var before = await InScope(db => db.Devices.CountAsync());
        var screen = NewDevicesScreen();

        screen.AddCommand.Execute(null);
        screen.Name = name;
        screen.SelectedType = "SF300";
        screen.IpAddress = ip;
        screen.Port = port;

        await Run(screen.SaveCommand);

        var after = await InScope(db => db.Devices.CountAsync());
        Assert.True(before == after, $"{what} ile cihaz oluşturuldu.");
        Assert.False(string.IsNullOrWhiteSpace(screen.ErrorMessage),
            $"{what} reddedildi ama kullanıcıya sebep gösterilmedi.");
    }

    // ================================================= HAKEDIS EKRANI

    private MealEntitlementsViewModel NewEntitlementsScreen() => new(
        new MealEntitlementApiClient(client, new OperatorSession()),
        ["entitlements.manage", "entitlements.bulk"]);

    [Theory]
    [InlineData(0, "sıfır adet")]
    [InlineData(-5, "negatif adet")]
    public async Task BadEntitlementQuantityIsRefused(int quantity, string what)
    {
        var before = await InScope(db => db.MealEntitlements.CountAsync());
        var screen = NewEntitlementsScreen();
        await screen.InitializeAsync();

        screen.OpenGrantCommand.Execute(null);
        screen.TargetType = "Manual";
        screen.ManualStudentIds = Guid.NewGuid().ToString();
        screen.GrantMeal = screen.MealTypes.FirstOrDefault();
        screen.Quantity = quantity;

        await Run(screen.PreviewCommand);
        await Run(screen.ApplyCommand);

        var after = await InScope(db => db.MealEntitlements.CountAsync());
        // Vaka adi mesaja girer: hangi girdinin gectigi test ciktisindan okunabilsin.
        Assert.True(before == after, $"{what}: hakediş yazılmamalıydı ({before} -> {after}).");
    }

    [Fact]
    public async Task AnEndDateBeforeTheStartDateIsRefused()
    {
        var before = await InScope(db => db.MealEntitlements.CountAsync());
        var screen = NewEntitlementsScreen();
        await screen.InitializeAsync();

        screen.OpenGrantCommand.Execute(null);
        screen.TargetType = "Manual";
        screen.ManualStudentIds = Guid.NewGuid().ToString();
        screen.GrantMeal = screen.MealTypes.FirstOrDefault();
        screen.GrantStartsOn = new DateTime(2026, 5, 20);
        screen.GrantEndsOn = new DateTime(2026, 5, 1);      // baslangictan ONCE
        screen.Quantity = 1;

        await Run(screen.PreviewCommand);
        await Run(screen.ApplyCommand);

        Assert.Null(screen.Preview);
        var after = await InScope(db => db.MealEntitlements.CountAsync());
        Assert.Equal(before, after);
    }

    // ================================================= SMS EKRANI

    private SmsViewModel NewSmsScreen() => new(
        new SmsApiClient(client, new OperatorSession()),
        ["sms.read", "sms.send", "sms.manage"]);

    [Fact]
    public async Task AnEmptyMessageCannotBeQueued()
    {
        // Bos SMS gondermek parayi bosa harcar.
        var screen = NewSmsScreen();
        await screen.InitializeAsync();

        screen.CustomMessage = "";
        screen.IsConfirmed = true;

        Assert.False(screen.EnqueueCommand.CanExecute(null),
            "Boş mesaj kuyruğa alınabiliyor.");
    }

    [Fact]
    public async Task AVeryLongMessageIsCountedHonestlySoBillingIsNotSurprising()
    {
        var screen = NewSmsScreen();
        await screen.InitializeAsync();

        screen.CustomMessage = new string('A', 1000);

        Assert.Equal(1000, screen.CharacterCount);
        Assert.True(screen.SegmentCount >= 7,
            $"1000 karakter {screen.SegmentCount} segment sayıldı; fatura eksik hesaplanır.");
    }
}
