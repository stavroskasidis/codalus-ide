using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Testing;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// The UI never hangs in everyday flows (M56, the maintainer's requirement): each flow runs on a solution large enough to
/// show work that grows with it, and no input waits longer than a budget meanwhile (<see cref="UiStallMonitor"/>). Each
/// flow runs once first, so the budget measures the flow and not the JIT (the shipped shell is compiled ahead of time).
/// The budget catches hangs, not slowdowns; CI's benchmarks measure those.
/// </summary>
public class EverydayResponsivenessTests
{
    /// <summary>
    /// The longest any input may wait in these flows, without the GC's pauses: a hang, on a loaded CI runner. The explorer's
    /// flows on 3,000 files waited 25-124 ms at most on a busy 4-core container (M56); the walk through the tree they fixed
    /// took seconds.
    /// </summary>
    private const double Budget = 1000;

    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 120);
    }

    /// <summary>Runs <paramref name="flow"/> once for the JIT, then again measured: no input may wait over the budget.</summary>
    private static async Task FlowAsync(UiStallMonitor monitor, string name, Func<Task> flow)
    {
        await flow();
        await WaitAsync(300);
        monitor.Reset();
        await flow();
        await WaitAsync(300);
        monitor.AssertResponsive(name, Budget);
    }

    [AvaloniaFact]
    public async Task Solution_explorer_flows_on_three_thousand_files()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        foreach (var (project, count) in new[] { ("App", 2000), ("Core", 1000) })
        {
            var many = Path.Combine(folder, "src", project, "Many");
            Directory.CreateDirectory(many);
            for (var i = 0; i < count; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(many, $"F{i:0000}.cs"), $"namespace {project}.Many;\n\ninternal static class F{i:0000};\n", TestContext.Current.CancellationToken);
            }
        }

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        f.Window.Height = 700;
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.FirstOrDefault(r => r.Name == "App") is { HasChildren: true }, seconds: 60);
        f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Any(v => v.IsEffectivelyVisible));
        var explorer = f.Shell.Explorer;
        var files = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? Path.Combine(folder, "src", "App", "Many", $"F{i * 97:0000}.cs")
            : Path.Combine(folder, "src", "Core", "Many", $"F{i * 47:0000}.cs")).ToList();
        explorer.TrackActiveItem = true;
        await ShellFixture.PumpUntil(() => explorer.TrackActiveItem);

        using var monitor = new UiStallMonitor();
        Task Flow(string name, Func<Task> flow) => FlowAsync(monitor, name, flow);

        await Flow("Track Active Item while opening files far apart", async () =>
        {
            foreach (var file in files)
            {
                await f.Shell.OpenFileAsync(file);
                await WaitAsync(20);
            }
        });

        await Flow("tab switches, as Ctrl+Tab held down", async () =>
        {
            foreach (var editor in f.Shell.Editors.Where(e => e.Owner is not null).ToList())
            {
                f.Shell.Layout.OpenDocument(editor);
                await WaitAsync(30);
            }
        });

        await Flow("Sync with Active Document after Collapse All", async () =>
        {
            foreach (var editor in f.Shell.Editors.Where(e => e.Owner is not null).Take(5).ToList())
            {
                f.Shell.Layout.OpenDocument(editor);
                explorer.CollapseAll();
                await WaitAsync(30);
                explorer.SyncWithActiveDocument();
                await WaitAsync(30);
            }
        });

        await Flow("typing in the search box and clearing it", async () =>
        {
            foreach (var text in new[] { "f", "f1", "f19", "f199", string.Empty })
            {
                explorer.Filter = text;
                await WaitAsync(80);
            }

            await ShellFixture.PumpUntil(() => !explorer.IsFiltering);
        });

        await Flow("Close All Documents, then opening them again", async () =>
        {
            f.Shell.CloseAllDocuments(exceptActive: false);
            await WaitAsync(100);
            foreach (var file in files)
            {
                await f.Shell.OpenFileAsync(file);
            }
        });
    }

    [AvaloniaFact]
    public async Task Error_list_flows_on_a_hundred_thousand_diagnostics()
    {
        // An unrestored solution reports 100,000 errors and more: each keystroke in the search box, each toggle and each
        // regrouping sorted them all on the UI thread, up to a second each (M56).
        await using var f = ShellFixture.Create();
        var root = Path.Combine(Path.GetTempPath(), "codalus-everyday-" + Guid.NewGuid().ToString("N"));
        Diagnostic[] items = [.. Enumerable.Range(0, 500).Select(line => new Diagnostic(
            new Range(new Position(line, 0), new Position(line, 4)), line % 3 == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, "CS0246", "csharp", "The type or namespace name 'Telerik' could not be found"))];
        var list = f.Shell.ErrorList;
        for (var file = 0; file < 200; file++)
        {
            f.Shell.Languages.Diagnostics.Set(Path.Combine(root, $"File{file:000}.cs"), "csharp", null, items);
        }

        f.Shell.Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => list.Rows.Count == 100_000 && !list.IsRefreshing, seconds: 120);

        using var monitor = new UiStallMonitor();
        await FlowAsync(monitor, "typing in the Error List's search box and clearing it", async () =>
        {
            foreach (var text in new[] { "F", "Fi", "Fil", "File1", "File12", string.Empty })
            {
                list.Filter = text;
                await WaitAsync(60);
            }

            await ShellFixture.PumpUntil(() => !list.IsRefreshing && list.Rows.Count == 100_000, seconds: 120);
        });

        await FlowAsync(monitor, "hiding warnings, grouping by file and collapsing groups", async () =>
        {
            list.ShowWarnings = false;
            await ShellFixture.PumpUntil(() => !list.IsRefreshing, seconds: 120);
            list.ShowWarnings = true;
            list.GroupBy = Array.IndexOf(ErrorListViewModel.GroupByValues, "file");
            await ShellFixture.PumpUntil(() => !list.IsRefreshing && list.Items.Count == 100_200, seconds: 120);
            for (var i = 0; i < 2; i++)
            {
                foreach (var group in list.Items.OfType<ErrorListGroup>().Take(10).ToList())
                {
                    list.ToggleGroup(group);
                    await WaitAsync(20);
                }
            }

            list.GroupBy = 0;
            await ShellFixture.PumpUntil(() => !list.IsRefreshing && list.Items.Count == 100_000, seconds: 120);
        });
    }

    [AvaloniaFact]
    public async Task A_test_run_of_twenty_thousand_tests_with_editors_open()
    {
        // During discovery and runs the test service reports a change every 50 ms: at each one, every editor looked
        // through every test of the solution twice, and the Test Explorer arranged them all, shown or not (M56).
        await using var f = ShellFixture.Create();
        const string controller = "codalus.synthetic:tests";
        var folder = Path.Combine(f.UserData, "tests");
        Directory.CreateDirectory(folder);
        var items = new List<TestItemDto>();
        var paths = new List<string>();
        for (var d = 0; d < 200; d++)
        {
            var path = Path.Combine(folder, $"Tests{d:000}.txt");
            paths.Add(path);
            await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 210).Select(i => $"line {i}")), TestContext.Current.CancellationToken);
            var uri = DocumentUris.FromPath(path);
            items.Add(new TestItemDto($"p{d}", null, $"P{d}", null, null, null, null, null, null));
            items.Add(new TestItemDto($"p{d}|c", $"p{d}", "Calc", null, uri, new RangeDto(0, 0, 205, 0), null, null, null));
            for (var t = 0; t < 100; t++)
            {
                items.Add(new TestItemDto($"p{d}|m{t}", $"p{d}|c", "Test" + t, null, uri, new RangeDto((2 * t) + 1, 0, (2 * t) + 2, 0), null, null, null));
            }
        }

        var tests = f.Shell.Tests;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", controller, "Synthetic"), new TestRefreshTests.Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(controller, [.. items], []));
        foreach (var path in paths.Take(20))
        {
            await f.Shell.OpenFileAsync(path);
        }

        // The Start page opens in the left column once the Welcome plugin runs, and takes the column's tab: the Test
        // Explorer shows after it (a later plugin host, after another test, covered it).
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible("welcome.view"), seconds: 60);
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Shell.TestExplorer.IsViewShown && f.Shell.TestExplorer.Summary.Contains("20000 tests", StringComparison.Ordinal), seconds: 120, describe: () => $"shown: {f.Shell.TestExplorer.IsViewShown}, summary: {f.Shell.TestExplorer.Summary}");

        using var monitor = new UiStallMonitor();
        var runs = 0;
        await FlowAsync(monitor, "a run reporting 20,000 results", async () =>
        {
            var run = "r" + ++runs;
            tests.RunStarted(new TestRunStartedParams(controller, run, "Run", null, "run"));
            for (var batch = 0; batch < 100; batch++)
            {
                TestStateDto[] states = [.. Enumerable.Range(batch * 200, 200).Select(n => new TestStateDto($"p{n / 100}|m{n % 100}", n % 17 == 0 ? TestStates.Failed : TestStates.Passed, 1, null))];
                tests.RunUpdated(new TestRunUpdateParams(run, states, [], []));
                await WaitAsync(30);
            }

            tests.RunEnded(run);
            await ShellFixture.PumpUntil(() => f.Shell.TestExplorer.Summary.Contains("1177 failed", StringComparison.Ordinal), seconds: 120);
        });
    }
}
