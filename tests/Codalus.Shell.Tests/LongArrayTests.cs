using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.DataTips;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// A long array in the data tip and the variable windows (M56, the maintainer's choice): up to 1,000 elements show
/// directly; a longer array shows ranges of 1,000 that read their elements a page at a time when expanded, nested past
/// 1,000,000; a list an adapter sends whole past 1,000 shows 1,000 and how many more. The data tip builds the rows in
/// view, not one per element (it built N²/2 of them, and a big array hung the IDE), and the scripted adapter's request
/// log shows what was asked of it.
/// </summary>
public sealed class LongArrayTests
{
    private const string Code = "class Program\n{\n    static void Main()\n    {\n        Use(numbers, big, unpaged);\n    }\n}\n";

    private const int Line = 5;

    /// <summary>
    /// <c>numbers</c>, 100,000 ints; <c>big</c>, 2,500,000; both counted as netcoredbg counts an array's elements.
    /// <c>unpaged</c> lists 2,500 children without a count, as an adapter that doesn't page sends them.
    /// </summary>
    private static object[] Stops(int count = 1) =>
    [
        .. Enumerable.Range(0, count).Select(_ => (object)new Dictionary<string, object>
        {
            ["evaluate"] = new Dictionary<string, object>
            {
                ["numbers"] = Result("{int[100000]}", "int[]", 5, named: 100_000),
                ["big"] = Result("{int[2500000]}", "int[]", 6, named: 2_500_000),
                ["unpaged"] = Result("{App.Readings}", "App.Readings", 7),
            },
            ["variables"] = new Dictionary<string, object>
            {
                ["1"] = new object[]
                {
                    Var("numbers", "{int[100000]}", "int[]", 5, "numbers", named: 100_000),
                    Var("big", "{int[2500000]}", "int[]", 6, "big", named: 2_500_000),
                },
                ["5"] = Generated(100_000, "numbers"),
                ["6"] = Generated(2_500_000, "big"),
                ["7"] = Generated(2_500, "unpaged"),
            },
        }),
    ];

    private static string NewLog() => Path.Combine(Path.GetTempPath(), "codalus-long-array-" + Guid.NewGuid().ToString("N") + ".log");

    /// <summary>The variables requests made of <paramref name="reference"/>: their start and count (null without them).</summary>
    private static List<(int? Start, int? Count)> VariablesOf(string log, int reference) =>
        [.. Requests(log)
            .Where(r => r.Command == "variables" && r.Arguments.GetProperty("variablesReference").GetInt32() == reference)
            .Select(r => (r.Arguments.TryGetProperty("start", out var s) ? s.GetInt32() : (int?)null, r.Arguments.TryGetProperty("count", out var c) ? c.GetInt32() : (int?)null))];

    [AvaloniaFact]
    public async Task A_long_array_shows_ranges_of_a_thousand_that_read_their_page_and_the_tip_builds_only_the_rows_in_view()
    {
        var log = NewLog();
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(), requestLog: log);
            var tip = await s.HoverAsync(s.OffsetOf(Line, "numbers", 1));

            // 100,000 elements: 100 ranges of 1,000, and nothing asked of the array yet.
            var built = Volatile.Read(ref DataTipRow.Built);
            tip.SetOpen(null, tip.Header, tip.Root, true);
            await ShellFixture.PumpUntil(() => tip.Panels is [{ Nodes.Count: 100 }] && tip.Panels[0].Rows.Count > 0);
            var ranges = tip.Panels[0];
            ShellFixture.RenderFrame(HostOf(ranges));
            Assert.Equal(["[0..999]", "[1000..1999]"], ranges.Nodes.Take(2).Select(n => n.Name));
            Assert.Equal("[99000..99999]", ranges.Nodes[^1].Name);
            Assert.All(ranges.Nodes, n => Assert.Equal(MemberKind.Range, n.Kind));
            Assert.Empty(VariablesOf(log, 5));
            Assert.InRange(ranges.Rows.Count, 1, 40); // the rows in view and a few around them, not 100
            Assert.InRange(Volatile.Read(ref DataTipRow.Built) - built, 1, 60);

