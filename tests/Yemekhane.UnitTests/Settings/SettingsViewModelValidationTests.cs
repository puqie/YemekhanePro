using System.Net;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Settings;

/// <summary>
/// Ayarlar ekraninin canli denetimde bulunan bosluklari: sayisal alana harf/negatif
/// girilince sessiz kaliyordu, "25:99" saati sessizce 00:00 olarak kaydediliyordu,
/// sunucunun dogrulama mesaji "Çevrimdışı"ya donusuyordu.
/// </summary>
public sealed class SettingsViewModelValidationTests
{
    private static SettingsViewModel NewVm(FakeApi api, IFileDialogService? files = null) =>
        new(api, new ShellNavigationService([ShellRoutes.Settings]), ["settings.read", "settings.manage"], files);

    [Theory]
    [InlineData("abc", "sayı olmalıdır")]
    [InlineData("-5", "1 ile 300")]
    [InlineData("0", "1 ile 300")]
    [InlineData("301", "1 ile 300")]
    public async Task GecersizZamanAsimiKaydiEngellerVeTurkceSoyler(string text, string expected)
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.SmsTimeoutText = text;

        Assert.True(vm.IsDirty, "geçersiz girdi kirli sayılmalı ki Kaydet tıklanabilsin");
        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveAsync();
        Assert.Equal(0, api.SaveCalls);
        Assert.Contains(expected, vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("SMS zaman aşımı", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("25:99")]
    [InlineData("3")]
    [InlineData("saat")]
    public async Task GecersizYedeklemeSaatiSessizceSifirlanmaz(string time)
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.BackupTime = time;
        await vm.SaveAsync();

        Assert.Equal(0, api.SaveCalls);
        Assert.Contains("SS:dd", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("harf")]
    public async Task GecersizSaklamaSayisiReddedilir(string text)
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.BackupRetentionText = text;
        await vm.SaveAsync();

        Assert.Equal(0, api.SaveCalls);
        Assert.Contains("Saklanacak yedek sayısı", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GecerliDegerlerSayiOlarakSunucuyaGider()
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.SmsTimeoutText = " 45 "; vm.BackupRetentionText = "2"; vm.BackupTime = "3:30"; vm.LogRetentionText = "60"; vm.SyncIntervalText = "15";
        await vm.SaveAsync();

        Assert.Equal(1, api.SaveCalls);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(45, api.LastRequest!.Sms.TimeoutSeconds);
        Assert.Equal(2, api.LastRequest.Backup.RetentionCount);
        Assert.Equal(new TimeOnly(3, 30), api.LastRequest.Backup.Time);
        Assert.Equal(60, api.LastRequest.Logs.RetentionDays);
        Assert.Equal(15, api.LastRequest.Sync.IntervalMinutes);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SunucununReddiMesajiylaGosterilirCevrimdisiSayilmaz()
    {
        var api = new FakeApi { SaveFailure = new ApiRequestException("Yedek yolu tam nitelikli mutlak bir yol olmalıdır.", HttpStatusCode.BadRequest) };
        var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.BackupPath = "goreli\\klasor";
        await vm.SaveAsync();

        Assert.Equal("Yedek yolu tam nitelikli mutlak bir yol olmalıdır.", vm.ErrorMessage);
        Assert.False(vm.IsOffline);
        Assert.True(vm.IsDirty, "reddedilen değişiklik kaybolmamalı");
    }

    [Fact]
    public async Task EsitlemeEtkinkenAdresVeCihazZorunludur()
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();

        vm.SyncEnabled = true;
        await vm.SaveAsync();

        Assert.Equal(0, api.SaveCalls);
        Assert.Contains("zorunlu", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("geri yukle", false)]
    [InlineData("GERİ YÜKLE", false)]
    [InlineData("GERI YUKLE ", false)]
    [InlineData("GERI YUKLE", true)]
    public async Task GeriYuklemeOnayiTamEslesmeIster(string typed, bool expected)
    {
        var api = new FakeApi(); var vm = NewVm(api);
        await vm.InitializeAsync();
        var backup = Path.GetTempFileName();
        try
        {
            vm.RestorePath = backup;
            vm.RestoreConfirmation = typed;
            Assert.Equal(expected, vm.IsRestoreConfirmed);
            Assert.Equal(expected, vm.RestoreCommand.CanExecute(null));
            if (!expected) Assert.Contains("eşleşmiyor", vm.RestoreConfirmationHint, StringComparison.Ordinal);
        }
        finally { File.Delete(backup); }
    }

    [Fact]
    public async Task YedekDosyasiDiyalogDikisindenSecilir()
    {
        var api = new FakeApi();
        var backup = Path.GetTempFileName();
        try
        {
            var vm = NewVm(api, new StubDialogs { OpenResult = backup });
            await vm.InitializeAsync();
            vm.ChooseRestoreCommand.Execute(null);
            Assert.Equal(backup, vm.RestorePath);
            Assert.True(vm.ValidateBackupCommand.CanExecute(null));
        }
        finally { File.Delete(backup); }
    }

    [Fact]
    public void KimlikDogrulamaSecenekleriTurkceAdVeApiDegeriTasir()
    {
        var vm = NewVm(new FakeApi());
        Assert.Equal(["None", "Basic", "Bearer", "ApiKey"], vm.SmsAuthTypes.Select(x => x.Value).ToArray());
        Assert.All(vm.SmsAuthTypes, x => Assert.NotEqual(x.Value, x.Name));
    }

    private sealed class StubDialogs : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? OpenFile(string title, string filter) => OpenResult;
        public string? SaveFile(string title, string filter, string suggestedFileName) => null;
    }

    private sealed class FakeApi : ISettingsApiClient
    {
        private SettingsDocument value = Document();
        public int SaveCalls { get; private set; }
        public SaveSettingsRequest? LastRequest { get; private set; }
        public Exception? SaveFailure { get; set; }
        public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default)
        {
            if (SaveFailure is not null) throw SaveFailure;
            SaveCalls++; LastRequest = request;
            value = new SettingsDocument(new(request.School.Name, request.School.Address, request.School.Contact, request.School.LogoPath),
                new(request.Sms.Endpoint, request.Sms.AuthType, request.Sms.Username, request.Sms.Sender, request.Sms.TimeoutSeconds, request.Sms.Secret is not null),
                new(request.Backup.Enabled, request.Backup.Frequency, request.Backup.WeeklyDay, request.Backup.Time, request.Backup.RetentionCount, request.Backup.Path),
                new(request.Sync.Endpoint, request.Sync.DeviceId, request.Sync.IntervalMinutes, request.Sync.Enabled, false, new("Disabled", 0, 0, null, null)),
                new(request.Logs.Level, request.Logs.RetentionDays, request.Logs.Path), new(0, [], 0, []), false);
            return Task.FromResult(new SaveSettingsResult(value, ["Sms"], true));
        }
        public Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackupCommandResult(Guid.NewGuid(), "backup.zip", DateTimeOffset.UtcNow, "1", "1"));
        public Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SyncRunResult(0, 0, 0, 0, 0, 0));
        public Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);
        public Task RequeueConflictAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<ApplicationLogItem>([], page, pageSize, 0));
        private Yemekhane.Application.Sms.SmsAutomationSettings automation = Yemekhane.Application.Sms.SmsAutomationSettings.Default;
        public Task<Yemekhane.Application.Sms.SmsAutomationStatus> GetSmsAutomationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Yemekhane.Application.Sms.SmsAutomationStatus(automation, DateTimeOffset.UtcNow, null));
        public Task<Yemekhane.Application.Sms.SmsAutomationStatus> SaveSmsAutomationAsync(Yemekhane.Application.Sms.SmsAutomationSettings settings, CancellationToken cancellationToken = default) { automation = settings; return GetSmsAutomationAsync(cancellationToken); }
        public Task<Yemekhane.Application.Sms.EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Yemekhane.Application.Sms.EntitlementWarningRunResult(DateOnly.FromDateTime(DateTime.Today), 0, 0, 0, 0));
        private static SettingsDocument Document() => new(new("Okul", null, null, null), new(null, "None", null, null, 30, false), new(false, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null), new(null, null, 5, false, false, new("Disabled", 0, 0, null, null)), new("Information", 30, null), new(0, [], 0, []), false);
    }
}
