using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Codalus.Debugging.Companion;
using Codalus.Shell.Views.DataTips;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// Editing, changes and pins in the data tip (D44, decisions 6, 7 and 12, A11, A12, A14, A15, A16): values edited in
/// place (Enter sets, Esc cancels, the engine's error keeps the box open with its message, a value that can't change
/// opens no box and says why), values that changed since the last stop in red, any row with an evaluate name pinned,
/// and pinned tips with the header's look that expand the same way and are muted while the program runs.
/// </summary>
public sealed class DataTipEditingTests
{
    private const string Code = "class Program\n{\n    static void Main()\n    {\n        app.Use();\n        app.Use();\n    }\n}\n";

    private static readonly IReadOnlyList<TypeMember> Members =
    [
        new("Count", "field", "public", false, "App.Builder", "/app/App.dll", true, "int"),
        new("Name", "property", "public", false, "App.Builder", "/app/App.dll", true, "string"),
        new("Size", "property", "public", false, "App.Builder", "/app/App.dll", false, "int"),
    ];

    /// <summary>Two stops: between them Count goes from 3 to 4.</summary>
    private static object[] Stops() =>
    [
        Stop(5, "3"),
        Stop(6, "4"),
    ];

    private static Dictionary<string, object> Stop(int line, string count) => new()
    {
        ["line"] = line,
        ["evaluate"] = new Dictionary<string, object> { ["app"] = Result("{App.Builder}", "App.Builder", 10) },
        ["variables"] = new Dictionary<string, object>
        {
            ["10"] = new object[]
            {
                Var("Count", count, "int", 0, "app.Count"),
                Var("Name", "\"Development\"", "string", 0, "app.Name"),
                Var("Size", "2", "int", 0, "app.Size"),
            },
        },
        ["errors"] = new Dictionary<string, object> { ["app.Name=Production"] = "error: The name 'Production' does not exist in the current context" },
    };

    private static async Task<DataTipScenario> StartAsync()
    {
        var s = await DataTipScenario.StartAsync(Code, 5, Stops());
        s.F.Shell.Debugger.Displays = new DebuggerDisplays((type, _, _) => Task.FromResult<TypeDisplayResult?>(new TypeDisplayResult(null, null, null, [], [], null, type == "App.Builder" ? Members : null)));
        return s;
    }

