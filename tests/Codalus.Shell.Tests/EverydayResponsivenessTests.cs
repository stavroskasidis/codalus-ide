using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

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
        async Task Flow(string name, Func<Task> flow)
        {
            await flow(); // the JIT's first time
            await WaitAsync(300);
            monitor.Reset();
            await flow();
            await WaitAsync(300);
            monitor.AssertResponsive(name, Budget);
        }

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
}
