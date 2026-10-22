using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Debugging.Companion;
using Codalus.Shell.Controls;
using Codalus.Shell.Views.DataTips;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The data tip's rows and icons (D44, A5, A7, R): kinds from the companion's member list, your own type's non-public
/// members inline with the lock badge, a library's under Non-Public members, Static members last, the blue wrench for
/// properties, a getter's exception as "threw Type: message" and netcoredbg's &lt;error&gt; as "Couldn't evaluate" with
/// Re-evaluate.
/// </summary>
public sealed class DataTipRowsTests
{
    private const string Code = "class Controller\n{\n    void Get()\n    {\n        this.Run();\n    }\n}\n";

    private static readonly IReadOnlyList<TypeMember> Members =
    [
        new("ControllerContext", "property", "public", false, "Lib.ControllerBase", "/lib/Lib.dll", true, "Lib.ControllerContext"),
        new("logger", "field", "private", false, "App.Controller", "/app/App.dll", false, "App.Logger"),
        new("Session", "property", "public", false, "App.Controller", "/app/App.dll", false, "string"),
        new("Slow", "property", "public", false, "App.Controller", "/app/App.dll", false, "int"),
        new("_tempData", "field", "private", false, "Lib.ControllerBase", "/lib/Lib.dll", true, "Lib.TempData"),
        new("Summaries", "field", "public", true, "App.Controller", "/app/App.dll", true, "string"),
    ];

    private static object[] Stops() =>
    [
        new Dictionary<string, object>
        {
            ["evaluate"] = new Dictionary<string, object>
            {
                ["this"] = Result("{App.Controller}", "App.Controller", 10),
                ["this.Slow"] = Result("42", "int"),
            },
            ["variables"] = new Dictionary<string, object>
            {
                // As netcoredbg lists them: fields first, then properties, then its Static members.
                ["10"] = new object[]
                {
                    Var("logger", "{App.Logger}", "App.Logger", 0, "this.logger"),
                    Var("_tempData", "{Lib.TempData}", "Lib.TempData", 0, "this._tempData"),
                    Var("ControllerContext", "{Lib.ControllerContext}", "Lib.ControllerContext", 0, "this.ControllerContext"),
                    Var("Session", "{System.InvalidOperationException}", "System.InvalidOperationException", 11, "this.Session"),
                    Var("Slow", "<error>", "int", 0, "this.Slow"),
                    Var("Static members", "", "", 12, "App.Controller"),
                },
                ["11"] = new object[] { Var("_message", "\"Session has not been configured.\"", "string", 0, "this.Session._message"), Var("Message", "\"Session has not been configured.\"", "string", 0, "this.Session.Message") },
                ["12"] = new object[] { Var("Summaries", "\"Freezing\"", "string", 0, "App.Controller.Summaries") },
            },
        },
    ];

