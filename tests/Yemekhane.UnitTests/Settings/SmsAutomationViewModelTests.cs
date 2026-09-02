using System.Net;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Settings;

/// <summary>Ayarlar → SMS → "Otomatik SMS" karti: yukleme, kirli izleme, yerel dogrulama, kayit ve elle tetikleme.</summary>
[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class SmsAutomationViewModelTests
{
    [Fact]
    public async Task AutomationFieldsLoadFromServerAndParticipateInDirtyTracking()
    {
        var api = new FakeApi(); var vm = NewVm(api); await vm.InitializeAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal("13:10", vm.AutoEntitlementSendAt); Assert.Equal("2", vm.AutoEntitlementDaysText);
        Assert.Equal(SmsAutomationTemplates.EntitlementWarningDefault, vm.AutoEntitlementTemplate);
        Assert.StartsWith("Sunucu saati: ", vm.ServerTimeText);
        Assert.Contains("henüz çalışmadı", vm.LastEntitlementRunText);
        Assert.True(vm.RunEntitlementWarningCommand.CanExecute(null));

        vm.AutoIncomeEnabled = true;
        Assert.True(vm.IsDirty); Assert.True(vm.IsAutomationDirty);
        Assert.False(vm.RunEntitlementWarningCommand.CanExecute(null), "kaydedilmemis degisiklikle 'Şimdi gönder' pasif olmali");
        vm.Cancel();
        Assert.False(vm.AutoIncomeEnabled); Assert.False(vm.IsDirty);
        Assert.Equal(0, api.AutomationSaveCalls);
    }

    [Fact]
    public async Task LocalValidationBlocksSaveAndNamesTheField()
    {
        var api = new FakeApi(); var vm = NewVm(api); await vm.InitializeAsync();
        vm.AutoEntitlementSendAt = "25:99";
        Assert.True(vm.IsDirty);
        await vm.SaveAsync();
        Assert.Contains("SS:dd", vm.ErrorMessage); Assert.Equal(0, api.AutomationSaveCalls); Assert.Equal(0, api.SaveCalls);
        vm.AutoEntitlementSendAt = "13:10"; vm.AutoEntitlementDaysText = "0";
        await vm.SaveAsync();
        Assert.Contains("1 ile 30", vm.ErrorMessage);
        vm.AutoEntitlementDaysText = "3"; vm.AutoIncomeEnabled = true; vm.AutoIncomePhone = "";
        await vm.SaveAsync();
        Assert.Contains("GSM", vm.ErrorMessage);
        vm.AutoIncomePhone = "05321234567"; vm.AutoCardTemplate = "";
        await vm.SaveAsync();
        Assert.Contains("Kart yenileme", vm.ErrorMessage);
        vm.AutoCardTemplate = "Kart {kart_no}";
        Assert.Null(vm.ErrorMessage);   // alan duzeltilince uyari kalkar
        await vm.SaveAsync();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(1, api.AutomationSaveCalls);
        Assert.False(vm.IsDirty);
        var saved = api.LastAutomation!;
        Assert.True(saved.IncomeNotice.Enabled); Assert.Equal("05321234567", saved.IncomeNotice.AdminPhone);
        Assert.Equal(3, saved.EntitlementWarning.DaysThreshold); Assert.Equal(new TimeOnly(13, 10), saved.EntitlementWarning.SendAt);
        Assert.Equal("Kart {kart_no}", saved.CardReplacement.Template);
    }

    [Fact]
    public async Task ServerRejectionReachesUserWithoutOfflineBadge()
    {
        var api = new FakeApi { AutomationFailure = new ApiRequestException("Gelir bildirimi yetkili GSM no geçerli bir Türkiye mobil numarası olmalıdır (5 ile başlamalı).", HttpStatusCode.BadRequest) };
        var vm = NewVm(api); await vm.InitializeAsync();
        vm.AutoIncomeEnabled = true; vm.AutoIncomePhone = "02125554433";
        await vm.SaveAsync();
        Assert.Equal(api.AutomationFailure.Message, vm.ErrorMessage);
        Assert.False(vm.IsOffline);
        Assert.True(vm.IsDirty, "sunucu reddettiyse degisiklik kaydedilmis sayilmamali");
    }

    /// <summary>
    /// Sunucu reddettikten sonra kullanici alani KAYITLI degerin aynisi yaparsa form "temiz"
    /// gorunup Kaydet pasif kaliyordu: reddedilen istek bir daha gonderilemiyor, hata mesaji
    /// ekranda asili kaliyordu. Canli yolculugun ikinci kosusunda yakalandi.
    /// </summary>
    [Fact]
    public async Task FailedSaveKeepsFormDirtySoRetryWithOriginalValueStillReachesServer()
    {
        var api = new FakeApi();
        var vm = NewVm(api); await vm.InitializeAsync();
        // Once gecerli bir kayit yap: sunucudaki deger artik "05321234567".
        vm.AutoIncomeEnabled = true; vm.AutoIncomePhone = "05321234567";
        await vm.SaveAsync();
        Assert.Equal(1, api.AutomationSaveCalls); Assert.False(vm.IsDirty);

        // Sunucu bir sonraki kaydi reddetsin.
        api.AutomationFailure = new ApiRequestException("Gelir bildirimi yetkili GSM no geçerli bir Türkiye mobil numarası olmalıdır (5 ile başlamalı).", HttpStatusCode.BadRequest);
        vm.AutoIncomePhone = "02125554433";
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(1, api.AutomationSaveCalls);

        // Kullanici KAYITLI degerin aynisini geri yazar: form yine kirli olmali, Kaydet aktif kalmali.
        api.AutomationFailure = null;
        vm.AutoIncomePhone = "05321234567";
        Assert.True(vm.IsDirty, "reddedilen kayittan sonra form temiz gorunmemeli");
        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveAsync();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, api.AutomationSaveCalls);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task RunNowShowsQueuedCountAndReadOnlyUserCannotRun()
    {
        var api = new FakeApi { RunResult = new EntitlementWarningRunResult(new DateOnly(2026, 9, 2), 5, 3, 1, 1) };
        var vm = NewVm(api); await vm.InitializeAsync();
        vm.RunEntitlementWarningCommand.Execute(null);
        for (var i = 0; i < 50 && vm.EntitlementRunText is null; i++) await Task.Delay(10);
        Assert.Equal("02.09.2026: 3 SMS kuyruğa alındı (5 aday; 1 veli telefonu yok; 1 bugün zaten gönderilmiş).", vm.EntitlementRunText);
        Assert.Contains("kuyruğa alındı", vm.StatusMessage);

        var reader = new SettingsViewModel(new FakeApi(), new ShellNavigationService([ShellRoutes.Settings]), ["settings.read"]);
        await reader.InitializeAsync();
        Assert.False(reader.RunEntitlementWarningCommand.CanExecute(null));
    }

    private static SettingsViewModel NewVm(FakeApi api) =>
        new(api, new ShellNavigationService([ShellRoutes.Settings, ShellRoutes.Devices]), ["settings.read", "settings.manage"]);

    private sealed class FakeApi : ISettingsApiClient
    {
        private SettingsDocument value = new(new("Okul", null, null, null), new(null, "None", null, null, 30, false), new(false, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null), new(null, null, 5, false, false, new("Disabled", 0, 0, null, null)), new("Information", 30, null), new(0, [], 0, []), false);
        private SmsAutomationSettings automation = SmsAutomationSettings.Default;
        public int SaveCalls { get; private set; }
        public int AutomationSaveCalls { get; private set; }
        public SmsAutomationSettings? LastAutomation { get; private set; }
        public Exception? AutomationFailure { get; set; }
        public EntitlementWarningRunResult RunResult { get; set; } = new(new DateOnly(2026, 9, 2), 0, 0, 0, 0);
        public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default)
        { SaveCalls++; return Task.FromResult(new SaveSettingsResult(value, [], false)); }
        public Task<SmsAutomationStatus> GetSmsAutomationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsAutomationStatus(automation, new DateTimeOffset(2026, 9, 2, 14, 7, 0, TimeSpan.FromHours(3)), null));
        public Task<SmsAutomationStatus> SaveSmsAutomationAsync(SmsAutomationSettings settings, CancellationToken cancellationToken = default)
        {
            if (AutomationFailure is not null) throw AutomationFailure;
            AutomationSaveCalls++; LastAutomation = settings; automation = settings; return GetSmsAutomationAsync(cancellationToken);
        }
        public Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default) => Task.FromResult(RunResult);
        public Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);
        public Task RequeueConflictAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<ApplicationLogItem>([], page, pageSize, 0));
    }
}
