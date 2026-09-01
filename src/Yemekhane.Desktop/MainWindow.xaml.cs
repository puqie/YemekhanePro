using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop;

public partial class MainWindow : Window, IShortcutCommandTarget
{
    private ShortcutCommandRouter? shortcuts;
    private IReadOnlySet<string> permissions = new HashSet<string>();
    private string currentRoute = ShellRoutes.Dashboard;

    public MainWindow()
    {
        InitializeComponent();
        // Surum baslikta gorunur: kullanici destek isterken hangi surumu kullandigini okuyabilmeli.
        Title = $"YemekhanePro {AppVersion.Display} • Operasyon Merkezi";
        PreviewKeyDown += HandleShortcutKey;
    }

    public void ConfigureShortcuts(IReadOnlySet<string> grantedPermissions)
    {
        permissions = grantedPermissions;
        shortcuts = new ShortcutCommandRouter(this);
    }

    public object? GlobalSearchDataContext
    {
        get => GlobalSearchHost.DataContext;
        set
        {
            GlobalSearchHost.DataContext = value;
            if (value is GlobalSearchViewModel search)
                CollectionViewSource.GetDefaultView(search.Results).GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(SearchDisplayItem.GroupTitle)));
        }
    }

    public object? NotificationDataContext
    {
        get => NotificationHost.DataContext;
        set { NotificationHost.DataContext = value; NotificationButton.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible; }
    }

    public object? DailyTrackingDataContext
    {
        get => DailyTrackingHost.DataContext;
        set => DailyTrackingHost.DataContext = value;
    }

    public object? StudentsDataContext
    {
        get => StudentsHost.DataContext;
        set => StudentsHost.DataContext = value;
    }

    public object? MealEntitlementsDataContext
    {
        get => MealEntitlementsHost.DataContext;
        set => MealEntitlementsHost.DataContext = value;
    }

    public object? CalendarDataContext
    {
        get => CalendarHost.DataContext;
        set => CalendarHost.DataContext = value;
    }

    public object? DevicesDataContext
    {
        get => DevicesHost.DataContext;
        set => DevicesHost.DataContext = value;
    }

    public object? DeviceCardsDataContext
    {
        get => DeviceCardsHost.DataContext;
        set => DeviceCardsHost.DataContext = value;
    }

    public object? SmsDataContext
    {
        get => SmsHost.DataContext;
        set => SmsHost.DataContext = value;
    }

    public object? CashDataContext
    {
        get => CashHost.DataContext;
        set => CashHost.DataContext = value;
    }

    public object? ReportsDataContext
    {
        get => ReportsHost.DataContext;
        set => ReportsHost.DataContext = value;
    }

    public object? SettingsDataContext
    {
        get => SettingsHost.DataContext;
        set => SettingsHost.DataContext = value;
    }

    public object? StudentImportDataContext
    {
        get => StudentImportHost.DataContext;
        set => StudentImportHost.DataContext = value;
    }

    public void Navigate(string route)
    {
        currentRoute = route;
        UpdateNavigationSelection(route);
        var tracking = route == Services.ShellRoutes.DailyTracking;
        var students = route is Services.ShellRoutes.Cards or Services.ShellRoutes.CardReader
            || route == Services.ShellRoutes.Students || route.StartsWith(Services.ShellRoutes.Students + "/", StringComparison.Ordinal)
            || route.StartsWith(Services.ShellRoutes.StudentDetail + "/", StringComparison.Ordinal);
        var entitlements = route == Services.ShellRoutes.Entitlements || route.StartsWith(Services.ShellRoutes.Entitlements + "/", StringComparison.Ordinal);
        var calendar = route == Services.ShellRoutes.HolidayTransfer || route.StartsWith(Services.ShellRoutes.HolidayTransfer + "/", StringComparison.Ordinal);
        var devices = route == Services.ShellRoutes.Devices;
        var deviceCards = route == Services.ShellRoutes.DeviceCards;
        var sms = route == Services.ShellRoutes.Sms || route.StartsWith(Services.ShellRoutes.Sms + "/", StringComparison.Ordinal);
        var cash = route == Services.ShellRoutes.Cash;
        var reports = route == Services.ShellRoutes.Reports;
        var settings = route == Services.ShellRoutes.Settings;
        var studentImport = route == Services.ShellRoutes.StudentImport;
        DashboardHost.Visibility = tracking || students || entitlements || calendar || devices || deviceCards || sms || cash || reports || settings || studentImport ? Visibility.Collapsed : Visibility.Visible;
        DailyTrackingHost.Visibility = tracking ? Visibility.Visible : Visibility.Collapsed;
        StudentsHost.Visibility = students ? Visibility.Visible : Visibility.Collapsed;
        MealEntitlementsHost.Visibility = entitlements ? Visibility.Visible : Visibility.Collapsed;
        CalendarHost.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        DevicesHost.Visibility = devices ? Visibility.Visible : Visibility.Collapsed;
        DeviceCardsHost.Visibility = deviceCards ? Visibility.Visible : Visibility.Collapsed;
        SmsHost.Visibility = sms ? Visibility.Visible : Visibility.Collapsed;
        CashHost.Visibility = cash ? Visibility.Visible : Visibility.Collapsed;
        ReportsHost.Visibility = reports ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        StudentImportHost.Visibility = studentImport ? Visibility.Visible : Visibility.Collapsed;
        if (students && StudentsDataContext is StudentsViewModel viewModel) viewModel.HandleRoute(route);
        if (entitlements && MealEntitlementsDataContext is MealEntitlementsViewModel entitlementViewModel) entitlementViewModel.HandleRoute(route);
        if (sms && SmsDataContext is SmsViewModel smsViewModel && route.StartsWith(Services.ShellRoutes.Sms + "/", StringComparison.Ordinal)
            && Guid.TryParse(route[(route.LastIndexOf('/') + 1)..], out var studentId)) smsViewModel.SelectStudent(studentId);
        if (calendar && CalendarDataContext is CalendarViewModel calendarViewModel
            && DateOnly.TryParseExact(route[(route.LastIndexOf('/') + 1)..], "yyyy-MM-dd", out var date)) _ = calendarViewModel.NavigateToAsync(date);
    }

    private void UpdateNavigationSelection(string route)
    {
        var selected = BaseRoute(route);
        foreach (var button in NavigationButtons.Children.OfType<Button>())
        {
            var isSelected = string.Equals(button.Tag as string, selected, StringComparison.Ordinal)
                || selected == ShellRoutes.StudentDetail && string.Equals(button.Tag as string, ShellRoutes.Students, StringComparison.Ordinal);
            // Tag rota kimligini tasir (orn. "dashboard"); secim onun uzerine
            // yazilmaz. Background/FontWeight de dogrudan atanmaz -- bir local
            // deger NavItem stilindeki her Setter'i ve tetikleyiciyi (IsMouseOver
            // dahil) kalici olarak gecersiz kilar. Bunun yerine NavItem'in kendi
            // Tag=="secili" -- artik NavigationSelection.IsSelected -- tetikleyicisi
            // tek gercek kaynak olsun diye eklenti ozelligi kullanilir.
            NavigationSelection.SetIsSelected(button, isSelected);
        }
    }

    private void HandleShortcutKey(object sender, KeyEventArgs e)
    {
        if (GlobalSearchDataContext is GlobalSearchViewModel search && search.IsOpen)
        {
            if (e.Key == Key.Down) { search.MoveSelection(1); SearchResults.ScrollIntoView(SearchResults.SelectedItem); e.Handled = true; return; }
            if (e.Key == Key.Up) { search.MoveSelection(-1); SearchResults.ScrollIntoView(SearchResults.SelectedItem); e.Handled = true; return; }
        }
        if (shortcuts is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        var source = e.OriginalSource;
        var isText = source is TextBoxBase or PasswordBox;
        var multiline = source is TextBox { AcceptsReturn: true };
        e.Handled = shortcuts.TryExecute(new ShortcutGesture(key.ToString(), control, shift, alt),
            new ShortcutInputContext(e.IsRepeat, isText, multiline));
    }

    string IShortcutCommandTarget.CurrentRoute => BaseRoute(currentRoute);
    bool IShortcutCommandTarget.IsPaletteOpen => GlobalSearchDataContext is GlobalSearchViewModel { IsOpen: true };

    bool IShortcutCommandTarget.CanExecute(ShortcutCommand command) => command switch
    {
        ShortcutCommand.GlobalSearch or ShortcutCommand.Help => true,
        ShortcutCommand.Students => true,
        ShortcutCommand.CardRead => permissions.Contains("cards.manage"),
        ShortcutCommand.DailyTracking => true,
        ShortcutCommand.Refresh => CurrentRefreshCommand() is { } refresh && refresh.CanExecute(null),
        ShortcutCommand.ExportPdf => BaseRoute(currentRoute) == ShellRoutes.Reports && ReportsDataContext is ReportsViewModel reports && reports.ExportPdfCommand.CanExecute(null),
        ShortcutCommand.ExportExcel => BaseRoute(currentRoute) == ShellRoutes.Reports && ReportsDataContext is ReportsViewModel reports && reports.ExportExcelCommand.CanExecute(null),
        ShortcutCommand.CloseTopmost => HasClosableLayer(),
        ShortcutCommand.Activate => GlobalSearchDataContext is GlobalSearchViewModel { IsOpen: true },
        _ => false
    };

    bool IShortcutCommandTarget.IsEnabledInHelp(ShortcutCommand command) =>
        command == ShortcutCommand.CardRead
            ? ((IShortcutCommandTarget)this).CanExecute(command) && StudentsDataContext is StudentsViewModel { IsCardReaderAvailable: true }
            : ((IShortcutCommandTarget)this).CanExecute(command);

    void IShortcutCommandTarget.Execute(ShortcutCommand command)
    {
        switch (command)
        {
            case ShortcutCommand.GlobalSearch: OpenGlobalSearch(); break;
            case ShortcutCommand.Students: NavigateAndFocusStudents(); break;
            case ShortcutCommand.CardRead: OpenCardRead(); break;
            case ShortcutCommand.DailyTracking: Navigate(ShellRoutes.DailyTracking); break;
            case ShortcutCommand.Refresh: Execute(CurrentRefreshCommand()); break;
            case ShortcutCommand.ExportPdf: Execute((ReportsDataContext as ReportsViewModel)?.ExportPdfCommand); break;
            case ShortcutCommand.ExportExcel: Execute((ReportsDataContext as ReportsViewModel)?.ExportExcelCommand); break;
            case ShortcutCommand.CloseTopmost: CloseTopmost(); break;
            case ShortcutCommand.Activate: _ = ExecutePaletteAsync(); break;
            case ShortcutCommand.Help: ShowShortcutHelp(); break;
        }
    }

    private void OpenGlobalSearch()
    {
        if (GlobalSearchDataContext is not GlobalSearchViewModel search) return;
        search.Open(); GlobalSearchBox.Focus(); GlobalSearchBox.SelectAll();
    }

    private void NavigateAndFocusStudents()
    {
        Navigate(ShellRoutes.Students);
        Dispatcher.BeginInvoke(StudentsHost.FocusSearch);
    }

    private void OpenCardRead()
    {
        Navigate(ShellRoutes.Students);
        if (StudentsDataContext is StudentsViewModel students) _ = students.OpenCardWorkflowAsync();
    }

    private async Task ExecutePaletteAsync()
    {
        if (GlobalSearchDataContext is GlobalSearchViewModel search) await search.ExecuteOrSearchAsync();
    }

    private System.Windows.Input.ICommand? CurrentRefreshCommand() => BaseRoute(currentRoute) switch
    {
        ShellRoutes.Dashboard => (DataContext as DashboardViewModel)?.RefreshCommand,
        ShellRoutes.DailyTracking => (DailyTrackingDataContext as DailyTrackingViewModel)?.RefreshCommand,
        ShellRoutes.Students or ShellRoutes.StudentDetail => (StudentsDataContext as StudentsViewModel)?.SearchCommand,
        ShellRoutes.Entitlements => (MealEntitlementsDataContext as MealEntitlementsViewModel)?.SearchCommand,
        ShellRoutes.HolidayTransfer => (CalendarDataContext as CalendarViewModel)?.RefreshCommand,
        ShellRoutes.Devices => (DevicesDataContext as DevicesViewModel)?.RefreshCommand,
        ShellRoutes.DeviceCards => (DeviceCardsDataContext as DeviceCardsViewModel)?.RefreshCommand,
        ShellRoutes.Cash => (CashDataContext as CashViewModel)?.RefreshCommand,
        ShellRoutes.Reports => (ReportsDataContext as ReportsViewModel)?.ApplyCommand,
        ShellRoutes.Settings => (SettingsDataContext as SettingsViewModel)?.RefreshCommand,
        _ => null
    };

    private bool HasClosableLayer()
    {
        if (ShortcutHelpHost.Visibility == Visibility.Visible || GlobalSearchDataContext is GlobalSearchViewModel { IsOpen: true }) return true;
        return HasContextLayer();
    }

    private bool HasContextLayer()
    {
        return BaseRoute(currentRoute) switch
        {
            // Ogrenciler ekraninda IsQuickDetailOpen/IsDetailOpen artik hicbir sey CIZMIYOR
            // (Gorev 7: form kalici panel oldu, cekmeceler kaldirildi). Onlari kapatilabilir
            // katman saymak Escape'in surmekte olan bir duzenlemeyi (IsFormOpen'i kapatarak)
            // GORUNMEYEN bir nedenle sessizce iptal etmesine yol aciyordu. Gercekten goruntude
            // olan tek katman kart okuma modalidir.
            ShellRoutes.Students or ShellRoutes.StudentDetail => StudentsDataContext is StudentsViewModel students && students.IsCardWorkflowOpen,
            ShellRoutes.Devices => DevicesDataContext is DevicesViewModel devices && (devices.IsLogsOpen || devices.IsEditorOpen),
            ShellRoutes.HolidayTransfer => CalendarDataContext is CalendarViewModel calendar &&
                (calendar.IsDrawerOpen || calendar.BulkWizard is { IsOpen: true } or { IsHistoryOpen: true }),
            ShellRoutes.Entitlements => MealEntitlementsDataContext is MealEntitlementsViewModel entitlements &&
                (entitlements.IsCancelConfirmationOpen || entitlements.IsGrantOpen || entitlements.BulkWizard is { IsOpen: true } or { IsHistoryOpen: true }),
            ShellRoutes.Cash => CashDataContext is CashViewModel cash && (cash.IsVoidOpen || cash.IsAddOpen),
            _ => false
        };
    }

    private void CloseTopmost()
    {
        var paletteOpen = GlobalSearchDataContext is GlobalSearchViewModel { IsOpen: true };
        var layer = ShortcutLayerPriority.Resolve(ShortcutHelpHost.Visibility == Visibility.Visible, paletteOpen, HasContextLayer());
        if (layer == ShortcutLayer.Help) { ShortcutHelpHost.Visibility = Visibility.Collapsed; return; }
        if (layer == ShortcutLayer.Palette && GlobalSearchDataContext is GlobalSearchViewModel search) { search.Close(); return; }
        if (layer == ShortcutLayer.None) return;
        switch (BaseRoute(currentRoute))
        {
            case ShellRoutes.Students:
            case ShellRoutes.StudentDetail:
                if (StudentsDataContext is StudentsViewModel students)
                { if (students.IsCardWorkflowOpen) students.CloseCardWorkflow(); else Execute(students.CloseDrawersCommand); }
                break;
            case ShellRoutes.Devices:
                if (DevicesDataContext is DevicesViewModel devices) Execute(devices.IsLogsOpen ? devices.CloseLogsCommand : devices.CloseEditorCommand);
                break;
            case ShellRoutes.HolidayTransfer:
                if (CalendarDataContext is CalendarViewModel calendar)
                {
                    if (calendar.BulkWizard is { IsHistoryOpen: true } bulkHistory) Execute(bulkHistory.CloseHistoryCommand);
                    else if (calendar.BulkWizard is { IsOpen: true } bulk) Execute(bulk.CloseCommand);
                    else Execute(calendar.CloseDrawerCommand);
                }
                break;
            case ShellRoutes.Entitlements:
                if (MealEntitlementsDataContext is MealEntitlementsViewModel entitlements)
                {
                    if (entitlements.BulkWizard is { IsHistoryOpen: true } bulkHistory) Execute(bulkHistory.CloseHistoryCommand);
                    else if (entitlements.BulkWizard is { IsOpen: true } bulk) Execute(bulk.CloseCommand);
                    else Execute(entitlements.IsCancelConfirmationOpen ? entitlements.CloseCancelCommand : entitlements.CloseGrantCommand);
                }
                break;
            case ShellRoutes.Cash:
                if (CashDataContext is CashViewModel cash) Execute(cash.IsVoidOpen ? cash.CloseVoidCommand : cash.CloseAddCommand);
                break;
        }
    }

    private static void Execute(System.Windows.Input.ICommand? command)
    { if (command?.CanExecute(null) == true) command.Execute(null); }

    private static string BaseRoute(string route) => route.StartsWith(ShellRoutes.StudentDetail + "/", StringComparison.Ordinal) ? ShellRoutes.StudentDetail
        : route.StartsWith(ShellRoutes.Students + "/", StringComparison.Ordinal) ? ShellRoutes.Students
        : route is ShellRoutes.Cards or ShellRoutes.CardReader ? ShellRoutes.Students
        : route.StartsWith(ShellRoutes.Entitlements + "/", StringComparison.Ordinal) ? ShellRoutes.Entitlements
        : route.StartsWith(ShellRoutes.HolidayTransfer + "/", StringComparison.Ordinal) ? ShellRoutes.HolidayTransfer : route;

    private void OpenShortcutHelp(object sender, RoutedEventArgs e) => ShowShortcutHelp();
    private void CloseShortcutHelp(object sender, RoutedEventArgs e) => ShortcutHelpHost.Visibility = Visibility.Collapsed;
    private void ShowShortcutHelp()
    {
        if (shortcuts is null) return;
        ShortcutHelpList.ItemsSource = shortcuts.GetHelpItems();
        ShortcutHelpHost.Visibility = Visibility.Visible;
    }
}
