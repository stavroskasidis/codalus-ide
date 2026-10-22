using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Views.DataTips;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The data tip laid out as the classic IDE's (D44, docs/design/controls/DataTip.dc.html, A1, A5, A10, A18, A19): the
/// header over the hovered expression, a tip of its own per expanded level (opened by the pointer resting on a chevron,
/// a click or Right), 20 px rows without a type column, values trimmed at 400 px, 15 rows before a tip scrolls, tips that
/// flip up near the bottom edge and move left near the right one, a header that passes the code's gestures on, and Ctrl
/// fading the tips.
/// </summary>
public sealed class DataTipLayoutTests
{
    private const int StopLine = 6;

    private static readonly string LongValue = "\"" + new string('x', 300) + " the end\"";

    /// <summary>A program with <c>app</c> on many lines, so one can be near the bottom of the view.</summary>
    private static string Code()
    {
        var lines = new List<string> { "using App;", "class Program", "{", "    static void Main()", "    {" };
        for (var i = 0; i < 150; i++)
        {
            lines.Add($"        app.Use(items); // {i}");
        }

        lines.AddRange(["    }", "}"]);
        return string.Join('\n', lines) + "\n";
    }

    private static object[] Stops(int members = 0) =>
    [
        new Dictionary<string, object>
        {
            ["evaluate"] = new Dictionary<string, object>
            {
                ["app"] = Result("{App.Builder}", "App.Builder", 10),
            },
            ["variables"] = new Dictionary<string, object>
            {
                ["1"] = new object[] { Var("app", "{App.Builder}", "App.Builder", 10, "app") },
                ["10"] = members > 0
                    ? Enumerable.Range(0, members).Select(i => (object)Var($"Item{i:00}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, $"app.Item{i:00}")).ToArray()
                    : new object[]
                    {
                        Var("Count", "3", "int", 0, "app.Count"),
                        Var("Items", "{System.Collections.Generic.List<string>}", "System.Collections.Generic.List<string>", 11, "app.Items"),
                        Var("_name", "\"builder\"", "string", 0, "app._name"),
                        Var("Description", LongValue, "string", 0, "app.Description"),
                    },
                ["11"] = new object[] { Var("[0]", "\"a\"", "string", 0, "app.Items[0]"), Var("[1]", "\"b\"", "string", 0, "app.Items[1]") },
            },
        },
    ];

    /// <summary>What is under a point of a window, from the element hit up through its parents (a failure message).</summary>
    private static string Under(TopLevel host, Point point)
    {
        var parts = new List<string>();
        for (var visual = host.InputHitTest(point) as Visual; visual is not null && parts.Count < 8; visual = visual.GetVisualParent())
        {
            parts.Add($"{visual.GetType().Name}{((visual as Control)?.Classes.FirstOrDefault() is { } c ? "." + c : "")}@{visual.Bounds}");
        }

        return parts.Count > 0 ? string.Join(" < ", parts) : "nothing";
    }

    [AvaloniaFact]
    public async Task The_header_sits_over_the_expression_and_its_members_open_in_tips_of_their_own()
    {
        await using var s = await StartAsync(Code(), StopLine, Stops());
        var word = s.OffsetOf(StopLine, "app");
        var tip = await s.HoverAsync(word + 1);

        // The header: over the expression, its name on the code's; the value in its box; the type in the name's tooltip.
        var header = tip.Header;
        var name = s.InTextView(header.NameBlock);
        var code = s.InView(word);
        Assert.True(Math.Abs(name.Left - (code.X - 3)) <= 1.5, $"the header's name at {name.Left}, the code's at {code.X - 3}");
        Assert.True(Math.Abs(name.Center.Y - code.Y) <= 2, $"the header's name at y {name.Center.Y}, the line's middle at {code.Y}");
        Assert.Equal(DataTipMetrics.Header, header.Bounds.Height);
        Assert.Equal("app", header.NameBlock.Text);
        Assert.Equal("{App.Builder}", header.ValueBlock.Text);
        Assert.Equal("App.Builder", ToolTip.GetTip(header.NameBlock));
        Assert.DoesNotContain("App.Builder", Texts(header));
        Assert.Equal("symbol-local", header.Node.Icon);
        Assert.False(header.IsOpen);

        // A click on its chevron: the members in a tip of their own, its top on the header's bottom edge, 24 px in.
        var chevron = header.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("dataTipChevron"));
        s.Click(chevron);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows: [{ Node.IsPlaceholder: false }, ..] }]);
        ShellFixture.RenderFrame(s.F.Window);
        var members = tip.Panels[0];
        ShellFixture.RenderFrame(HostOf(members));
        Assert.True(header.IsOpen);
        var headerBounds = s.InTextView(header);
        var membersBounds = s.InTextView(members);
        Assert.True(Math.Abs(headerBounds.Left + DataTipMetrics.Indent - membersBounds.Left) <= 1 && Math.Abs(headerBounds.Bottom - membersBounds.Top) <= 1,
            $"header {headerBounds}, members {membersBounds}, members' host {HostOf(members).GetType().Name} {HostOf(members).Bounds}, header's host {HostOf(header).GetType().Name}, padding {members.Padding} border {members.BorderThickness}, view {s.View.Bounds}");
        Assert.Equal(["Count", "Items", "_name", "Description"], members.Rows.Select(r => r.Node.Name));

        // Rows 20 px; no type column: the type is the name's tooltip; a long value stops at 400 px, whole in its tooltip.
        Assert.All(members.Rows, r => Assert.Equal(DataTipMetrics.Row, r.Bounds.Height));
        Assert.DoesNotContain("int", Texts(members));
        Assert.DoesNotContain("System.Collections.Generic.List<string>", Texts(members));
        var count = members.Rows[0];
        Assert.Equal("int", ToolTip.GetTip(count.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("dataTipName"))));
        var description = members.Rows[3];
        Assert.True(description.ValueBlock.Bounds.Width <= DataTipMetrics.MaxValueWidth, $"the long value is {description.ValueBlock.Bounds.Width} px wide");
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, description.ValueBlock.TextTrimming);
        Assert.Equal(LongValue, ToolTip.GetTip(description.ValueBlock));
        Assert.False(count.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("dataTipChevron")).IsVisible); // nothing to expand

        // The names line up: one column as wide as the widest name; the values start at one x.
        var values = members.Rows.Select(r => s.InTextView(r.ValueBlock).Left).Distinct().ToList();
        Assert.Single(values);

        // The pointer resting on Items' chevron (about 400 ms) opens its tip under its row, 24 px in.
        var items = members.Rows[1];
        var itemsChevron = items.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("dataTipChevron"));
        s.MoveOnto(itemsChevron);
        Assert.True(items.IsHoverOpenPending || tip.Panels.Count == 2, $"pointer over the row {items.IsPointerOver}, the chevron {itemsChevron.IsPointerOver} ({s.InTextView(itemsChevron)}), under it {Under(HostOf(itemsChevron), InHost(itemsChevron, new Point(8, 10)))}");
        Assert.Single(tip.Panels); // not yet
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 2 && tip.Panels[1].Rows.Count == 2 && !tip.Panels[1].Rows[0].Node.IsPlaceholder);
        var elements = tip.Panels[1];
        ShellFixture.RenderFrame(HostOf(elements));
        Assert.True(items.IsOpen);
        Assert.Contains("open", items.Classes);
        var itemsBounds = s.InTextView(items);
        var elementsBounds = s.InTextView(elements);
        Assert.Equal(membersBounds.Left + DataTipMetrics.Indent, elementsBounds.Left, 1);
        Assert.Equal(itemsBounds.Bottom, elementsBounds.Top, 1);
        Assert.Equal(["[0]", "[1]"], elements.Rows.Select(r => r.Node.Name));

        // Into the child tip: the chain stays. To another row of the same tip: that row's child closes.
        s.MoveOnto(elements.Rows[0]);
        Assert.Equal(2, tip.Panels.Count);
        s.MoveOnto(count);
        Assert.Single(tip.Panels);
        Assert.False(items.IsOpen);

        // From the keyboard: Right opens the selected row's tip (and takes the focus), Left closes it, Esc closes them all.
        members.FocusRows();
        members.Select(items);
        s.F.Press(Key.Right);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 2);
        Assert.True(tip.Panels[1].IsKeyboardFocusWithin);
        s.F.Press(Key.Left);
        Assert.True(members.IsKeyboardFocusWithin);
        s.F.Press(Key.Left);
        Assert.Single(tip.Panels);
        s.F.Press(Key.Escape);
        Assert.False(s.Editor.IsQuickInfoOpen);
        Assert.Null(s.Editor.DataTip);
    }

    [AvaloniaFact]
    public async Task A_tip_shows_15_rows_then_scrolls_and_the_tips_flip_up_near_the_bottom_edge_and_move_left_near_the_right()
    {
        await using var s = await StartAsync(Code(), StopLine, Stops(members: 40));

        // The last whole line in view: no room under it for the members.
        s.View.EnsureVisualLines();
        var last = s.View.VisualLines.Last(l => l.VisualTop + l.Height - s.View.VerticalOffset <= s.View.Bounds.Height).FirstDocumentLine.LineNumber;
        var tip = await s.HoverAsync(s.OffsetOf(last, "app", 1));
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 40 }]);
        ShellFixture.RenderFrame(s.F.Window);
        var members = tip.Panels[0];
        ShellFixture.RenderFrame(HostOf(members));
        Assert.True(tip.IsFlipped);
        var header = s.InTextView(tip.Header);
        var panel = s.InTextView(members);
        Assert.Equal(header.Top, panel.Bottom, 1);
        Assert.Equal(header.Left + DataTipMetrics.Indent, panel.Left, 1);

        // 15 rows, then it scrolls; the wheel scrolls it rather than the editor.
        Assert.True(Math.Abs((DataTipMetrics.MaxRows * DataTipMetrics.Row) + 2 + 4 - members.Bounds.Height) <= 1, $"members {members.Bounds}, padding {members.Padding} border {members.BorderThickness}, host {HostOf(members).GetType().Name}");
        var scroll = s.View.VerticalOffset;
        HostOf(members).MouseWheel(InHost(members, new Point(40, 100)), new Vector(0, -1));
        ShellFixture.Pump();
        Assert.True(members.ScrollOffset > 0);
        Assert.Equal(scroll, s.View.VerticalOffset);
        Assert.NotNull(s.Editor.DataTip);
        s.F.Press(Key.Escape);

        // Near the right edge, the header moves left to stay in view.
        var document = s.Editor.Editor.Document;
        var line = document.GetLineByNumber(StopLine + 1);
        while (s.InView(s.OffsetOf(StopLine + 1, "app")).X < s.View.Bounds.Width - 100)
        {
            document.Insert(line.Offset, "          ");
        }

        ShellFixture.Pump();
        tip = await s.HoverAsync(s.OffsetOf(StopLine + 1, "app", 1));
        Assert.True(s.InTextView(tip.Header).Right <= s.View.Bounds.Width + 0.5, $"the header at {s.InTextView(tip.Header)}, the view {s.View.Bounds.Width} wide");
    }

    [AvaloniaFact]
    public async Task The_header_passes_the_codes_gestures_on_and_Ctrl_fades_the_tips()
    {
        await using var s = await StartAsync(Code(), StopLine, Stops());
        var word = s.OffsetOf(StopLine, "app");

        var tip = await s.HoverAsync(word + 1);

        // When the text moves while the tip is open (CodeLens rows arriving above it, a larger font), the tip moves with it.
        var before = s.InView(word);
        await s.F.Shell.Workbench.Settings.SetAsync("editor.fontSize", 18.0, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => s.InView(word).Y > before.Y + 10, describe: () => $"the text at {s.InView(word)}, was {before}");
        ShellFixture.RenderFrame(s.F.Window);
        Assert.Same(tip, s.Editor.DataTip);
        var name = s.InTextView(tip.Header.NameBlock);
        Assert.True(name.Top <= s.InView(word).Y && s.InView(word).Y <= name.Bottom, $"the header's name at {name}, app at {s.InView(word)}");

        // A double-click on the header's name selects the word under it.
        var host = HostOf(tip.Header.NameBlock);
        var at = InHost(tip.Header.NameBlock, new Point(6, 8));
        host.MouseDown(at, MouseButton.Left);
        host.MouseUp(at, MouseButton.Left);
        host.MouseDown(at, MouseButton.Left);
        host.MouseUp(at, MouseButton.Left);
        ShellFixture.Pump();
        Assert.False(s.Editor.IsQuickInfoOpen);
        Assert.Equal("app", s.Editor.Editor.SelectedText);

        // Ctrl+click goes to the definition of the word under it: the caret moves there (no language service here).
        s.Editor.Editor.Select(0, 0);
        tip = await s.HoverAsync(s.OffsetOf(StopLine + 2, "app", 1));
        s.Click(tip.Header.NameBlock, new Point(6, 8), RawInputModifiers.Control);
        Assert.False(s.Editor.IsQuickInfoOpen);
        Assert.InRange(s.Editor.Editor.CaretOffset, s.OffsetOf(StopLine + 2, "app"), s.OffsetOf(StopLine + 2, "app", 3));

        // The wheel scrolls the editor and closes the tips.
        tip = await s.HoverAsync(word + 1);
        var later = s.OffsetOf(StopLine + 8, "app", 1);
        var scroll = s.View.VerticalOffset;
        host = HostOf(tip.Header);
        host.MouseWheel(InHost(tip.Header, new Point(10, 10)), new Vector(0, -1));
        ShellFixture.Pump();
        await ShellFixture.PumpUntil(() => s.View.VerticalOffset > scroll, describe: () => $"the editor at {s.View.VerticalOffset}: {s.Editor.QuickInfoTrace}");
        Assert.False(s.Editor.IsQuickInfoOpen);
        await s.SettleAsync();

        // Ctrl fades the tips and makes them click-through while held.
        tip = await s.HoverAsync(later);
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 1);
        s.Editor.Editor.TextArea.Focus();
        s.F.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        ShellFixture.Pump();
        Assert.True(tip.IsFaded);
        Assert.All(new Control[] { tip.Header, tip.Panels[0] }, c =>
        {
            Assert.True(c.Opacity < 0.5);
            Assert.False(c.IsHitTestVisible);
        });
        s.F.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        ShellFixture.Pump();
        Assert.False(tip.IsFaded);
        Assert.All(new Control[] { tip.Header, tip.Panels[0] }, c =>
        {
            Assert.Equal(1, c.Opacity);
            Assert.True(c.IsHitTestVisible);
        });
    }
}
