namespace Yemekhane.Desktop.Services;

public static class ShellRoutes
{
    public const string Dashboard = "dashboard";
    public const string Students = "students";
    public const string StudentsCreate = "students/new";
    public const string Cards = "cards";
    public const string Entitlements = "entitlements";
    public const string HolidayTransfer = "holiday-transfer";
    public const string CardReader = "card-reader";
    public const string Reports = "reports";
    public const string DailyTracking = "daily-tracking";
    public const string StudentDetail = "student-detail";
    public const string Devices = "devices";
    public const string DeviceCards = "device-cards";
    public const string Sms = "sms";
    public const string Cash = "cash";
    public const string Settings = "settings";
    public const string StudentImport = "student-import";
    /// <summary>
    /// AYRILMIS rota: API tarafinda RbacController (users.manage) var ama masaustunde
    /// kullanici/rol ekrani henuz yazilmadi. Bu rota kasitli olarak HICBIR yerde kayitli
    /// rotalara eklenmez; boylece Ayarlar'daki "Kullanıcılar / Roller" dugmesi
    /// (CanNavigateUsers = IsAvailable(...)) gizli kalir ve olmayan bir ekrana gidilemez.
    /// Sabit silinemez: SettingsViewModel buna basvurur. Ekran yazildiginda View'i ekleyip
    /// App.xaml.cs ve LiveUiHarness'teki rota listesine "users.manage" kosuluyla eklenmeli.
    /// </summary>
    public const string UsersRoles = "users-roles";
}

public sealed class NavigationRequestedEventArgs(string route) : EventArgs
{
    public string Route { get; } = route;
}

public interface IShellNavigationService
{
    event EventHandler<NavigationRequestedEventArgs>? NavigationRequested;
    bool IsAvailable(string route);
    void Navigate(string route);
}

public sealed class ShellNavigationService(IEnumerable<string> availableRoutes) : IShellNavigationService
{
    private readonly HashSet<string> routes = new(availableRoutes, StringComparer.Ordinal);
    public event EventHandler<NavigationRequestedEventArgs>? NavigationRequested;
    public bool IsAvailable(string route) => routes.Contains(route) || routes.Any(x => route.StartsWith(x + "/", StringComparison.Ordinal));
    public void Navigate(string route)
    {
        if (!IsAvailable(route)) throw new InvalidOperationException($"'{route}' özelliği henüz kullanıma açık değil.");
        NavigationRequested?.Invoke(this, new NavigationRequestedEventArgs(route));
    }
}
