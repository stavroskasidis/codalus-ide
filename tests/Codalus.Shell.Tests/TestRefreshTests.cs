using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Test Explorer and the editors' test glyphs and CodeLens while tests change (M56), on tests fed to the model by
/// hand: the explorer rebuilds nothing while hidden and at most once per <see cref="TestExplorerViewModel.ChangeInterval"/>
/// while shown, the last change always showing; an editor refreshes for its own document only, once it shows.
/// </summary>
public sealed class TestRefreshTests
{
    private const string Controller = "codalus.synthetic:tests";

    /// <summary>A project with a class at the document's first line and a test on every other line after it.</summary>
    private static IEnumerable<TestItemDto> Items(string project, string path, int tests)
    {
        var uri = DocumentUris.FromPath(path);
        yield return new TestItemDto(project, null, project, null, null, null, null, null, null);
        yield return new TestItemDto(project + "|c", project, "Calc", null, uri, new RangeDto(0, 0, (2 * tests) + 1, 0), null, null, null);
        for (var i = 0; i < tests; i++)
        {
            yield return new TestItemDto(Test(project, i), project + "|c", "Test" + i, null, uri, new RangeDto((2 * i) + 1, 0, (2 * i) + 2, 0), null, null, null);
        }
    }

    private static string Test(string project, int i) => $"{project}|m{i}";

    /// <summary>The one-based line of a test's glyph and CodeLens.</summary>
    private static int Line(int i) => (2 * i) + 2;

    private static void Report(TestService tests, string run, string id, string state) =>
        tests.RunUpdated(new TestRunUpdateParams(run, [new TestStateDto(id, state, 1, null)], [], []));