            // A range reads its 1,000 elements with one page, in a tip that builds the rows in view.
            built = Volatile.Read(ref DataTipRow.Built);
            using var monitor = new UiStallMonitor();
            tip.SetOpen(ranges, ranges.Rows[1], ranges.Nodes[1], true);
            await ShellFixture.PumpUntil(() => tip.Panels.Count == 2 && tip.Panels[1].Nodes.Count == 1000 && tip.Panels[1].Rows.Count > 0);
            var elements = tip.Panels[1];
            ShellFixture.RenderFrame(HostOf(elements));
            Assert.Equal([(1000, 1000)], VariablesOf(log, 5).Select(r => (r.Start ?? -1, r.Count ?? -1)));
            Assert.Equal(("[1000]", "1000", "numbers[1000]"), (elements.Nodes[0].Name, elements.Nodes[0].Value, elements.Nodes[0].EvaluateName));
            Assert.Equal("[1999]", elements.Nodes[^1].Name);
            Assert.Equal(5, elements.Nodes[0].ParentReference); // set in the array, whichever range it shows in
            Assert.InRange(elements.Rows.Count, 1, 40);
            Assert.InRange(Volatile.Read(ref DataTipRow.Built) - built, 1, 60); // it built 500,500 for 1,000 members
            monitor.AssertResponsive("opening a range of 1,000 elements", 1000);

            // Scrolled to the end, the last element shows; the name column stays as wide, measured from every name.
            elements.FocusRows(); // the first element selected
            var names = elements.NameColumn;
            var host = HostOf(elements);
            for (var i = 0; i < 400 && elements.ScrollOffset < (1000 - DataTipMetrics.MaxRows) * DataTipMetrics.Row - 1; i++)
            {
                ShellFixture.RenderFrame(host);
                host.MouseWheel(InHost(elements, new Point(40, 100)), new Vector(0, -10));
                ShellFixture.Pump();
            }

            await ShellFixture.PumpUntil(() => elements.Rows.Any(r => r.Node.Name == "[1999]"), describe: () => $"scrolled to {elements.ScrollOffset}, rows {string.Join(", ", elements.Rows.Select(r => r.Node.Name))}");
            Assert.Equal(names, elements.NameColumn);
            Assert.InRange(elements.Rows.Count, 1, 40);

            // F2 edits the selected element, though the wheel scrolled its row out (and it wasn't built): back in view first.
            Assert.Null(elements.RowOf(elements.Nodes[0]));
            s.F.Press(Avalonia.Input.Key.F2);
            await ShellFixture.PumpUntil(() => elements.RowOf(elements.Nodes[0]) is { Editor.IsEditing: true });
            s.F.Press(Avalonia.Input.Key.Escape);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task A_pinned_long_array_opens_in_ranges_without_asking_for_its_elements()
    {
        // The pinned tip's root kept no count, so it read the array the old way: one variables request for every element.
        var log = NewLog();
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(), requestLog: log);
            var tip = await s.HoverAsync(s.OffsetOf(Line, "numbers", 1));
            tip.Header.PinButton!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await ShellFixture.PumpUntil(() => s.Editor.PinLayer.Cascades.SingleOrDefault(c => c.Root.Name == "numbers") is { Root.HasChildren: true });
            var pinned = s.Editor.PinLayer.Cascades.Single(c => c.Root.Name == "numbers");
            pinned.SetOpen(null, pinned.Header, pinned.Root, true);
            await ShellFixture.PumpUntil(() => pinned.Panels is [{ Nodes.Count: 100 }], describe: () => $"{pinned.Panels.Count} tips, {(pinned.Panels.Count > 0 ? pinned.Panels[0].Nodes.Count : 0)} members");
            Assert.All(pinned.Panels[0].Nodes, n => Assert.Equal(MemberKind.Range, n.Kind));
            Assert.Equal(("[0..999]", "[99000..99999]"), (pinned.Panels[0].Nodes[0].Name, pinned.Panels[0].Nodes[^1].Name));
            Assert.Empty(VariablesOf(log, 5));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task Past_a_million_elements_the_ranges_nest_and_a_list_sent_whole_shows_a_thousand_and_how_many_more()
    {
        var log = NewLog();
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(), requestLog: log);

