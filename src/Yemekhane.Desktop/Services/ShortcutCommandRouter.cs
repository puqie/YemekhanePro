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
        var normalized = new ShortcutGesture(gesture.Key.Trim(), gesture.Control, gesture.Shift, gesture.Alt);
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

    private string ContextStatus(ShortcutCommand command) => command switch
    {
        ShortcutCommand.Refresh => $"Etkin: {target.CurrentRoute}",
        ShortcutCommand.ExportPdf or ShortcutCommand.ExportExcel => "Etkin: Raporlar",
        ShortcutCommand.Activate when !target.IsPaletteOpen => "Odak bağlamına göre",
        _ => "Etkin"
    };

    private static string UnavailableStatus(ShortcutCommand command) => command switch
    {
        ShortcutCommand.ExportPdf or ShortcutCommand.ExportExcel => "Yalnızca Raporlar ve reports.export izniyle",
        ShortcutCommand.CardRead => "Öğrenci/kart yetkisi veya bağlı okuyucu yok",
        ShortcutCommand.Refresh => "Bu görünüm yenilemeyi desteklemiyor",
        ShortcutCommand.CloseTopmost => "Açık katman yok",
        ShortcutCommand.Activate => "Uygulanabilir odak yok",
        _ => "Bu oturumda kullanılamıyor"
    };

    private static string Format(ShortcutGesture gesture) => gesture.Control ? $"Ctrl+{gesture.Key}" : gesture.Key;
}