    /// <summary>Lets the test service's coalesced notifications, and what they post, run.</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(TestService.NotifyInterval);
            ShellFixture.Pump();
        }
    }

    [AvaloniaFact]
    public async Task The_test_explorer_rebuilds_nothing_while_hidden_and_at_most_five_times_a_second_while_shown()
    {
        await using var f = ShellFixture.Create();
        await f.StartViewShownAsync(); // it takes the Test Explorer's column when it comes
        var tests = f.Shell.Tests;
        var explorer = f.Shell.TestExplorer;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", Controller, "Synthetic"), new Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(Controller, [.. Items("P", Path.Combine(f.UserData, "CalcTests.cs"), 50)], []));
        await SettleAsync();
        Assert.False(explorer.IsViewShown);

        // Hidden: a run's results rebuild nothing (each rebuild arranges every test of the solution).
        var builds = explorer.Builds;
        tests.RunStarted(new TestRunStartedParams(Controller, "r1", "Run", null, "run"));
        for (var i = 0; i < 50; i++)
        {
            Report(tests, "r1", Test("P", i), TestStates.Passed);
            await Task.Delay(10, TestContext.Current.CancellationToken);
            ShellFixture.Pump();
        }

        tests.RunEnded("r1");
        await SettleAsync();
        Assert.Equal(builds, explorer.Builds);

        // Shown: one rebuild catches up.
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => explorer.IsViewShown && explorer.Summary.Contains("50 passed", StringComparison.Ordinal));
        await SettleAsync();
        Assert.Equal(builds + 1, explorer.Builds);

        // Shown, during a run: at most one rebuild per interval (the service reports every 50 ms), and the last results show.
        builds = explorer.Builds;
        var clock = Stopwatch.StartNew();
        tests.RunStarted(new TestRunStartedParams(Controller, "r2", "Run", null, "run"));
        for (var i = 0; i < 50; i++)
        {
            Report(tests, "r2", Test("P", i), TestStates.Failed);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            ShellFixture.Pump();
        }

        tests.RunEnded("r2");
        await ShellFixture.PumpUntil(() => explorer.Summary.Contains("0 passed · 50 failed", StringComparison.Ordinal));
        var elapsed = clock.Elapsed;
        var most = (int)(elapsed / TestExplorerViewModel.ChangeInterval) + 2;
        Assert.True(explorer.Builds - builds <= most, $"{explorer.Builds - builds} rebuilds in {elapsed.TotalMilliseconds:0} ms, at most {most} expected");
        Assert.All(explorer.Rows.Where(r => r.Group.Node?.IsLeaf == true), row => Assert.True(row.Style.IsFailed, row.Label));

        // Closed: nothing again.
        f.Shell.Layout.CloseToolWindow(TestExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => !explorer.IsViewShown);
        builds = explorer.Builds;
        tests.RunStarted(new TestRunStartedParams(Controller, "r3", "Run", null, "run"));
        Report(tests, "r3", Test("P", 0), TestStates.Passed);
        tests.RunEnded("r3");
        await SettleAsync();
        Assert.Equal(builds, explorer.Builds);
    }

    [AvaloniaFact]
    public async Task A_view_built_again_while_the_old_one_shows_keeps_the_test_explorer_shown()
    {
        // Dock builds a tool window's view again for a new layout while the old one still shows: the new view says it's
        // shown, then the old one says it isn't. With one flag for both, the explorer took itself for hidden while on
        // screen, and stopped showing results.
        await using var f = ShellFixture.Create();
        await f.StartViewShownAsync(); // it takes the Test Explorer's column when it comes
        var tests = f.Shell.Tests;
        var explorer = f.Shell.TestExplorer;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", Controller, "Synthetic"), new Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(Controller, [.. Items("P", Path.Combine(f.UserData, "CalcTests.cs"), 5)], []));
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => explorer.IsViewShown);

        var second = new Avalonia.Controls.Window { Width = 400, Height = 300, Content = new Views.TestExplorerView { DataContext = explorer } };
        second.Show();
        ShellFixture.Pump();
        f.Shell.Layout.CloseToolWindow(TestExplorerViewModel.ToolWindowId);
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(explorer.IsViewShown, "the explorer took itself for hidden while a view still shows it");
        var builds = explorer.Builds;
        tests.RunStarted(new TestRunStartedParams(Controller, "r1", "Run", null, "run"));
        Report(tests, "r1", Test("P", 0), TestStates.Passed);
        tests.RunEnded("r1");
        await ShellFixture.PumpUntil(() => explorer.Summary.Contains("1 passed", StringComparison.Ordinal));
        Assert.True(explorer.Builds > builds);

        second.Close();
        await ShellFixture.PumpUntil(() => !explorer.IsViewShown);
    }

    [AvaloniaFact]
    public async Task Editors_refresh_their_test_glyphs_and_codelens_for_their_own_document_once_shown()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "tests");
        Directory.CreateDirectory(folder);
        string[] paths = [Path.Combine(folder, "A.txt"), Path.Combine(folder, "B.txt"), Path.Combine(folder, "C.txt")];
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}")), TestContext.Current.CancellationToken);
        }

        var tests = f.Shell.Tests;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", Controller, "Synthetic"), new Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(Controller, [.. Items("A", paths[0], 10), .. Items("B", paths[1], 10), .. Items("C", paths[2], 10)], []));

        // C is the active tab; A and B are hidden behind it (each shown once, so that it has its editor).
        var editors = new List<CodeEditorController>();
        foreach (var path in paths)
        {
            var editor = await f.Shell.OpenFileAsync(path);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            editors.Add(editor!.Controller!);
        }

        var (a, b, c) = (editors[0], editors[1], editors[2]);
        await ShellFixture.PumpUntil(() => editors.All(e => e.TestMargin.Glyphs.Count == 11) && c.Insights.ExtraLenses(Line(0)).Contains("Not run"));
        await SettleAsync();
        string Counts() => string.Join(", ", editors.Select(e => $"{e.Model.Name}: {e.TestMargin.Refreshes} glyphs, {e.Insights.TestLensUpdates} lenses"));
        var before = editors.Select(e => (e.TestMargin.Refreshes, e.Insights.TestLensUpdates)).ToList();

        // A result in C: C follows; A and B, hidden and not its document, find nothing again.
        tests.RunStarted(new TestRunStartedParams(Controller, "r1", "Run", null, "run"));
        Report(tests, "r1", Test("C", 3), TestStates.Failed);
        await ShellFixture.PumpUntil(() => c.Insights.ExtraLenses(Line(3)).Contains("✗ Failed"), describe: Counts);
        await SettleAsync();
        Assert.True(before[0] == (a.TestMargin.Refreshes, a.Insights.TestLensUpdates) && before[1] == (b.TestMargin.Refreshes, b.Insights.TestLensUpdates), Counts());
        var shown = (c.TestMargin.Refreshes, c.Insights.TestLensUpdates);

        // A result in A while it's hidden: nobody refreshes, C (shown) included.
        Report(tests, "r1", Test("A", 5), TestStates.Passed);
        await SettleAsync();
        Assert.True(before[0] == (a.TestMargin.Refreshes, a.Insights.TestLensUpdates) && shown == (c.TestMargin.Refreshes, c.Insights.TestLensUpdates), Counts());
        Assert.Contains("Not run", a.Insights.ExtraLenses(Line(5)));

        // A shown: it catches up, once.
        f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == paths[0]));
        await ShellFixture.PumpUntil(() => a.Insights.ExtraLenses(Line(5)).Any(l => l.StartsWith("✓ Passed", StringComparison.Ordinal)), describe: Counts);
        await SettleAsync();
        Assert.True((before[0].Refreshes + 1, before[0].TestLensUpdates + 1) == (a.TestMargin.Refreshes, a.Insights.TestLensUpdates), Counts());
        Assert.Equal(before[1], (b.TestMargin.Refreshes, b.Insights.TestLensUpdates));
    }

    /// <summary>A controller's connection that runs nothing (the tests report results to the service themselves).</summary>
    internal sealed class Connection : ITestControllerConnection
    {
        public Task<IReadOnlyList<string>> RunAsync(TestRunParams request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