            // 2,500,000 elements: ranges of 1,000,000, each of ranges of 1,000.
            var tip = await s.HoverAsync(s.OffsetOf(Line, "big", 1));
            tip.SetOpen(null, tip.Header, tip.Root, true);
            await ShellFixture.PumpUntil(() => tip.Panels is [{ Nodes.Count: 3 }] && tip.Panels[0].Rows.Count == 3);
            Assert.Equal(["[0..999999]", "[1000000..1999999]", "[2000000..2499999]"], tip.Panels[0].Nodes.Select(n => n.Name));
            tip.SetOpen(tip.Panels[0], tip.Panels[0].Rows[2], tip.Panels[0].Nodes[2], true);
            await ShellFixture.PumpUntil(() => tip.Panels.Count == 2 && tip.Panels[1].Nodes.Count == 500 && tip.Panels[1].Rows.Count > 0);
            Assert.Equal(("[2000000..2000999]", "[2499000..2499999]"), (tip.Panels[1].Nodes[0].Name, tip.Panels[1].Nodes[^1].Name));
            tip.SetOpen(tip.Panels[1], tip.Panels[1].Rows[0], tip.Panels[1].Nodes[0], true);
            await ShellFixture.PumpUntil(() => tip.Panels.Count == 3 && tip.Panels[2].Nodes.Count == 1000);
            Assert.Equal(("[2000000]", "[2000999]"), (tip.Panels[2].Nodes[0].Name, tip.Panels[2].Nodes[^1].Name));
            Assert.Equal([(2_000_000, 1000)], VariablesOf(log, 6).Select(r => (r.Start ?? -1, r.Count ?? -1)));
            s.F.Press(Avalonia.Input.Key.Escape);