    private static async Task<DataTipPanel> OpenAsync(DataTipScenario s, int line)
    {
        var tip = await s.HoverAsync(s.OffsetOf(line, "app", 1));
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 3 }] && !tip.Panels[0].Rows[0].Node.IsPlaceholder);
        ShellFixture.RenderFrame(s.F.Window);
        return tip.Panels[0];
    }

    [AvaloniaFact]
    public async Task Values_are_edited_in_place_and_the_engines_refusal_keeps_the_box_open()
    {
        await using var s = await StartAsync();
        var panel = await OpenAsync(s, 5);
        var name = panel.Rows[1];

        // A click on the value opens its box, the value selected; Enter sets it.
        s.Click(name.ValueBlock);
        await ShellFixture.PumpUntil(() => name.Editor.Box is { IsFocused: true }, describe: () => $"editing {name.Editor.IsEditing}, focus on {TopLevel.GetTopLevel(name)?.FocusManager?.GetFocusedElement()}, reason '{s.Editor.DataTip?.ReadOnlyReason(name.Node)}'");
        var box = name.Editor.Box!;
        Assert.Equal("\"Development\"", box.Text);
        Assert.Equal((0, box.Text!.Length), (box.SelectionStart, box.SelectionEnd));
        box.Text = "\"Staging\"";
        s.F.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => !name.Editor.IsEditing, describe: () => $"error {name.Editor.Error}, box '{name.Editor.Box?.Text}' focused {name.Editor.Box?.IsFocused}, focus on {TopLevel.GetTopLevel(name)?.FocusManager?.GetFocusedElement()}");
        Assert.Equal("\"Staging\"", name.ValueBlock.Text);
        Assert.True(name.ValueBlock.IsVisible);

        // Typed without quotes, the engine refuses: the box stays, its edge red, the message under it; Esc gives up.
        name.Editor.Begin();
        await ShellFixture.PumpUntil(() => name.Editor.Box is { IsFocused: true });
        name.Editor.Box!.Text = "Production";
        s.F.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => name.Editor.Error is not null);
        Assert.True(name.Editor.IsEditing);
        Assert.Contains("error", name.Editor.Box!.Classes);
        Assert.Equal("error: The name 'Production' does not exist in the current context", name.Editor.Error);
        var message = name.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single();
        Assert.True(message.IsOpen);
        Assert.Contains(name.Editor.Error, message.Child!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        s.F.Press(Key.Escape);
        Assert.False(name.Editor.IsEditing);
        Assert.False(message.IsOpen);
        Assert.Equal("\"Staging\"", name.ValueBlock.Text);
        Assert.NotNull(s.Editor.DataTip); // Esc in the box closes the box, not the tips

        // F2 on the selected row edits too; the header's value is edited by its expression (setExpression).
        panel.FocusRows();
        panel.Select(panel.Rows[0]);
        s.F.Press(Key.F2);
        await ShellFixture.PumpUntil(() => panel.Rows[0].Editor.Box is { IsFocused: true });
        panel.Rows[0].Editor.Box!.Text = "9";
        s.F.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => panel.Rows[0].ValueBlock.Text == "9");
        Assert.Equal("9", (await s.F.Shell.Debugger.EvaluateAsync("app.Count", "watch", TestContext.Current.CancellationToken)).Result);
    }

    [AvaloniaFact]
    public async Task A_value_that_cant_change_opens_no_box_and_says_why()
    {
        await using var s = await StartAsync();
        var panel = await OpenAsync(s, 5);
        var size = panel.Rows[2];
        s.Click(size.ValueBlock);
        ShellFixture.Pump();
        Assert.False(size.Editor.IsEditing);
        Assert.Equal("2\nSize can't be changed: it has no setter.", ToolTip.GetTip(size.ValueBlock));
        Assert.False(size.Editor.Begin());
        Assert.Equal("\"Development\"", ToolTip.GetTip(panel.Rows[1].ValueBlock)); // one that can: its value alone
    }

    [AvaloniaFact]
    public async Task Values_that_changed_since_the_last_stop_are_red()
    {
        await using var s = await StartAsync();
        var panel = await OpenAsync(s, 5);
        Assert.DoesNotContain(panel.Rows, r => r.ValueBlock.Classes.Contains("changed")); // nothing to compare with yet
        s.F.Press(Key.Escape);

        await s.F.Shell.Debugger.StepOverAsync();
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.State == Workbench.Debugging.DebugState.Stopped && s.Editor.CurrentDebugLine() == 6);
        panel = await OpenAsync(s, 6);
        Assert.Equal("4", panel.Rows[0].ValueBlock.Text);
        Assert.Contains("changed", panel.Rows[0].ValueBlock.Classes); // Count: 3 at the last stop
        Assert.DoesNotContain("changed", panel.Rows[1].ValueBlock.Classes); // Name: the same
        Assert.True(panel.Rows[0].Node.IsChanged);
    }

    [AvaloniaFact]
    public async Task Any_row_pins_and_pinned_tips_take_the_headers_look_and_expand()
    {
        await using var s = await StartAsync();
        var panel = await OpenAsync(s, 5);

        // The pin at the end of a row with an evaluate name pins that expression to the line.
        var count = panel.Rows[0];
        Assert.NotNull(count.PinButton);
        s.Click(count.PinButton!);
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.PinnedTips.All.Count == 1);
        var pinned = s.F.Shell.Debugger.PinnedTips.All.Single();
        Assert.Equal(("app.Count", 5), (pinned.Expression, pinned.Line));
        Assert.False(s.Editor.IsQuickInfoOpen);

        // The header's own pin: app, which expands like the hover's.
        var tip = await s.HoverAsync(s.OffsetOf(5, "app", 1));
        s.Click(tip.Header.PinButton!);
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.PinnedTips.All.Count == 2 && s.Editor.PinLayer.Cascades.Count == 2);
        await ShellFixture.PumpUntil(() => s.Editor.PinLayer.Cascades.SingleOrDefault(c => c.Root.Name == "app") is { Root.HasChildren: true });
        var app = s.Editor.PinLayer.Cascades.Single(c => c.Root.Name == "app");
        Assert.NotNull(app.Header.UnpinButton);
        Assert.Null(app.Header.PinButton);
        Assert.Equal("{App.Builder}", app.Header.ValueBlock.Text);
        Assert.Equal("symbol-local", app.Root.Icon);
        Assert.Equal("symbol-property", s.Editor.PinLayer.Cascades.Single(c => c.Root.Name == "app.Count").Root.Icon); // a member: its kind guessed from its name
        app.SetOpen(null, app.Header, app.Root, true);
        await ShellFixture.PumpUntil(() => app.Panels is [{ Rows.Count: 3 }] && !app.Panels[0].Rows[0].Node.IsPlaceholder);
        Assert.Equal(["Count", "Name", "Size"], app.Panels[0].Rows.Select(r => r.Node.Name));
        ShellFixture.RenderFrame(s.F.Window);
        var header = s.InTextView(app.Header);
        var rows = s.InTextView(app.Panels[0]);
        Assert.Equal(header.Left + DataTipMetrics.Indent, rows.Left, 1);
        Assert.True(Math.Abs(header.Bottom - rows.Top) <= 1 || Math.Abs(header.Top - rows.Bottom) <= 1, $"header {header}, rows {rows}");

        // A step: Count changed, so its pinned tip is red; the two tips on line 5 stack.
        await s.F.Shell.Debugger.StepOverAsync();
        await ShellFixture.PumpUntil(() => s.Editor.PinLayer.Cascades.SingleOrDefault(c => c.Root.Name == "app.Count")?.Header.ValueBlock.Text == "4");
        Assert.Contains("changed", s.Editor.PinLayer.Cascades.Single(c => c.Root.Name == "app.Count").Header.ValueBlock.Classes);
        Assert.Equal(2, ((StackPanel)s.Editor.PinLayer.Pins[5]).Children.OfType<DataTipHeader>().Count());

        // The program ends: the last values stay, muted, and nothing expands.
        await s.F.Shell.Debugger.ContinueAsync();
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.State == Workbench.Debugging.DebugState.Idle);
        await ShellFixture.PumpUntil(() => s.Editor.PinLayer.Cascades.Count == 2 && s.Editor.PinLayer.Cascades.All(c => c.Header.ValueBlock.Classes.Contains("muted")));
        app = s.Editor.PinLayer.Cascades.Single(c => c.Root.Name == "app");
        Assert.Equal("{App.Builder}", app.Header.ValueBlock.Text);
        Assert.False(app.Header.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("dataTipChevron")).IsHitTestVisible);

        // Unpin from its strip.
        s.Click(app.Header.UnpinButton!);
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.PinnedTips.All.Count == 1);
    }
}
