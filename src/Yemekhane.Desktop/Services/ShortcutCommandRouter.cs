namespace Yemekhane.Desktop.Services;

public enum ShortcutCommand
{
    GlobalSearch,
    Students,
    CardRead,
    DailyTracking,
    Refresh,
    ExportPdf,
    ExportExcel,
    CloseTopmost,
    Activate,
    Help
}

public readonly record struct ShortcutGesture(string Key, bool Control = false, bool Shift = false, bool Alt = false);

public readonly record struct ShortcutInputContext(bool IsRepeat, bool IsTextInput, bool IsMultilineInput);

public sealed record ShortcutHelpItem(string Gesture, string Description, bool IsEnabled, string Status);

public enum ShortcutLayer { None, Context, Palette, Help }

public static class ShortcutLayerPriority
{
    public static ShortcutLayer Resolve(bool helpOpen, bool paletteOpen, bool contextLayerOpen) =>
        helpOpen ? ShortcutLayer.Help : paletteOpen ? ShortcutLayer.Palette : contextLayerOpen ? ShortcutLayer.Context : ShortcutLayer.None;
}

public interface IShortcutCommandTarget
{
    string CurrentRoute { get; }
    bool IsPaletteOpen { get; }
    bool CanExecute(ShortcutCommand command);
    bool IsEnabledInHelp(ShortcutCommand command) => CanExecute(command);
    void Execute(ShortcutCommand command);
}

public sealed class ShortcutCommandRouter(IShortcutCommandTarget target)
{
    private static readonly (ShortcutGesture Gesture, ShortcutCommand Command, string Label)[] Bindings =
    [
        (new("K", true), ShortcutCommand.GlobalSearch, "Global arama"),
        (new("F2"), ShortcutCommand.Students, "Öğrenciler ve arama odağı"),
        (new("F3"), ShortcutCommand.CardRead, "Kart okuma ve öğrenci eşleştirme"),
        (new("F4"), ShortcutCommand.DailyTracking, "Günlük Takip"),
        (new("F5"), ShortcutCommand.Refresh, "Geçerli görünümü yenile"),
        (new("P", true), ShortcutCommand.ExportPdf, "Geçerli raporu PDF aktar"),
        (new("E", true), ShortcutCommand.ExportExcel, "Geçerli raporu Excel aktar"),
        (new("Escape"), ShortcutCommand.CloseTopmost, "En üst pencereyi kapat"),
        (new("Enter"), ShortcutCommand.Activate, "Odaklı öğeyi aç / gönder"),
        (new("F1"), ShortcutCommand.Help, "Kısayol yardımını göster")
    ];

    public bool TryExecute(ShortcutGesture gesture, ShortcutInputContext input)
    {
        var command = Map(gesture);
        if (command is null || input.IsRepeat || IsUnsafeWhileTyping(command.Value, input)) return false;
        if (!target.CanExecute(command.Value)) return false;
        target.Execute(command.Value);
        return true;
    }

    public IReadOnlyList<ShortcutHelpItem> GetHelpItems() => Bindings.Select(binding =>
    {
        var enabled = target.IsEnabledInHelp(binding.Command);
        return new ShortcutHelpItem(Format(binding.Gesture), binding.Label, enabled,
            enabled ? ContextStatus(binding.Command) : UnavailableStatus(binding.Command));
    }).ToArray();

    public static ShortcutCommand? Map(ShortcutGesture gesture)
    {
        // WPF'te Key.Enter, Key.Return'un takma adidir ve Key.ToString() "Return" verir.
        // Baglama tablosu "Enter" dedigi icin gercek klavyeden gelen Enter hicbir komuta
        // eslesmiyordu: palet sonucunu Enter ile acmak calismiyordu (canli yolculukta bulundu).
        var keyName = gesture.Key.Trim();
        if (string.Equals(keyName, "Return", StringComparison.OrdinalIgnoreCase)) keyName = "Enter";
        var normalized = new ShortcutGesture(keyName, gesture.Control, gesture.Shift, gesture.Alt);
        foreach (var binding in Bindings)
            if (string.Equals(binding.Gesture.Key, normalized.Key, StringComparison.OrdinalIgnoreCase)
                && binding.Gesture.Control == normalized.Control
                && binding.Gesture.Shift == normalized.Shift
                && binding.Gesture.Alt == normalized.Alt)
                return binding.Command;
        return null;
    }

    private static bool IsUnsafeWhileTyping(ShortcutCommand command, ShortcutInputContext input) =>
        input.IsTextInput && command is ShortcutCommand.ExportPdf or ShortcutCommand.ExportExcel
        || input.IsMultilineInput && command == ShortcutCommand.Activate;

    /// <summary>Rota kimligi ("daily-tracking") kullaniciya gosterilmez; ekranin Turkce adi yazilir.</summary>
    public static string RouteTitle(string route) => route switch
    {
        // Yan menu ve sayfa basligiyla ayni ad: F1 yardiminda "Etkin: Dashboard" yaziyordu.
        ShellRoutes.Dashboard => "Genel Bakış",
        ShellRoutes.DailyTracking => "Günlük Takip",
        ShellRoutes.Students or ShellRoutes.StudentDetail or ShellRoutes.StudentsCreate or ShellRoutes.Cards or ShellRoutes.CardReader => "Öğrenciler",
        ShellRoutes.Entitlements => "Yemek Hakedişleri",
        ShellRoutes.HolidayTransfer => "Takvim / Tatil",
        ShellRoutes.StudentImport => "Sicil Aktar",
        ShellRoutes.Devices => "Cihazlar / Turnikeler",
        ShellRoutes.DeviceCards => "Kart Yükleme Durumu",
        ShellRoutes.Sms => "SMS Merkezi",
        ShellRoutes.Cash => "Kasa",
        ShellRoutes.Reports => "Raporlar",
        ShellRoutes.Settings => "Ayarlar",
        // Eksikti: Tanimlar ekraninda F1 yardimi ham rota kimligini ("definitions") gosteriyordu.
        ShellRoutes.Definitions => "Tanımlar",
        // UsersRoles bilerek yok: o rota hicbir yere kayitli degil, ekrani henuz yazilmadi.
        _ => route
    };

    private string ContextStatus(ShortcutCommand command) => command switch
    {
        ShortcutCommand.Refresh => $"Etkin: {RouteTitle(target.CurrentRoute)}",
        ShortcutCommand.ExportPdf or ShortcutCommand.ExportExcel => "Etkin: Raporlar",
        ShortcutCommand.Activate when !target.IsPaletteOpen => "Odak bağlamına göre",
        _ => "Etkin"
    };

    private static string UnavailableStatus(ShortcutCommand command) => command switch
    {
        ShortcutCommand.ExportPdf or ShortcutCommand.ExportExcel => "Yalnızca Raporlar ekranında, hazır rapor ve dışa aktarma yetkisiyle",
        ShortcutCommand.CardRead => "Kart yetkisi yok ya da bağlı kart okuyucu bulunmuyor",
        ShortcutCommand.Refresh => "Bu görünüm yenilemeyi desteklemiyor",
        ShortcutCommand.CloseTopmost => "Açık katman yok",
        ShortcutCommand.Activate => "Uygulanabilir odak yok",
        // Tanimlar ekraninda F2 tablonun "yeniden adlandir" kisayoluna birakilir.
        ShortcutCommand.Students => "Bu ekranda F2, seçili tanımı yeniden adlandırır",
        _ => "Bu oturumda kullanılamıyor"
    };

    private static string Format(ShortcutGesture gesture) => gesture.Control ? $"Ctrl+{gesture.Key}" : gesture.Key;
}
