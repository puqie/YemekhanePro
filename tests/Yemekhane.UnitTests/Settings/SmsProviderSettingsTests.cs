using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Settings;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Settings;

/// <summary>
/// Ayarlar → SMS: saglayici secimi (Http | Mutlucell) sunucuda dogrulanir ve kalici olur;
/// masaustunde alan etiketleri saglayiciya gore degisir, istek saglayiciyi tasir, test SMS
/// ve kontor dugmeleri yalnizca KAYITLI formda acilir ve sonuc metni ham yaniti gosterir.
/// </summary>
[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class SmsProviderSettingsTests
{
    // ---------------------------------------------------------------- sunucu dogrulama / kalicilik

    [Fact]
    public void ValidationRequiresKnownProviderAndMutlucellUsername()
    {
        Assert.Throws<ArgumentException>(() => SettingsValidation.Validate(Request(new(null, "None", null, null, 30, null, "Netgsm"))));
        Assert.Throws<ArgumentException>(() => SettingsValidation.Validate(Request(new(null, "None", "  ", null, 30, null, "Mutlucell"))));
        SettingsValidation.Validate(Request(new(null, "None", "okul", "OKUL", 30, "pwd", "Mutlucell")));
        SettingsValidation.Validate(Request(new("https://sms.example/", "Bearer", null, null, 30, null)));
    }

    [Fact]
    public async Task ProviderIsPersistedAndReadBackWithHttpAsLegacyDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options); await db.Database.EnsureCreatedAsync();
        var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new TestAuditContext());
        var service = new SettingsService(db, new PassThroughProtector(), audit, TimeProvider.System);

        Assert.Equal("Http", (await service.GetAsync()).Sms.Provider);

        var saved = await service.SaveAsync(Request(new(null, "None", "okul", "OKUL", 30, "pwd", "Mutlucell")));

        Assert.Equal("Mutlucell", saved.Settings.Sms.Provider);
        Assert.Equal("okul", saved.Settings.Sms.Username);
        Assert.True(saved.Settings.Sms.SecretConfigured);
        Assert.Contains("Sms", saved.ChangedCategories);
        Assert.Equal("Mutlucell", (await service.GetAsync()).Sms.Provider);
        Assert.Equal("pwd", await service.GetSecretAsync(SettingsService.SmsSecretKey));
    }

    // ---------------------------------------------------------------- masaustu

    [Fact]
    public async Task SwitchingProviderChangesLabelsVisibilityAndRequest()
    {
        var api = new FakeApi();
        var vm = await Load(api);

        Assert.Equal("Http", vm.SmsProvider);
        Assert.True(vm.IsGenericHttp);
        Assert.Equal("Kullanıcı adı", vm.SmsUsernameLabel);
        Assert.Equal(["Mutlucell", "Http"], vm.SmsProviders.Select(x => x.Value));

        vm.SmsProvider = "Mutlucell";

        Assert.True(vm.IsMutlucell);
        Assert.False(vm.IsGenericHttp);
        Assert.Equal("Kullanıcı adı (ka)", vm.SmsUsernameLabel);
        Assert.StartsWith("API şifresi (pwd)", vm.SmsSecretLabel, StringComparison.Ordinal);
        Assert.StartsWith("Başlık / originatör (org)", vm.SmsSenderLabel, StringComparison.Ordinal);
        Assert.True(vm.IsDirty);
        Assert.Contains("Mutlucell için kullanıcı adı (ka) zorunludur.", vm.Validate());

        vm.SmsUsername = "okul"; vm.SetSmsSecret("pwd");
        await ((AsyncCommand)vm.SaveCommand).ExecuteAsync(null);

        var request = Assert.Single(api.Saved);
        Assert.Equal("Mutlucell", request.Sms.Provider);
        Assert.Equal("okul", request.Sms.Username);
        Assert.Equal("pwd", request.Sms.Secret);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task TestSmsNeedsSavedFormAndPhoneThenShowsProviderReply()
    {
        var api = new FakeApi { Test = new SmsTestResult(true, "Mutlucell", "905321112233", "88512", null, null, null, "$88512", DateTimeOffset.UtcNow) };
        var vm = await Load(api);

        Assert.False(vm.SendTestSmsCommand.CanExecute(null));
        vm.TestSmsPhone = "0532 111 22 33";
        Assert.True(vm.SendTestSmsCommand.CanExecute(null));
        vm.SmsSender = "DEGISTI";
        Assert.False(vm.SendTestSmsCommand.CanExecute(null), "kirli formda test kapali; once Kaydet");
        vm.CancelCommand.Execute(null);
        Assert.True(vm.SendTestSmsCommand.CanExecute(null));

        await ((AsyncCommand)vm.SendTestSmsCommand).ExecuteAsync(null);

        Assert.Equal("0532 111 22 33", api.TestedPhone);
        Assert.Equal("Gönderildi ✓ 905321112233 · Sağlayıcı: Mutlucell · Paket/mesaj no: 88512 · Ham yanıt: $88512", vm.TestSmsResultText);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task FailedTestSmsShowsMessageCodeAndRawReply()
    {
        var api = new FakeApi { Test = new SmsTestResult(false, "Mutlucell", "905321112233", null, "Authentication", "mutlucell_23",
            "Mutlucell: kullanıcı adı veya şifre hatalı (23).", "23", DateTimeOffset.UtcNow) };
        var vm = await Load(api);
        vm.TestSmsPhone = "05321112233";

        await ((AsyncCommand)vm.SendTestSmsCommand).ExecuteAsync(null);

        Assert.Equal("Gönderilemedi ✗ 905321112233 · Mutlucell: kullanıcı adı veya şifre hatalı (23). · Kod: mutlucell_23 · Ham yanıt: 23", vm.TestSmsResultText);
    }

    [Fact]
    public async Task ServerRejectionOfTestSmsGoesToErrorBanner()
    {
        var api = new FakeApi { TestError = new ApiRequestException("Telefon geçerli bir Türkiye mobil numarası olmalıdır.", System.Net.HttpStatusCode.BadRequest) };
        var vm = await Load(api);
        vm.TestSmsPhone = "0212";

        await ((AsyncCommand)vm.SendTestSmsCommand).ExecuteAsync(null);

        Assert.Equal("Telefon geçerli bir Türkiye mobil numarası olmalıdır.", vm.ErrorMessage);
        Assert.Null(vm.TestSmsResultText);
    }

    [Fact]
    public async Task CreditQueryOnlyOnMutlucellAndShowsResult()
    {
        var api = new FakeApi { Credit = new SmsCreditResult(true, 15234, "Kalan kontör: 15.234", "$15234") };
        var vm = await Load(api);
        Assert.False(vm.QuerySmsCreditCommand.CanExecute(null));

        api.Document = api.Document with { Sms = api.Document.Sms with { Provider = "Mutlucell", Username = "okul", SecretConfigured = true } };
        await vm.LoadAsync();

        Assert.True(vm.QuerySmsCreditCommand.CanExecute(null));
        await ((AsyncCommand)vm.QuerySmsCreditCommand).ExecuteAsync(null);
        Assert.Equal("Kalan kontör: 15.234", vm.SmsCreditText);

        api.Credit = new SmsCreditResult(false, null, "Mutlucell: kullanıcı adı veya şifre hatalı (23).", "23");
        await ((AsyncCommand)vm.QuerySmsCreditCommand).ExecuteAsync(null);
        Assert.Equal("Sorgulanamadı: Mutlucell: kullanıcı adı veya şifre hatalı (23). · Ham yanıt: 23", vm.SmsCreditText);
    }

    [Fact]
    public void FormatCoversMissingPiecesWithoutBlankSegments()
    {
        var text = SettingsViewModel.FormatTestResult(new SmsTestResult(true, "Http", "905321112233", null, null, null, null, null, DateTimeOffset.UtcNow));
        Assert.Equal("Gönderildi ✓ 905321112233 · Sağlayıcı: Http · Ham yanıt: (boş)", text);

        var failed = SettingsViewModel.FormatTestResult(new SmsTestResult(false, "Http", "905321112233", null, "Transport", "transport_error", null, null, DateTimeOffset.UtcNow));
        Assert.Equal("Gönderilemedi ✗ 905321112233 · transport_error · Kod: transport_error · Ham yanıt: (boş)", failed);
    }

    // ---------------------------------------------------------------- yardimcilar

    private static async Task<SettingsViewModel> Load(FakeApi api)
    {
        var vm = new SettingsViewModel(api, new ShellNavigationService([ShellRoutes.Settings]), ["settings.read", "settings.manage"]);
        await vm.InitializeAsync();
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    private static SaveSettingsRequest Request(SaveSmsProviderSettings sms) => new(new("Test Okulu", null, null, null), sms,
        new(true, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null),
        new(null, null, 5, false, null), new("Information", 30, null));

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class TestAuditContext : IAuditContext { public Guid? UserId => Guid.Empty; public string? CorrelationId => "test"; }

    private sealed class FakeApi : ISettingsApiClient
    {
        public SettingsDocument Document { get; set; } = new(new("Okul", null, null, null), new(null, "None", null, null, 30, false),
            new(false, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null),
            new(null, null, 5, false, false, new SyncStatus("Idle", 0, 0, null, null)), new("Information", 30, null), new(0, [], 0, []), false);
        public List<SaveSettingsRequest> Saved { get; } = [];
        public SmsTestResult? Test { get; set; }
        public Exception? TestError { get; set; }
        public SmsCreditResult? Credit { get; set; }
        public string? TestedPhone { get; private set; }

        public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Document);
        public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default)
        {
            Saved.Add(request);
            Document = Document with
            {
                Sms = new(request.Sms.Endpoint, request.Sms.AuthType, request.Sms.Username, request.Sms.Sender, request.Sms.TimeoutSeconds,
                    request.Sms.Secret is not null || Document.Sms.SecretConfigured, request.Sms.Provider)
            };
            return Task.FromResult(new SaveSettingsResult(Document, ["Sms"], true));
        }
        public Task<SmsTestResult> SendTestSmsAsync(string phone, CancellationToken cancellationToken = default)
        {
            TestedPhone = phone;
            if (TestError is not null) throw TestError;
            return Task.FromResult(Test ?? throw new InvalidOperationException("Test sonucu ayarlanmadı."));
        }
        public Task<SmsCreditResult> QuerySmsCreditAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Credit ?? throw new InvalidOperationException("Kontör sonucu ayarlanmadı."));
        public Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);
        public Task RequeueConflictAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<ApplicationLogItem>([], page, pageSize, 0));
        public Task<SmsAutomationStatus> GetSmsAutomationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SmsAutomationStatus(SmsAutomationSettings.Default, DateTimeOffset.UtcNow, null));
        public Task<SmsAutomationStatus> SaveSmsAutomationAsync(SmsAutomationSettings settings, CancellationToken cancellationToken = default) => GetSmsAutomationAsync(cancellationToken);
        public Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Yemekhane.Application.Maintenance.YearEndResetPreview> GetYearEndResetPreviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Yemekhane.Application.Maintenance.YearEndResetResult> YearEndResetAsync(string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
