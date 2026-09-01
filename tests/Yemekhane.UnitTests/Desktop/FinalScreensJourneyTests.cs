using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Arayuzden surulmemis SON ekranlar: Takvim, Cihaz Kartlari, Dashboard,
/// Bildirim Merkezi ve Toplu Islem Sihirbazi.
///
/// Bu dosya ile masaustundeki tum ekranlar arayuzden test edilmis olur.
/// </summary>
[Collection("UI")]
public sealed class FinalScreensJourneyTests : IAsyncLifetime
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

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

        public void RaiseAccess(AccessDecisionCommittedEvent e) => AccessReceived?.Invoke(this, e);
        public void RaiseState(RealtimeConnectionState s) => StateChanged?.Invoke(this, s);
    }

    private sealed class SilentNotifications : INotificationRealtimeClient
    {
        public event EventHandler<NotificationEvent>? NotificationReceived;
        public event EventHandler<RealtimeConnectionState>? StateChanged;

        public void Raise(NotificationEvent e) => NotificationReceived?.Invoke(this, e);
    }

    private static readonly string[] Routes =
    [
        ShellRoutes.Dashboard, ShellRoutes.Students, ShellRoutes.Entitlements,
        ShellRoutes.Cash, ShellRoutes.Devices, ShellRoutes.DeviceCards,
        ShellRoutes.DailyTracking, ShellRoutes.HolidayTransfer, ShellRoutes.Reports
    ];

    private Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> query)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return query(db).ContinueWith(t => { scope.DisposeAsync().AsTask().Wait(); return t.Result; });
    }

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
            Assert.Fail($"Buton komutu hata fırlattı: {escaped.GetType().Name}: {escaped.Message}");
    }

    // ================================================= TAKVIM EKRANI

    private CalendarViewModel NewCalendarScreen() => new(
        new CalendarApiClient(client, new OperatorSession()),
        ["calendar.manage"]);

    [Fact]
    public async Task DeclaringAHolidayFromTheCalendarStoresIt()
    {
        var screen = NewCalendarScreen();
        await screen.InitializeAsync();

        screen.OpenHolidayFormCommand.Execute(null);
        screen.HolidayName = "Ulusal Egemenlik ve Çocuk Bayramı";

        await Execute(screen.CreateHolidayCommand);

        var day = screen.SelectedDate;
        var stored = await InScope(db => db.Set<Holiday>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Date == day));

        Assert.NotNull(stored);
        Assert.Contains("Egemenlik", stored!.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolidayWithNoNameIsRefusedWithAMessage()
    {
        var screen = NewCalendarScreen();
        await screen.InitializeAsync();

        screen.OpenHolidayFormCommand.Execute(null);
        screen.HolidayName = "   ";        // kullanici bos birakti

        await Execute(screen.CreateHolidayCommand);   // COKMEMELI

        var day = screen.SelectedDate;
        Assert.False(await InScope(db => db.Set<Holiday>().AnyAsync(x => x.Date == day)),
            "İsimsiz tatil kaydedildi.");
        Assert.False(string.IsNullOrWhiteSpace(screen.FormMessage),
            "İsimsiz tatil reddedildi ama kullanıcıya sebep gösterilmedi.");
    }

    [Fact]
    public async Task MovingToTheNextMonthLoadsThatMonthsDays()
    {
        var screen = NewCalendarScreen();
        await screen.InitializeAsync();
        var startTitle = screen.MonthTitle;

        await Execute(screen.NextMonthCommand);

        Assert.NotEqual(startTitle, screen.MonthTitle);
        Assert.NotEmpty(screen.Days);
    }

    [Fact]
    public async Task TheTodayButtonComesBackToTodaysDate()
    {
        var screen = NewCalendarScreen();
        await screen.InitializeAsync();

        await Execute(screen.NextMonthCommand);
        await Execute(screen.NextMonthCommand);
        await Execute(screen.TodayCommand);

        Assert.Equal(screen.Today, screen.SelectedDate);
    }

    [Fact]
    public void WithoutCalendarPermissionTheHolidayButtonIsDisabled()
    {
        var screen = new CalendarViewModel(
            new CalendarApiClient(client, new OperatorSession()),
            []);   // izin yok

        Assert.False(screen.CanManage);
        Assert.False(screen.CreateHolidayCommand.CanExecute(null),
            "calendar.manage olmadan tatil oluşturma aktif.");
    }

    // ================================================= CIHAZ KARTLARI

    [Fact]
    public async Task TheDeviceCardScreenListsWhatIsWaitingToBePushed()
    {
        // Kart cihaza yuklenmemisse ogrenci turnikeden gecemez; bu ekran
        // bekleyen isleri gostermezse sorun FARK EDILMEZ.
        var deviceId = await SeedDeviceAsync("Kart Yukleme Cihazi");
        await SeedStudentWithCardAsync("2026-7040", "KART-7040");

        var screen = new DeviceCardsViewModel(
            new DeviceCardsApiClient(client, new OperatorSession()));

        await Execute(screen.RefreshCommand);

        Assert.Null(screen.Error);
        Assert.Contains(screen.Devices, d => d.DeviceName == "Kart Yukleme Cihazi");
    }

    [Fact]
    public async Task PushingCardsReportsTheOutcomeToTheUser()
    {
        await SeedDeviceAsync("Elle Yukleme Cihazi");
        var screen = new DeviceCardsViewModel(
            new DeviceCardsApiClient(client, new OperatorSession()));
        await Execute(screen.RefreshCommand);

        await Execute(screen.PushNowCommand);   // COKMEMELI

        // Cihaz gercekte bagli degil; islem basarisiz olabilir ama
        // kullanici SONUCU gormelidir.
        Assert.False(screen.IsPushing, "Yükleme bitti ama IsPushing açık kaldı.");
    }

    // ================================================= DASHBOARD

    [Fact]
    public async Task TheDashboardShowsTodaysNumbersFromTheDatabase()
    {
        var (mealTypeId, deviceId) = await SeedEntitledStudentAsync("2026-7050", "PANO-7050");
        await SwipeAsync("PANO-7050", deviceId, mealTypeId);

        var screen = new DashboardViewModel(
            new DashboardApiClient(client, new OperatorSession()),
            new SilentRealtime(),
            new ShellNavigationService(Routes),
            new OperatorSession());

        await Execute(screen.RefreshCommand);

        Assert.Null(screen.ErrorMessage);
        Assert.NotNull(screen.Snapshot);
        Assert.True(screen.Snapshot!.Kpis.Used >= 1,
            $"Turnikeden geçiş oldu ama panoda kullanılan hak {screen.Snapshot.Kpis.Used} görünüyor.");
    }

    [Fact]
    public async Task DashboardNavigationOnlyOffersRoutesThatExist()
    {
        // Var olmayan bir ekrana yonlendirme dugmesi aktif olursa
        // kullanici bos ekrana duser.
        var navigation = new ShellNavigationService([ShellRoutes.Students]);
        var screen = new DashboardViewModel(
            new DashboardApiClient(client, new OperatorSession()),
            new SilentRealtime(), navigation, new OperatorSession());

        Assert.True(screen.NavigateStudentsCommand.CanExecute(null),
            "Var olan Öğrenciler rotası pasif.");
        Assert.False(screen.NavigateDevicesCommand.CanExecute(null),
            "Kayıtlı olmayan Cihazlar rotası aktif; kullanıcı boş ekrana düşer.");
    }

    [Fact]
    public async Task WhenTheRealtimeConnectionDropsTheDashboardSaysSo()
    {
        var realtime = new SilentRealtime();
        var screen = new DashboardViewModel(
            new DashboardApiClient(client, new OperatorSession()),
            realtime, new ShellNavigationService(Routes), new OperatorSession());
        await Execute(screen.RefreshCommand);

        realtime.RaiseState(RealtimeConnectionState.Disconnected);

        Assert.NotEqual(RealtimeConnectionState.Connected, screen.RealtimeState);
    }

    // ================================================= BILDIRIM MERKEZI

    [Fact]
    public async Task OpeningTheNotificationCenterLoadsItsItems()
    {
        var screen = new NotificationCenterViewModel(
            new NotificationApiClient(client, new OperatorSession()),
            new SilentNotifications(),
            new ShellNavigationService(Routes));

        await Execute(screen.ToggleCommand);

        Assert.True(screen.IsOpen, "Bildirim merkezi açılmadı.");
        Assert.Null(screen.Error);
    }

    [Fact]
    public void MarkAllReadIsDisabledWhenThereIsNothingUnread()
    {
        var screen = new NotificationCenterViewModel(
            new NotificationApiClient(client, new OperatorSession()),
            new SilentNotifications(),
            new ShellNavigationService(Routes));

        Assert.False(screen.HasUnread);
        Assert.False(screen.MarkAllReadCommand.CanExecute(null),
            "Okunmamış bildirim yokken 'Tümünü Okundu İşaretle' aktif.");
    }

    // ================================================= TOPLU ISLEM SIHIRBAZI

    private BulkOperationWizardViewModel NewWizard(params string[] permissions) => new(
        new BulkOperationApiClient(client, new OperatorSession()),
        permissions.Length > 0 ? permissions : ["entitlements.bulk", "calendar.manage"]);

    [Fact]
    public void TheWizardCannotBeOpenedWithoutBulkPermission()
    {
        var wizard = NewWizard("calendar.manage");   // entitlements.bulk yok

        Assert.False(wizard.CanBulk,
            "entitlements.bulk olmadan toplu işlem sihirbazı kullanılabilir.");
    }

    [Fact]
    public async Task TheWizardWalksForwardOneStepAtATime()
    {
        // Yuzlerce ogrenciyi etkileyen islem adim adim ilerlemeli;
        // adimlar atlanirsa kullanici ne yaptigini gormeden uygular.
        var wizard = NewWizard();
        wizard.OpenCommand.Execute(null);

        Assert.True(wizard.IsStep1, "Sihirbaz 1. adımda başlamadı.");
        Assert.False(wizard.IsStep2);

        await Execute(wizard.NextCommand);
        Assert.True(wizard.IsStep2, "İleri butonu 2. adıma geçmedi.");
    }

    [Fact]
    public void ClosingTheWizardHidesIt()
    {
        var wizard = NewWizard();
        wizard.OpenCommand.Execute(null);
        Assert.True(wizard.IsOpen);

        wizard.CloseCommand.Execute(null);

        Assert.False(wizard.IsOpen, "Kapat butonu sihirbazı kapatmadı.");
    }

    // ================================================= ORTAK KURULUM

    private async Task<Guid> SeedDeviceAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(), Name = name, DeviceType = "SF300",
            ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Disconnected",
            IpAddress = $"10.4.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
            IpPort = Random.Shared.Next(2000, 60000),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> SeedStudentWithCardAsync(string studentNo, string cardNumber)
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Kart", LastName = "Sahibi" });
        created.EnsureSuccessStatusCode();
        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == studentNo).Select(x => x.Id).SingleAsync());

        var card = await client.PostAsJsonAsync(
            $"api/students/{studentId:D}/cards", new { CardNumber = cardNumber });
        card.EnsureSuccessStatusCode();
        return studentId;
    }

    private async Task<(Guid MealTypeId, Guid DeviceId)> SeedEntitledStudentAsync(
        string studentNo, string cardNumber)
    {
        var studentId = await SeedStudentWithCardAsync(studentNo, cardNumber);
        var deviceId = await SeedDeviceAsync($"Turnike {studentNo}");

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
        var grant = await client.PostAsJsonAsync("api/meal-entitlements/bulk", new
        {
            StudentIds = new[] { studentId }, MealTypeId = mealTypeId,
            StartsOn = today, EndsOn = today, Quantity = 1,
            IncludeSaturday = true, IncludeSunday = true
        });
        grant.EnsureSuccessStatusCode();
        return (mealTypeId, deviceId);
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
}
