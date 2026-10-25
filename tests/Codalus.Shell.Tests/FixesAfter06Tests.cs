using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Dock.Avalonia.Controls;
using Dock.Model;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>M50 (0.7): the maintainer's list after the 0.6.0 release.</summary>
public class FixesAfter06Tests
{
    /// <summary>
    /// Navigate Backward and Forward did nothing after ordinary navigation: only Go To Definition and the like made a
    /// step. Leaving a document and a caret move of more than ten lines now do, as in the classic IDE, and the mouse's
    /// back and forward buttons run them anywhere in the window.
    /// </summary>
    [AvaloniaFact]
    public async Task Back_and_forward_follow_documents_and_long_caret_moves_and_the_mouse_buttons()
    {
        await using var f = ShellFixture.Create();
        var first = Path.Combine(f.UserData, "First.txt");
        var second = Path.Combine(f.UserData, "Second.txt");
        await File.WriteAllTextAsync(first, string.Join('\n', Enumerable.Range(1, 60).Select(i => $"first {i}")));
        await File.WriteAllTextAsync(second, string.Join('\n', Enumerable.Range(1, 60).Select(i => $"second {i}")));

        async Task<CodeEditorController> OpenAsync(string path)
        {
            var document = await f.Shell.OpenFileAsync(path, preview: false);
            Assert.NotNull(document);
            await ShellFixture.PumpUntil(() => document.Controller is not null && f.Shell.ActiveEditor == document);
            return document.Controller!;
        }

        (string Name, int Line) Where()
        {
            var editor = f.Shell.ActiveEditor!;
            return (editor.Model.Name, editor.Controller!.Caret.Line);
        }

        var a = await OpenAsync(first);
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(3).Offset;
        ShellFixture.Pump();
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(40).Offset; // more than ten lines: a step
        ShellFixture.Pump();
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(42).Offset; // two lines: not one
        ShellFixture.Pump();
        await OpenAsync(second);

        // The toolbar's arrow, then the mouse's back button over the editor.
        var back = f.Window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Navigate Backward" && b.IsEffectivelyVisible);
        back.Command!.Execute(null);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        var editorView = f.Shell.ActiveEditor!.Controller!.Editor;
        var over = editorView.TranslatePoint(new Point(40, 10), f.Window)!.Value;
        f.Window.MouseDown(over, MouseButton.XButton1);
        f.Window.MouseUp(over, MouseButton.XButton1);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 3));

        // Forward retraces them, the mouse's forward button too.
        f.Window.MouseDown(over, MouseButton.XButton2);
        f.Window.MouseUp(over, MouseButton.XButton2);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        await f.RunAsync("workbench.navigateForward");
        await ShellFixture.PumpUntil(() => Where().Name == "Second.txt");

        // A new step after going back drops what was ahead.
        await f.RunAsync("workbench.navigateBack");
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        await OpenAsync(second);
        await f.RunAsync("workbench.navigateForward");
        ShellFixture.Pump();
        Assert.Equal("Second.txt", Where().Name);
    }

    /// <summary>
    /// The pin and X in a tool window's header were much larger than the document tab's (the maintainer's screenshot 4):
    /// now the same 16 px buttons with the same 12 px glyphs, in every theme, the menu's arrow scaled with them.
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_window_header_buttons_are_the_size_of_the_document_tabs()
    {
        await using var f = ShellFixture.Create();
        var file = Path.Combine(f.UserData, "a.txt");
        await File.WriteAllTextAsync(file, "x");
        await f.Shell.OpenFileAsync(file, preview: false);
        foreach (var theme in ThemeService.Themes)
        {
            f.Window.RequestedThemeVariant = theme.Variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            var chrome = f.Window.GetVisualDescendants().OfType<ToolChromeControl>().First(c => c.IsEffectivelyVisible);
            var tab = f.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(c => c.IsEffectivelyVisible);
            Control Part(Visual root, string name) => root.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
            var tabClose = (Control)Part(tab, "PART_ClosePath").GetVisualParent()!.GetVisualParent()!;
            var tabGlyph = Part(tab, "PART_ClosePath").Bounds.Size;
            foreach (var name in new[] { "PART_PinButton", "PART_CloseButton", "PART_MenuButton" })
            {
                var button = Part(chrome, name);
                Assert.True(Math.Abs(button.Bounds.Width - tabClose.Bounds.Width) < 0.5, $"{theme.Id}: {name} is {button.Bounds.Width} wide, the tab's X {tabClose.Bounds.Width}");
            }

            foreach (var name in new[] { "PART_PinPath", "PART_ClosePath" })
            {
                var glyph = ((Control)Part(chrome, name).GetVisualParent()!.GetVisualParent()!).Bounds.Size; // the Viewbox
                Assert.True(glyph.Width <= tabGlyph.Width + 0.5 && glyph.Height <= tabGlyph.Height + 0.5, $"{theme.Id}: {name}'s glyph is {glyph}, the tab's X {tabGlyph}");
            }

            var arrow = ((Control)Part(chrome, "PART_MenuPath").GetVisualParent()!.GetVisualParent()!).Bounds.Size;
            Assert.True(arrow.Width <= tabGlyph.Width && arrow.Height <= tabGlyph.Height, $"{theme.Id}: the menu's arrow is {arrow}");
        }

        f.Window.RequestedThemeVariant = null;
    }

    /// <summary>
    /// A tool window dragged to the other side landed with no width (the maintainer's screenshots 1–3): it lands at the
    /// width it had, never under 200 px, whatever proportion Dock gave its new pane.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Main", 0.5)] // the window's left edge: Dock's global proportion
    [InlineData("Main", 0.0)] // the proportion the maintainer's saved layout left it
    [InlineData("Center", 0.0)] // beside the documents
    public async Task A_tool_window_dragged_across_keeps_its_width(string target, double proportion)
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        await ShellFixture.PumpUntil(() => layout.OpenToolWindows.Any(t => t.Id == "welcome.view")); // the Start page takes its column first
        ShellFixture.Pump();
        ShellFixture.Pump();
        var tool = layout.OpenToolWindows.First(t => t.Id == "solutionExplorer");
        ToolChromeControl? Frame() => f.Window.GetVisualDescendants().OfType<ToolChromeControl>().SingleOrDefault(c => c.IsEffectivelyVisible && c.DataContext == tool.Owner);
        ToolChromeControl Chrome() => Frame() ?? throw new InvalidOperationException("no chrome; owner " + tool.Owner?.GetType().Name + " p=" + tool.Owner?.Proportion + " chromes: " + string.Join(", ", f.Window.GetVisualDescendants().OfType<ToolChromeControl>().Select(c => $"{(c.DataContext as IDock)?.ActiveDockable?.Id}:{c.IsEffectivelyVisible}:{c.Bounds.Width:0}")));
        var before = Chrome().Bounds.Width;
        Assert.True(before >= 150, $"Solution Explorer starts {before} wide");

        static IDockable? Find(IDockable d, string id) => d.Id == id ? d : (d as IDock)?.VisibleDockables?.Select(c => Find(c, id)).FirstOrDefault(x => x is not null);
        var dock = (IDock)Find(layout.Root, target)!;
        Assert.True(new DockManager(new DockService()).ValidateDockable(tool, dock, DragAction.Move, DockOperation.Left, true));
        tool.Owner!.Proportion = proportion;
        tool.Owner.CollapsedProportion = proportion;
        var wanted = Math.Max(before, Codalus.Shell.Docking.DockDropSizer.MinimumSize);
        await ShellFixture.PumpUntil(() => Frame() is { } c && Math.Abs(c.Bounds.Width - wanted) < 12, seconds: 10, describe: () => $"Solution Explorer is {Frame()?.Bounds.Width} wide, not {wanted}");
        if (target == "Main")
        {
            var left = f.Window.GetVisualDescendants().OfType<ToolChromeControl>().Where(c => c.IsEffectivelyVisible).Min(c => c.TranslatePoint(default, f.Window)!.Value.X);
            Assert.Equal(left, Chrome().TranslatePoint(default, f.Window)!.Value.X, 1); // the window's left edge: the leftmost
        }
    }
}
