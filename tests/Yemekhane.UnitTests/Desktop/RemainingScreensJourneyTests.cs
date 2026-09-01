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

// Sahte istemcilerde bazi olaylar arabirim geregi vardir ama tetiklenmez.
#pragma warning disable CS0067

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Onceki turlarda Ogrenci / Kasa / Hakedis ekranlari arayuzden surulmustu.
/// Bu dosya GERIYE KALAN ekranlari ayni derinlikte ele alir: ekranin bagli
/// oldugu ozelligi yaz, butonun komutunu calistir, GERCEK veritabanina bak.
///
/// Yalnizca kacinilmaz bagimliliklar (gercek zamanli soket, ses, tercih dosyasi)
/// sahtelenir; API -> denetleyici -> veritabani zinciri gercektir.
/// </summary>
[Collection("UI")]
public sealed class RemainingScreensJourneyTests : IAsyncLifetime, IDisposable
{
    private readonly YemekhaneApiFactory factory = new();
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateAdminClient();
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

    private sealed class OperatorSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateAdminToken();
        public bool IsAuthenticated => true;
    }

    /// <summary>Gercek zamanli baglanti testte kurulmaz; olaylar elle tetiklenir.</summary>
    private sealed class SilentRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RaiseAccess(AccessDecisionCommittedEvent e) => AccessReceived?.Invoke(this, e);
        public void RaiseDevice(DeviceStatusChangedEvent e) => DeviceStatusChanged?.Invoke(this, e);
    }

    private static readonly string[] Routes =
    [
        ShellRoutes.Students, ShellRoutes.Entitlements, ShellRoutes.Cash,
        ShellRoutes.Sms, ShellRoutes.Reports, ShellRoutes.Devices, ShellRoutes.Settings
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

    // ================================================= CIHAZLAR EKRANI

    private DevicesViewModel NewDevicesScreen(SilentRealtime? realtime = null) => new(
        new DeviceApiClient(client, new OperatorSession()),
        realtime ?? new SilentRealtime(),
        new HashSet<string>(StringComparer.Ordinal) { "devices.manage" });

    [Fact]
    public async Task OperatorAddsATurnstileFromTheScreenAndItIsStored()
    {
        var screen = NewDevicesScreen();
        await Execute(screen.RefreshCommand);

        screen.AddCommand.Execute(null);
        screen.Name = "Yemekhane Giris Turnikesi";
        screen.SelectedType = "SF300";
        screen.IpAddress = "192.168.1.50";
        screen.Port = 4370;

        await Execute(screen.SaveCommand);
        Assert.True(string.IsNullOrWhiteSpace(screen.ErrorMessage),
            $"Cihaz kaydedilemedi: {screen.ErrorMessage}");

        var stored = await InScope(db => db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Name == "Yemekhane Giris Turnikesi"));

        Assert.NotNull(stored);
        Assert.Equal("192.168.1.50", stored!.IpAddress);
        Assert.Equal(4370, stored.IpPort);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task ADeviceWithAnInvalidIpIsRefusedWithAMessage()
    {
        var screen = NewDevicesScreen();
        await Execute(screen.RefreshCommand);

        screen.AddCommand.Execute(null);
        screen.Name = "Bozuk IP Cihazi";
        screen.SelectedType = "SF300";
        screen.IpAddress = "bu-bir-ip-degil";
        screen.Port = 4370;

        await Execute(screen.SaveCommand);   // COKMEMELI

        Assert.False(await InScope(db => db.Devices.AnyAsync(x => x.Name == "Bozuk IP Cihazi")),
            "Geçersiz IP ile cihaz kaydedildi.");
        Assert.False(string.IsNullOrWhiteSpace(screen.ErrorMessage),
            "Geçersiz IP reddedildi ama kullanıcıya sebep gösterilmedi.");
    }

    [Fact]
    public async Task ADeviceStatusChangeFromTheServerAppearsOnTheScreen()
    {
        // Cihaz baglantisi koptugunda ekran bunu ANINDA gostermelidir;
        // aksi halde operator calismayan turnikeyi calisiyor sanir.
        var realtime = new SilentRealtime();
        var screen = NewDevicesScreen(realtime);

        screen.AddCommand.Execute(null);
        screen.Name = "Durum Takip Cihazi";
        screen.SelectedType = "SF300";
        screen.IpAddress = "192.168.1.51";
        screen.Port = 4371;
        await Execute(screen.SaveCommand);
        await Execute(screen.RefreshCommand);

        var deviceId = await InScope(db => db.Devices.AsNoTracking()
            .Where(x => x.Name == "Durum Takip Cihazi").Select(x => x.Id).SingleAsync());

        realtime.RaiseDevice(new DeviceStatusChangedEvent(
            deviceId, "Durum Takip Cihazi", PreviousStatus: "Connected",
            Status: "Disconnected", OccurredAt: DateTimeOffset.UtcNow));

        var row = screen.Devices.SingleOrDefault(x => x.Item.Id == deviceId);
        Assert.NotNull(row);
        Assert.Equal("Disconnected", row!.Status);
        Assert.Equal("Bağlı değil", row.StatusText);
    }

    [Fact]
    public void WithoutManagePermissionDeviceButtonsAreDisabled()
    {
        var screen = new DevicesViewModel(
            new DeviceApiClient(client, new OperatorSession()),
            new SilentRealtime(),
            new HashSet<string>(StringComparer.Ordinal));   // izin yok

        Assert.False(screen.AddCommand.CanExecute(null), "devices.manage olmadan 'Ekle' aktif.");
        Assert.False(screen.SaveCommand.CanExecute(null), "devices.manage olmadan 'Kaydet' aktif.");
    }

    // ================================================= AYARLAR EKRANI

    /// <summary>
    /// Ayarlar OPERATOR sinirinin disindadir (bkz. Task060SecurityTests:
    /// operator /api/settings'e eristiginde 403 almalidir). Bu yuzden ekran
    /// testi ayri, yonetici yetkili bir oturum kullanir.
    /// </summary>
    private sealed class SettingsAdminSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateTokenWith(
            Yemekhane.Api.Authorization.Permissions.SettingsRead,
            Yemekhane.Api.Authorization.Permissions.SettingsManage,
            Yemekhane.Api.Authorization.Permissions.BackupsManage);
        public bool IsAuthenticated => true;
    }

    private SettingsViewModel NewSettingsScreen() => new(
        new SettingsApiClient(client, new SettingsAdminSession()),
        new ShellNavigationService(Routes),
        ["settings.read", "settings.manage"]);

    [Fact]
    public async Task ChangingTheSchoolNameFromSettingsPersistsIt()
    {
        var screen = NewSettingsScreen();
        await screen.LoadAsync();

        screen.SchoolName = "Atatürk Anadolu Lisesi";
        Assert.True(screen.IsDirty, "Değişiklik yapıldı ama IsDirty açılmadı; Kaydet butonu pasif kalır.");

        await Execute(screen.SaveCommand);
        Assert.Null(screen.ErrorMessage);

        // Ekran yeniden acildiginda deger KORUNMALI.
        var reopened = NewSettingsScreen();
        await reopened.LoadAsync();
        Assert.Equal("Atatürk Anadolu Lisesi", reopened.SchoolName);
    }

    [Fact]
    public async Task CancellingSettingsRestoresTheOriginalValues()
    {
        var screen = NewSettingsScreen();
        await screen.LoadAsync();
        var original = screen.SchoolName;

        // Degerin GERCEKTEN degistiginden emin ol; ayni degeri yazmak
        // IsDirty'yi acmaz ve test bos yere gecerdi.
        screen.SchoolName = original + " (Yanlislikla Yazilan)";
        Assert.True(screen.IsDirty, "Değişiklik yapıldı ama IsDirty açılmadı.");

        screen.CancelCommand.Execute(null);

        Assert.Equal(original, screen.SchoolName);
        Assert.False(screen.IsDirty, "Vazgeçildikten sonra IsDirty açık kaldı.");
    }

    [Fact]
    public void ReadOnlyUserCannotSaveSettings()
    {
        var screen = new SettingsViewModel(
            new SettingsApiClient(client, new SettingsAdminSession()),
            new ShellNavigationService(Routes),
            ["settings.read"]);

        Assert.True(screen.CanRead);
        Assert.False(screen.CanManage);
        Assert.False(screen.SaveCommand.CanExecute(null), "settings.manage olmadan 'Kaydet' aktif.");
    }

    // ================================================= SMS EKRANI

    private SmsViewModel NewSmsScreen(params string[] permissions) => new(
        new SmsApiClient(client, new OperatorSession()),
        permissions.Length > 0 ? permissions : ["sms.read", "sms.send", "sms.manage"]);

    [Fact]
    public void SmsWithoutSendPermissionCannotBePreviewedOrQueued()
    {
        // SMS PARA HARCAR: sms.send olmadan gonderim yolu tamamen kapali olmali.
        var screen = NewSmsScreen("sms.read");

        Assert.False(screen.CanSend);
        Assert.False(screen.PreviewCommand.CanExecute(null), "sms.send olmadan 'Önizle' aktif.");
        Assert.False(screen.EnqueueCommand.CanExecute(null), "sms.send olmadan 'Kuyruğa Al' aktif.");
    }

    [Fact]
    public void QueueingIsBlockedUntilThePreviewIsConfirmed()
    {
        // Onizleme onaylanmadan kuyruga alma AKTIF OLMAMALI: SMS geri alinamaz
        // ve her mesaj para harcar.
        var screen = NewSmsScreen();
        screen.CustomMessage = "Yarın okul tatil.";

        Assert.False(screen.HasPreview);
        Assert.False(screen.IsConfirmed);
        Assert.False(screen.EnqueueCommand.CanExecute(null),
            "Önizleme onaylanmadan 'Kuyruğa Al' aktif; körlemesine SMS gönderilebilir.");
    }

    [Fact]
    public void TheCharacterCounterMatchesWhatTheUserTyped()
    {
        // Segment sayisi FATURAYI belirler: yanlis sayarsa okul fazla oder.
        var screen = NewSmsScreen();

        screen.CustomMessage = "Merhaba";
        Assert.Equal(7, screen.CharacterCount);
        Assert.Equal(1, screen.SegmentCount);

        screen.CustomMessage = new string('A', 160);
        Assert.Equal(160, screen.CharacterCount);
        Assert.Equal(1, screen.SegmentCount);

        screen.CustomMessage = new string('A', 161);
        Assert.True(screen.SegmentCount >= 2,
            "161 karakter tek segment sayıldı; fatura eksik hesaplanır.");
    }

    [Fact]
    public void TurkishCharactersShortenTheSmsSegmentAndTheCounterKnowsIt()
    {
        // GSM-7 alfabesinde Turkce harfler yoktur; mesaj UCS-2'ye duser ve
        // segment 160 degil 70 karaktere iner. Sayac bunu bilmezse okul
        // beklediginin iki kati odeme yapar.
        var screen = NewSmsScreen();

        screen.CustomMessage = new string('ş', 71);

        Assert.Equal(71, screen.CharacterCount);
        Assert.True(screen.SegmentCount >= 2,
            $"71 Türkçe karakter {screen.SegmentCount} segment sayıldı; UCS-2 sınırı 70.");
    }

    [Fact]
    public async Task ChangingTheMessageInvalidatesAnExistingPreview()
    {
        // Kullanici onizler, sonra metni degistirir: ESKI onizlemeyle
        // gonderim yapilmamalidir.
        var screen = NewSmsScreen();
        await screen.InitializeAsync();

        screen.CustomMessage = "Ilk metin";
        screen.IsConfirmed = true;
        screen.CustomMessage = "Degistirilmis metin";

        Assert.False(screen.HasPreview,
            "Metin değişti ama eski önizleme hâlâ geçerli sayılıyor.");
        Assert.False(screen.EnqueueCommand.CanExecute(null),
            "Metin değiştikten sonra eski önizlemeyle gönderim yapılabiliyor.");
    }

    // ================================================= RAPORLAR EKRANI

    private ReportsViewModel NewReportsScreen(params string[] permissions) => new(
        new ReportApiClient(client, new OperatorSession()),
        permissions.Length > 0 ? permissions : ["reports.read", "reports.export"]);

    [Fact]
    public void ExportIsBlockedWithoutTheExportPermission()
    {
        var screen = NewReportsScreen("reports.read");
        Assert.False(screen.CanExport, "reports.export yokken CanExport açık.");
    }

    // ================================================= GENEL ARAMA

    [Fact]
    public async Task GlobalSearchFindsAStudentThatWasJustCreated()
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = "2026-7010", FirstName = "Bulunacak", LastName = "Kisi" });
        created.EnsureSuccessStatusCode();

        var screen = new GlobalSearchViewModel(
            new GlobalSearchApiClient(client, new OperatorSession()),
            new ShellNavigationService(Routes));

        screen.Query = "Bulunacak";
        await screen.SearchNowAsync();

        Assert.NotEmpty(screen.Results);
    }

    [Fact]
    public async Task GlobalSearchWithNoMatchesReportsEmptinessInsteadOfLookingBroken()
    {
        var screen = new GlobalSearchViewModel(
            new GlobalSearchApiClient(client, new OperatorSession()),
            new ShellNavigationService(Routes));

        screen.Query = "BOYLE-BIR-KAYIT-KESINLIKLE-YOK";
        await screen.SearchNowAsync();

        Assert.Empty(screen.Results);
    }

    // ================================================= GUNLUK TAKIP

    /// <summary>
    /// Turnikeden GERCEKTEN gecen ogrenci gunluk takip ekraninda gorunmelidir.
    ///
    /// Gercek zamanli olay veriyi TASIMAZ; yalnizca "bir sey oldu, tazele"
    /// sinyalidir (RecoverGapAsync). Bu sayede soket olayi kaybolsa bile bir
    /// sonraki olay boslugu kapatir ve ekran her zaman sunucunun gercegini
    /// gosterir. Test de bu yuzden gercek bir gecis uretir.
    /// </summary>
    [Fact]
    public async Task ATurnstilePassAppearsOnTheDailyTrackingScreen()
    {
        var (mealTypeId, deviceId) = await SeedStudentCardAndEntitlementAsync(
            "2026-7030", "TAKIP-7030");

        var realtime = new SilentRealtime();
        var screen = new DailyTrackingViewModel(
            new DailyTrackingApiClient(client, new OperatorSession()),
            realtime, new MemoryTrackingPreferences(), new SilentSound());

        await Execute(screen.RefreshCommand);
        Assert.Empty(screen.Rows);

        // Ogrenci turnikeden gecer.
        var decision = await SwipeAsync("TAKIP-7030", deviceId, mealTypeId);
        Assert.Equal("ALLOW", decision);

        // Cihaz olayi ekrana "tazele" sinyali gonderir.
        realtime.RaiseAccess(new AccessDecisionCommittedEvent(
            OperationId: Guid.NewGuid(), Decision: "ALLOW", Reason: "Hak kullanildi",
            StudentId: null, StudentName: "",
            DeviceId: deviceId, MealTypeId: mealTypeId,
            OccurredAt: DateTimeOffset.UtcNow));

        await WaitUntil(() => screen.Rows.Count > 0);

        var row = Assert.Single(screen.Rows);
        Assert.Equal("TAKIP-7030", row.CardNumber);
        Assert.Equal("ALLOW", row.Decision);
    }

    /// <summary>
    /// Reddedilen gecis de gorunmelidir: gorevli KIMIN geri cevrildigini
    /// bilmezse ogrenciye yardim edemez.
    /// </summary>
    [Fact]
    public async Task ADeniedPassIsAlsoShownSoStaffCanSeeWhoWasTurnedAway()
    {
        var (mealTypeId, deviceId) = await SeedStudentCardAndEntitlementAsync(
            "2026-7031", "TAKIP-7031");

        var realtime = new SilentRealtime();
        var screen = new DailyTrackingViewModel(
            new DailyTrackingApiClient(client, new OperatorSession()),
            realtime, new MemoryTrackingPreferences(), new SilentSound());
        await Execute(screen.RefreshCommand);

        // Ilk gecis hakki tuketir, ikincisi REDDEDILIR.
        await SwipeAsync("TAKIP-7031", deviceId, mealTypeId);
        var second = await SwipeAsync("TAKIP-7031", deviceId, mealTypeId);
        Assert.Equal("DENY", second);

        realtime.RaiseAccess(new AccessDecisionCommittedEvent(
            OperationId: Guid.NewGuid(), Decision: "DENY", Reason: "Hak yok",
            StudentId: null, StudentName: "",
            DeviceId: deviceId, MealTypeId: mealTypeId,
            OccurredAt: DateTimeOffset.UtcNow));

        await WaitUntil(() => screen.Rows.Any(x =>
            x.Decision.Equals("DENY", StringComparison.OrdinalIgnoreCase)));

        Assert.Contains(screen.Rows, x =>
            x.Decision.Equals("DENY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Duraklatilmis ekran canli olaylari ISLEMEMELIDIR.</summary>
    [Fact]
    public async Task APausedScreenDoesNotPullNewRows()
    {
        var (mealTypeId, deviceId) = await SeedStudentCardAndEntitlementAsync(
            "2026-7032", "TAKIP-7032");

        var realtime = new SilentRealtime();
        var screen = new DailyTrackingViewModel(
            new DailyTrackingApiClient(client, new OperatorSession()),
            realtime, new MemoryTrackingPreferences(), new SilentSound());
        await Execute(screen.RefreshCommand);

        screen.ToggleLiveCommand.Execute(null);
        Assert.True(screen.IsPaused, "Duraklat komutu çalışmadı.");

        await SwipeAsync("TAKIP-7032", deviceId, mealTypeId);
        realtime.RaiseAccess(new AccessDecisionCommittedEvent(
            OperationId: Guid.NewGuid(), Decision: "ALLOW", Reason: "Hak kullanildi",
            StudentId: null, StudentName: "",
            DeviceId: deviceId, MealTypeId: mealTypeId,
            OccurredAt: DateTimeOffset.UtcNow));

        await Task.Delay(400);
        Assert.Empty(screen.Rows);
    }

    /// <summary>Kosul saglanana kadar bekler; canli guncelleme asenkron isler.</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Beklenen durum zaman aşımına uğradı.");
    }

    /// <summary>Ogrenci + kart + bugune hakedis + turnike hazirlar.</summary>
    private async Task<(Guid MealTypeId, Guid DeviceId)> SeedStudentCardAndEntitlementAsync(
        string studentNo, string cardNumber)
    {
        var created = await client.PostAsJsonAsync("api/students",
            new { StudentNo = studentNo, FirstName = "Takip", LastName = "Ogrencisi" });
        created.EnsureSuccessStatusCode();
        var studentId = await InScope(db => db.Students.AsNoTracking()
            .Where(x => x.StudentNo == studentNo).Select(x => x.Id).SingleAsync());

        var card = await client.PostAsJsonAsync(
            $"api/students/{studentId:D}/cards", new { CardNumber = cardNumber });
        card.EnsureSuccessStatusCode();

        Guid mealTypeId, deviceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            var meal = new MealType
            {
                Id = Guid.NewGuid(), Name = $"Ogun {studentNo}",
                IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Set<MealType>().Add(meal);
            var device = new Device
            {
                Id = Guid.NewGuid(), Name = $"Turnike {studentNo}", DeviceType = "Turnstile",
                ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Disconnected",
                IpAddress = $"10.3.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
                IpPort = Random.Shared.Next(2000, 60000),
                IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            mealTypeId = meal.Id;
            deviceId = device.Id;
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

    private sealed class MemoryTrackingPreferences : IDailyTrackingPreferences
    {
        public bool SoundEnabled { get; set; } = true;
    }

    private sealed class SilentSound : ITrackingSoundPlayer
    {
        public ValueTask PlayAsync(string decision, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
