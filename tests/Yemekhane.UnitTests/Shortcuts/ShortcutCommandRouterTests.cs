using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Shortcuts;

public sealed class ShortcutCommandRouterTests
{
    [Theory]
    [InlineData("K", true, ShortcutCommand.GlobalSearch)]
    [InlineData("F2", false, ShortcutCommand.Students)]
    [InlineData("F3", false, ShortcutCommand.CardRead)]
    [InlineData("F4", false, ShortcutCommand.DailyTracking)]
    [InlineData("F5", false, ShortcutCommand.Refresh)]
    [InlineData("P", true, ShortcutCommand.ExportPdf)]
    [InlineData("E", true, ShortcutCommand.ExportExcel)]
    [InlineData("Escape", false, ShortcutCommand.CloseTopmost)]
    [InlineData("Enter", false, ShortcutCommand.Activate)]
    // WPF Key.Enter == Key.Return; Key.ToString() "Return" verir. Gercek klavye bu adla gelir.
    [InlineData("Return", false, ShortcutCommand.Activate)]
    public void MapsKeys(string key, bool control, ShortcutCommand expected) =>
        Assert.Equal(expected, ShortcutCommandRouter.Map(new(key, control)));

    [Fact]
    public void ContextAndRbacDisableReportExportOutsidePermittedReportsView()
    {
        var target = new FakeTarget { Route = ShellRoutes.Students };
        var router = new ShortcutCommandRouter(target);
        Assert.False(router.TryExecute(new("P", true), default));
        target.Route = ShellRoutes.Reports;
        target.Allowed.Add(ShortcutCommand.ExportPdf);
        Assert.True(router.TryExecute(new("P", true), default));
        Assert.Equal([ShortcutCommand.ExportPdf], target.Executed);
    }

    [Fact]
    public void TextInputsKeepExportAndMultilineEnterNative()
    {
        var target = AllowAll(); var router = new ShortcutCommandRouter(target);
        Assert.False(router.TryExecute(new("P", true), new(false, true, false)));
        Assert.False(router.TryExecute(new("Enter"), new(false, true, true)));
        Assert.Empty(target.Executed);
    }

    [Fact]
    public void RepeatedKeyDoesNotExecuteDuplicateCommand()
    {
        var target = AllowAll(); var router = new ShortcutCommandRouter(target);
        Assert.True(router.TryExecute(new("F5"), default));
        Assert.False(router.TryExecute(new("F5"), new(true, false, false)));
        Assert.Single(target.Executed);
    }

    [Fact]
    public void ExtraModifiersDoNotTriggerAConflictingShortcut()
    {
        var target = AllowAll(); var router = new ShortcutCommandRouter(target);
        Assert.False(router.TryExecute(new("P", true, Shift: true), default));
        Assert.Empty(target.Executed);
    }

    [Fact]
    public void ReportExportDispatchesOnlyRequestedFormat()
    {
        var target = AllowAll(); var router = new ShortcutCommandRouter(target);
        router.TryExecute(new("E", true), default);
        Assert.Equal([ShortcutCommand.ExportExcel], target.Executed);
    }

    [Fact]
    public void EscapePriorityIsHelpThenPaletteThenContext()
    {
        Assert.Equal(ShortcutLayer.Help, ShortcutLayerPriority.Resolve(true, true, true));
        Assert.Equal(ShortcutLayer.Palette, ShortcutLayerPriority.Resolve(false, true, true));
        Assert.Equal(ShortcutLayer.Context, ShortcutLayerPriority.Resolve(false, false, true));
        Assert.Equal(ShortcutLayer.None, ShortcutLayerPriority.Resolve(false, false, false));
    }

    /// <summary>
    /// F2 ekran sahibine birakilabilmeli. Pencerenin PreviewKeyDown'i DataGrid.InputBindings'ten
    /// once tuneller; router true dondugunde tus yutuluyordu. Tanimlar ekraninda F2 "yeniden
    /// adlandir" oldugu icin kabuk F2'yi CanExecute ile geri ceker ve tus tabloya ulasir.
    /// </summary>
    [Fact]
    public void EkranSahipliyseF2TabloyaBirakilir()
    {
        var target = AllowAll();
        var router = new ShortcutCommandRouter(target);
        Assert.True(router.TryExecute(new("F2"), default));

        target.Allowed.Remove(ShortcutCommand.Students);
        Assert.False(router.TryExecute(new("F2"), default));
        Assert.Equal([ShortcutCommand.Students], target.Executed);

        var help = router.GetHelpItems().Single(x => x.Gesture == "F2");
        Assert.False(help.IsEnabled);
        Assert.Contains("yeniden adlandırır", help.Status);
    }

    /// <summary>
    /// F1 yardimindaki "Etkin: X" satiri RouteTitle ile yazilir. Gezilebilir HER rotanin
    /// Turkce adi olmali: eksik olan rota ham kimligiyle ("definitions") ekrana dusuyordu.
    /// UsersRoles bilerek disarida: o rota hicbir yere kayitli degil (ekrani yazilmadi).
    /// </summary>
    [Theory]
    [InlineData(ShellRoutes.Dashboard)]
    [InlineData(ShellRoutes.DailyTracking)]
    [InlineData(ShellRoutes.Students)]
    [InlineData(ShellRoutes.StudentDetail)]
    [InlineData(ShellRoutes.StudentsCreate)]
    [InlineData(ShellRoutes.Cards)]
    [InlineData(ShellRoutes.CardReader)]
    [InlineData(ShellRoutes.Entitlements)]
    [InlineData(ShellRoutes.HolidayTransfer)]
    [InlineData(ShellRoutes.StudentImport)]
    [InlineData(ShellRoutes.Definitions)]
    [InlineData(ShellRoutes.Devices)]
    [InlineData(ShellRoutes.DeviceCards)]
    [InlineData(ShellRoutes.Sms)]
    [InlineData(ShellRoutes.Cash)]
    [InlineData(ShellRoutes.Reports)]
    [InlineData(ShellRoutes.Settings)]
    public void HerRotaninTurkceAdiVar(string route)
    {
        var title = ShortcutCommandRouter.RouteTitle(route);
        Assert.NotEqual(route, title);
        Assert.DoesNotContain("-", title);
    }

    private static FakeTarget AllowAll()
    {
        var target = new FakeTarget();
        foreach (var value in Enum.GetValues<ShortcutCommand>()) target.Allowed.Add(value);
        return target;
    }

    private sealed class FakeTarget : IShortcutCommandTarget
    {
        public string Route { get; set; } = ShellRoutes.Reports;
        public string CurrentRoute => Route;
        public bool IsPaletteOpen { get; set; }
        public HashSet<ShortcutCommand> Allowed { get; } = [];
        public List<ShortcutCommand> Executed { get; } = [];
        public bool CanExecute(ShortcutCommand command) => Allowed.Contains(command);
        public void Execute(ShortcutCommand command) => Executed.Add(command);
    }
}
