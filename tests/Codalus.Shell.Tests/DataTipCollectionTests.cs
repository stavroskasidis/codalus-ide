using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Codalus.Debugging.Companion;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.DataTips;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The shell's collection views in the data tip (D44, decision 1 and 5, A4, A8, A9, A10): a List's elements read from its
/// fields, then Raw View with its own members; a Dictionary's entries named by their keys, each expanding to Key and
/// Value, and Grid through the view; a long list's [DebuggerDisplay] applied to the rows as they scroll into view.
/// </summary>
public sealed class DataTipCollectionTests
{
    private const string Code = "class Program\n{\n    static void Main()\n    {\n        Use(forecasts, properties);\n    }\n}\n";

    private const string ListType = "System.Collections.Generic.List<App.Forecast>";
    private const string DictionaryType = "System.Collections.Generic.Dictionary<string, object>";

    private static IReadOnlyList<TypeMember> ListMembers =>
    [
        new("_items", "field", "private", false, "System.Collections.Generic.List`1", "/dotnet/System.Private.CoreLib.dll", true, "!0[]"),
        new("_size", "field", "private", false, "System.Collections.Generic.List`1", "/dotnet/System.Private.CoreLib.dll", true, "int"),
        new("_version", "field", "private", false, "System.Collections.Generic.List`1", "/dotnet/System.Private.CoreLib.dll", true, "int"),
        new("Capacity", "property", "public", false, "System.Collections.Generic.List`1", "/dotnet/System.Private.CoreLib.dll", true, "int"),
        new("Count", "property", "public", false, "System.Collections.Generic.List`1", "/dotnet/System.Private.CoreLib.dll", false, "int"),
    ];

    /// <summary>A paused program with a list of <paramref name="count"/> forecasts and a dictionary with a free slot, as netcoredbg lists them.</summary>
    private static object[] Stops(int count)
    {
        var evaluate = new Dictionary<string, object>
        {
            ["forecasts"] = Result("{" + ListType + "}", ListType, 10),
            ["properties"] = Result("{" + DictionaryType + "}", DictionaryType, 20),
        };
        for (var i = 0; i < count; i++)
        {
            evaluate[$"(forecasts._items[{i}]).Day"] = Result(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int");
        }

        static Dictionary<string, object?> Entry(int i) =>
            Var($"[{i}]", "{System.Collections.Generic.Dictionary<string, object>.Entry}", "System.Collections.Generic.Dictionary<string, object>.Entry", 30 + i, $"properties._entries[{i}]");
        return
        [
            new Dictionary<string, object>
            {
                ["evaluate"] = evaluate,
                ["variables"] = new Dictionary<string, object>
                {
                    ["10"] = new object[]
                    {
                        Var("_items", "{App.Forecast[" + (count + 1) + "]}", "App.Forecast[]", 11, "forecasts._items"),
                        Var("_size", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, "forecasts._size"),
                        Var("_version", "3", "int", 0, "forecasts._version"),
                        Var("Capacity", (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, "forecasts.Capacity"),
                        Var("Count", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, "forecasts.Count"),
                    },
                    ["11"] = Enumerable.Range(0, count).Select(i => (object)Var($"[{i}]", "{App.Forecast}", "App.Forecast", 100 + i, $"forecasts._items[{i}]")).Append(Var($"[{count}]", "null", "App.Forecast", 0, $"forecasts._items[{count}]")).ToArray(),
                    ["20"] = new object[]
                    {
                        Var("_entries", "{System.Collections.Generic.Dictionary<string, object>.Entry[3]}", "System.Collections.Generic.Dictionary<string, object>.Entry[]", 21, "properties._entries"),
                        Var("_count", "3", "int", 0, "properties._count"),
                        Var("_freeCount", "1", "int", 0, "properties._freeCount"),
                        Var("Count", "2", "int", 0, "properties.Count"),
                    },
                    ["21"] = new object[] { Entry(0), Entry(1), Entry(2) },
                    ["30"] = new object[] { Var("hashCode", "1798518155", "uint", 0, "properties._entries[0].hashCode"), Var("next", "-1", "int", 0, "properties._entries[0].next"), Var("key", "\"server.Features\"", "string", 0, "properties._entries[0].key"), Var("value", "{int}", "int", 0, "properties._entries[0].value") },
                    ["31"] = new object[] { Var("hashCode", "0", "uint", 0, "properties._entries[1].hashCode"), Var("next", "-2", "int", 0, "properties._entries[1].next"), Var("key", "null", "string", 0, "properties._entries[1].key"), Var("value", "null", "object", 0, "properties._entries[1].value") },
                    ["32"] = new object[] { Var("hashCode", "1367684090", "uint", 0, "properties._entries[2].hashCode"), Var("next", "-1", "int", 0, "properties._entries[2].next"), Var("key", "\"application.Services\"", "string", 0, "properties._entries[2].key"), Var("value", "\"services\"", "string", 0, "properties._entries[2].value") },
                },
            },
        ];
    }

    private static void Companion(ShellFixture f) => f.Shell.Debugger.Displays = new DebuggerDisplays((type, _, _) => Task.FromResult<TypeDisplayResult?>(type switch
    {
        ListType => new TypeDisplayResult(null, null, null, [], [], null, ListMembers),
        "App.Forecast" => new TypeDisplayResult("Day = {Day}", null, null, [], [], null, []),
        _ => new TypeDisplayResult(null, null, null, [], [], null),
    }));

    [AvaloniaFact]
    public async Task A_list_shows_its_elements_then_raw_view_with_its_own_members()
    {
        await using var s = await StartAsync(Code, 5, Stops(3));
        Companion(s.F);
        var tip = await s.HoverAsync(s.OffsetOf(5, "forecasts", 1));
        Assert.Contains(tip.Header.View.Choices, c => c.Title == "Grid Visualizer"); // a list: Grid, through ToArray()
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 4 }]);
        var rows = tip.Panels[0].Rows;
        Assert.Equal(["[0]", "[1]", "[2]", "Raw View"], rows.Select(r => r.Node.Name));
        Assert.All(rows.Take(3), r => Assert.Equal(("symbol-field", "Day = " + r.Node.Name.Trim('[', ']')), (r.Node.Icon, r.Node.Value)));
        Assert.Equal(("symbol-raw-view", string.Empty), (rows[3].Node.Icon, rows[3].ValueBlock.Text));
        Assert.Equal(11, rows[0].Node.ParentReference); // set in _items, where netcoredbg names the slot [0]