            // Sent whole, unasked: the first 1,000, and a row that says how many more.
            tip = await s.HoverAsync(s.OffsetOf(Line, "unpaged", 1));
            tip.SetOpen(null, tip.Header, tip.Root, true);
            await ShellFixture.PumpUntil(() => tip.Panels is [{ Nodes.Count: 1001 }]);
            var members = tip.Panels[0].Nodes;
            Assert.Equal("[999]", members[999].Name);
            Assert.True(members[1000].IsPlaceholder);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, "{0:N0} more", 1500), members[1000].Value);
            Assert.Equal([((int?)null, (int?)null)], VariablesOf(log, 7));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task A_range_of_an_adapter_that_sends_every_element_anyway_shows_its_own_page()
    {
        // An adapter that doesn't page answers a range's request with the whole array: the range keeps its own elements.
        var log = NewLog();
        try
        {
            object[] stops =
            [
                new Dictionary<string, object>
                {
                    ["evaluate"] = new Dictionary<string, object> { ["numbers"] = Result("{int[2500]}", "int[]", 5, named: 2_500) },
                    ["variables"] = new Dictionary<string, object> { ["5"] = Generated(2_500, "numbers") },
                },
            ];
            await using var s = await StartAsync(Code, Line, stops, requestLog: log, options: new Dictionary<string, object> { ["ignoresPaging"] = true });
            var tip = await s.HoverAsync(s.OffsetOf(Line, "numbers", 1));
            tip.SetOpen(null, tip.Header, tip.Root, true);
            await ShellFixture.PumpUntil(() => tip.Panels is [{ Nodes.Count: 3 }] && tip.Panels[0].Rows.Count == 3);
            tip.SetOpen(tip.Panels[0], tip.Panels[0].Rows[1], tip.Panels[0].Nodes[1], true);
            await ShellFixture.PumpUntil(() => tip.Panels.Count == 2 && tip.Panels[1].Nodes.Count == 1000);
            Assert.Equal(("[1000]", "[1999]"), (tip.Panels[1].Nodes[0].Name, tip.Panels[1].Nodes[^1].Name));
            Assert.Equal([(1000, 1000)], VariablesOf(log, 5).Select(r => (r.Start ?? -1, r.Count ?? -1)));
            tip.SetOpen(tip.Panels[0], tip.Panels[0].Rows[2], tip.Panels[0].Nodes[2], true);
            await ShellFixture.PumpUntil(() => tip.Panels.Count == 2 && tip.Panels[1].Nodes.Count == 500);
            Assert.Equal(("[2000]", "[2499]"), (tip.Panels[1].Nodes[0].Name, tip.Panels[1].Nodes[^1].Name));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public async Task The_longest_array_makes_its_ranges_without_overflowing()
    {
        // Near Array.MaxLength elements, the ranges' arithmetic passed int.MaxValue: a negative capacity threw, and a
        // range's last page looped for good.
        var source = new VariableSource(_ => Task.FromResult<IReadOnlyList<Codalus.Debugging.Variable>>([]));
        var bytes = new VariableNode("bytes", "{byte[2147483591]}", "byte[]", 5, source) { Counts = ChildCounts.Of("byte[]", Array.MaxLength, null) };
        var ranges = await source.ChildrenAsync(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(2148, ranges.Count);
        Assert.Equal(("[0..999999]", "[2147000000..2147483590]"), (ranges[0].Name, ranges[^1].Name));

        var last = await source.ChildrenAsync(ranges[^1], TestContext.Current.CancellationToken);
        Assert.Equal(484, last.Count);
        Assert.Equal(("[2147000000..2147000999]", "[2147483000..2147483590]"), (last[0].Name, last[^1].Name));
    }

    [AvaloniaFact]
    public async Task Locals_keeps_a_long_array_open_as_its_ranges_across_steps_without_asking_for_its_elements()
    {
        var log = NewLog();
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(count: 6), requestLog: log);
            s.F.Shell.Layout.ShowToolWindow(VariablesViewModel.LocalsWindowId, focus: false);
            VariableRowList? Find() => s.F.Window.GetVisualDescendants().OfType<Avalonia.Controls.UserControl>()
                .FirstOrDefault(v => ReferenceEquals(v.DataContext, s.F.Shell.Locals))?.GetVisualDescendants().OfType<VariableRowList>().FirstOrDefault();
            await ShellFixture.PumpUntil(() => s.F.Shell.Locals.Roots.Any(r => r.Name == "numbers") && Find() is not null);
            var list = Find()!;
            s.F.Shell.Locals.Roots.First(r => r.Name == "numbers").IsExpanded = true;
            await ShellFixture.PumpUntil(() => list.Rows.Count == 2 + 100);

            using var monitor = new UiStallMonitor();
            for (var step = 1; step <= 5; step++)
            {
                await s.F.Shell.Debugger.StepOverAsync();
                await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.State == DebugState.Stopped && s.F.Shell.Locals.Roots.FirstOrDefault(r => r.Name == "numbers") is { IsExpanded: true, Children.Count: 100 } && list.Rows.Count == 2 + 100,
                    describe: () => $"step {step}: {list.Rows.Count} rows");
            }

            monitor.AssertResponsive("five steps with a long array open in Locals", 1000);
            Assert.Empty(VariablesOf(log, 5)); // its ranges come from its count; no element was asked for
        }
        finally
        {
            File.Delete(log);
        }
    }
}
