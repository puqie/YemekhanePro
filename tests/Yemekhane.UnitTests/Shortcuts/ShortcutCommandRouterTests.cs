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