        // Raw View: what netcoredbg lists, grouped (List<T> is a library type).
        tip.SetOpen(tip.Panels[0], rows[3], rows[3].Node, true);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 2);
        Assert.Equal(["Capacity", "Count", "Non-Public members"], tip.Panels[1].Rows.Select(r => r.Node.Name));
        tip.SetOpen(tip.Panels[1], tip.Panels[1].Rows[2], tip.Panels[1].Rows[2].Node, true);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 3);
        Assert.Equal(["_items", "_size", "_version"], tip.Panels[2].Rows.Select(r => r.Node.Name));
    }

    [AvaloniaFact]
    public async Task A_dictionarys_entries_are_named_by_their_keys_and_grid_reads_them_through_the_view()
    {
        await using var s = await StartAsync(Code, 5, Stops(3));
        Companion(s.F);
        var tip = await s.HoverAsync(s.OffsetOf(5, "properties", 1));
        Assert.Contains(tip.Header.View.Choices, c => c.Title == "Grid Visualizer"); // no ToArray(): through the view (decision 5)
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 3 }]);
        var rows = tip.Panels[0].Rows;
        Assert.Equal(["[\"server.Features\"]", "[\"application.Services\"]", "Raw View"], rows.Select(r => r.Node.Name)); // the free slot skipped
        Assert.Equal("\"services\"", rows[1].ValueBlock.Text);
        Assert.Equal(("properties._entries[2].value", null), (rows[1].Node.EvaluateName, rows[1].Node.ParentReference)); // set by its evaluate name

        // An entry expands to Key and Value.
        tip.SetOpen(tip.Panels[0], rows[1], rows[1].Node, true);
        await ShellFixture.PumpUntil(() => tip.Panels.Count == 2);
        Assert.Equal(["Key", "Value"], tip.Panels[1].Rows.Select(r => r.Node.Name));
        Assert.Equal(["\"application.Services\"", "\"services\""], tip.Panels[1].Rows.Select(r => r.Node.Value));

        // Grid, through the view: the keys and the values.
        await s.F.Shell.VisualizeAsync(tip.Root, VisualizerKind.Grid);
        await ShellFixture.PumpUntil(() => s.F.Shell.ActiveEditor?.Model.ReadOnlyLabel == "Grid Visualizer");
        var grid = s.F.Shell.ActiveEditor!.Model.Text.Text;
        Assert.Contains("properties (2 elements)", grid, StringComparison.Ordinal);
        Assert.Contains("Key", grid, StringComparison.Ordinal);
        Assert.Contains("\"application.Services\" │ \"services\"", grid, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_long_lists_displays_are_applied_to_the_rows_that_scroll_into_view()
    {
        await using var s = await StartAsync(Code, 5, Stops(70));
        Companion(s.F);
        var tip = await s.HoverAsync(s.OffsetOf(5, "forecasts", 1));
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Nodes.Count: 71 }] && tip.Panels[0].Rows.Count > 0);
        var panel = tip.Panels[0];
        var nodes = panel.Nodes;

        // The first 60 got their display as they loaded; the rest show their type, muted, until they show (their rows
        // are built as they scroll into view, M56).
        Assert.Equal("Day = 0", nodes[0].Value);
        Assert.Equal("{App.Forecast}", nodes[65].Value);
        Assert.True(nodes[65].IsDisplayPending);
        Assert.Null(panel.RowOf(nodes[65]));
        Assert.Equal((DataTipMetrics.MaxRows * DataTipMetrics.Row) + 2 + 4, panel.Bounds.Height, 1);

        // Scrolled to the end: the rows in view get theirs.
        var host = HostOf(panel);
        for (var i = 0; i < 40 && panel.ScrollOffset < (71 - DataTipMetrics.MaxRows) * DataTipMetrics.Row - 1; i++)
        {
            ShellFixture.RenderFrame(host);
            host.MouseWheel(InHost(panel, new Point(40, 100)), new Vector(0, -3));
            ShellFixture.Pump();
        }

        await ShellFixture.PumpUntil(() => nodes[65].Value == "Day = 65" && panel.RowOf(nodes[65]) is not null, describe: () => $"row 65: {nodes[65].Value}, scrolled to {panel.ScrollOffset}");
        Assert.False(nodes[65].IsDisplayPending);
        Assert.DoesNotContain("muted", panel.RowOf(nodes[65])!.ValueBlock.Classes);
        Assert.Equal("Day = 65", panel.RowOf(nodes[65])!.ValueBlock.Text);
    }
}