    [AvaloniaFact]
    public async Task Rows_take_their_kinds_icons_and_groups_from_the_member_list()
    {
        await using var s = await StartAsync(Code, 5, Stops());
        s.F.Shell.Debugger.Displays = new DebuggerDisplays((type, _, _) => Task.FromResult<TypeDisplayResult?>(new TypeDisplayResult(null, null, null, [], [], null, type == "App.Controller" ? Members : null)));
        s.F.Shell.Debugger.IsUserModule = path => path == "/app/App.dll";
        var tip = await s.HoverAsync(s.OffsetOf(5, "this", 1));
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 6 }]);
        var rows = tip.Panels[0].Rows;

        // Inline in name order, your private field among them with the lock badge; then the library's, then the statics.
        Assert.Equal(["ControllerContext", "logger", "Session", "Slow", "Non-Public members", "Static members"], rows.Select(r => r.Node.Name));
        Icon IconOf(DataTipRow row) => row.GetVisualDescendants().OfType<Icon>().First(i => i.Kind != "chevron-right" && i.Kind != "chevron-down");
        Assert.Equal(("symbol-property", null), (IconOf(rows[0]).Kind, IconOf(rows[0]).Badge));
        Assert.Equal(("symbol-field", "lock"), (IconOf(rows[1]).Kind, IconOf(rows[1]).Badge));
        Assert.Equal("Private field of Controller (your code) · App.Logger", ToolTip.GetTip(rows[1].GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("dataTipName"))));
        Assert.Equal(("lock", "symbol-class"), (IconOf(rows[4]).Kind, IconOf(rows[5]).Kind));
        Assert.Equal(string.Empty, rows[4].ValueBlock.Text);

        // A getter that threw: its exception's type and message, in the error color, and it still expands.
        Assert.Equal("threw InvalidOperationException: Session has not been configured.", rows[2].ValueBlock.Text);
        Assert.Contains("error", rows[2].ValueBlock.Classes);
        Assert.True(rows[2].Node.HasChildren);
        Assert.Equal("symbol-property", IconOf(rows[2]).Kind);

        // netcoredbg's <error>: "Couldn't evaluate", and Re-evaluate asks again.
        Assert.Equal("Couldn't evaluate", rows[3].ValueBlock.Text);
        Assert.Contains("error", rows[3].ValueBlock.Classes);
        var reevaluate = rows[3].GetVisualDescendants().OfType<Button>().Single(b => b.GetVisualDescendants().OfType<Icon>().Any(i => i.Kind == "refresh"));
        reevaluate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); // a click: a tooltip left open under load can take a real one
        await ShellFixture.PumpUntil(() => rows[3].ValueBlock.Text == "42", describe: () => $"value {rows[3].ValueBlock.Text}, tip open {s.Editor.DataTip is not null}, row attached {TopLevel.GetTopLevel(rows[3]) is not null}, button visible {reevaluate.IsEffectivelyVisible}, trace {s.Editor.QuickInfoTrace}");
        Assert.DoesNotContain("error", rows[3].ValueBlock.Classes);
        Assert.False(reevaluate.IsVisible);

        // The groups open as any row does: the library's private field, and the static field.
        tip.SetOpen(tip.Panels[0], rows[4], rows[4].Node, true);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 2);
        Assert.Equal(["_tempData"], tip.Panels[1].Rows.Select(r => r.Node.Name));
        Assert.Equal(("symbol-field", null), (IconOf(tip.Panels[1].Rows[0]).Kind, IconOf(tip.Panels[1].Rows[0]).Badge));
        Assert.Equal("Private field of ControllerBase · Lib.TempData", tip.Panels[1].Rows[0].Node.NameTip);
        tip.SetOpen(tip.Panels[0], rows[5], rows[5].Node, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [_, { Rows: [{ Node.IsPlaceholder: false }] }], describe: () => $"panels {tip.Panels.Count}: {string.Join(" | ", tip.Panels.Select(p => string.Join(", ", p.Rows.Select(r => r.Node.Name + "=" + r.Node.Value))))}");
        Assert.Equal(("Summaries", "symbol-field"), (tip.Panels[1].Rows[0].Node.Name, tip.Panels[1].Rows[0].Node.Icon));
    }

    [AvaloniaFact]
    public void The_data_tips_icon_kinds_are_drawn_at_build_time_in_their_colors_and_the_lock_is_a_badge()
    {
        // Raw View: a committed SVG (eng/icons/symbol-icons.py); the View button's visualizer: a stroke drawing. Both blue.
        Assert.NotNull(IconSet.GetFilled("symbol-raw-view"));
        Assert.Contains("symbol-raw-view", IconSet.SymbolNames);
        Assert.NotNull(IconSet.Get("visualizer"));
        Assert.Equal((CommandIcons.Blue, CommandIcons.Blue), (CommandIcons.ColorOf("symbol-raw-view"), CommandIcons.ColorOf("visualizer")));
        Assert.Equal(CommandIcons.Blue, CommandIcons.ColorOf("symbol-property")); // the completion list's blue wrench (decision 4)

        // The lock as a 9 px badge at the icon's bottom right, in its own yellow, over the field's blue.
        var plain = new Icon { Kind = "symbol-field", Width = 16, Height = 16, Foreground = Avalonia.Media.Brushes.Black };
        var badged = new Icon { Kind = "symbol-field", Width = 16, Height = 16, Foreground = Avalonia.Media.Brushes.Black, Badge = "lock", BadgeBackground = Avalonia.Media.Brushes.White };
        var window = new Window { Width = 80, Height = 40, Background = Avalonia.Media.Brushes.White, Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Children = { plain, badged } } };
        window.Show();
        try
        {
            ShellFixture.RenderFrame(window);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
            using var buffer = frame.Lock();
            Avalonia.Media.Color Pixel(int x, int y)
            {
                var offset = (y * buffer.RowBytes) + (x * 4);
                var (b0, b1, b2, b3) = (System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 1), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 2), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 3));
                return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Avalonia.Media.Color.FromArgb(b3, b0, b1, b2) : Avalonia.Media.Color.FromArgb(b3, b2, b1, b0);
            }

            var scale = frame.PixelSize.Width / window.Bounds.Width;
            var yellowish = 0;
            var corner = 0;
            for (var y = (int)(9 * scale); y < (int)(16 * scale); y++)
            {
                for (var x = (int)(9 * scale); x < (int)(16 * scale); x++)
                {
                    var badge = Pixel(x + (int)(16 * scale), y);
                    if (badge.R >= badge.G && badge.G > badge.B && badge.R - badge.B > 60)
                    {
                        yellowish++;
                    }

                    if (Pixel(x, y) != badge)
                    {
                        corner++;
                    }
                }
            }

            var seen = string.Join(" ", Enumerable.Range((int)(9 * scale), (int)(7 * scale)).Select(x => Pixel(x + (int)(16 * scale), (int)(13 * scale)).ToString()));
            Assert.True(yellowish > 4, $"{yellowish} yellow pixels in the badge's corner (scale {scale}, frame {frame.PixelSize}, a row: {seen}; Shell.Icon.Yellow {(window.TryFindResource("Shell.Icon.Yellow", window.ActualThemeVariant, out var token) ? token : "none")})");
            Assert.True(corner > 4, "the badged icon's corner looks like the plain one's");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Without_the_member_list_nothing_is_grouped_and_icons_come_from_the_names()
    {
        // A remote program (decision 15): the companion can't read its modules.
        await using var s = await StartAsync(Code, 5, Stops());
        s.F.Shell.Debugger.Displays = new DebuggerDisplays((_, _, _) => Task.FromResult<TypeDisplayResult?>(new TypeDisplayResult(null, null, null, [], [], null)));
        var tip = await s.HoverAsync(s.OffsetOf(5, "this", 1));
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 6 }]);
        var rows = tip.Panels[0].Rows;
        Assert.Equal(["logger", "_tempData", "ControllerContext", "Session", "Slow", "Static members"], rows.Select(r => r.Node.Name));
        Assert.Equal(["symbol-field", "symbol-field", "symbol-property", "symbol-property", "symbol-property", "symbol-class"], rows.Select(r => r.Node.Icon));
        Assert.DoesNotContain(rows, r => r.Node.HasBadge);
        Assert.Equal("{System.InvalidOperationException}", rows[3].ValueBlock.Text); // no declared type to tell it threw
    }
}
