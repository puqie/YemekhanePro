using System.Runtime.ExceptionServices;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Settings;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task LoadEditCancelAndSaveTrackDirtyState()
    {
        var api = new FakeApi(); var navigation = new ShellNavigationService([ShellRoutes.Settings, ShellRoutes.Devices]);
        var vm = new SettingsViewModel(api, navigation, ["settings.read", "settings.manage"]);
        await vm.InitializeAsync(); Assert.False(vm.IsDirty);
        vm.SchoolName = "Değişti"; Assert.True(vm.IsDirty); Assert.True(vm.SaveCommand.CanExecute(null));
        vm.Cancel(); Assert.Equal("Okul", vm.SchoolName); Assert.False(vm.IsDirty);
        vm.SchoolName = "Yeni"; await vm.SaveAsync();
        Assert.Equal(1, api.SaveCalls); Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task NavigationAndOperationStatesUseRealCommands()
    {
        var api = new FakeApi(); var navigation = new ShellNavigationService([ShellRoutes.Settings, ShellRoutes.Devices]); string? route = null;
        navigation.NavigationRequested += (_, x) => route = x.Route;
        var vm = new SettingsViewModel(api, navigation, ["settings.manage"]); await vm.InitializeAsync();
        vm.NavigateDevicesCommand.Execute(null); Assert.Equal(ShellRoutes.Devices, route);
        Assert.True(vm.BackupNowCommand.CanExecute(null)); Assert.True(vm.SyncNowCommand.CanExecute(null));
    }

    [Fact]
    public void SettingsXamlLoadsOnStaThread()
    {
        Exception? failure = null; var thread = new Thread(() => { try { _ = new SettingsView(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeApi : ISettingsApiClient
    {
        public int SaveCalls { get; private set; }
        private SettingsDocument value = Document();
        public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default)
        { SaveCalls++; value = value with { School = new(request.School.Name, request.School.Address, request.School.Contact, request.School.LogoPath) }; return Task.FromResult(new SaveSettingsResult(value, ["School"], false)); }
        public Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackupCommandResult(Guid.NewGuid(), "backup.zip", DateTimeOffset.UtcNow, "1", "1"));
        public Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SyncRunResult(0, 0, 0, 0, 0, 0));
        public Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<ApplicationLogItem>([], page, pageSize, 0));
        private static SettingsDocument Document() => new(new("Okul", null, null, null), new(null, "None", null, null, 30, false), new(false, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, null), new("https://sync.example/", "device", 5, true, false, new("Ready", 0, 0, null, null)), new("Information", 30, null), new(0, [], 0, []), false);
    }
}
