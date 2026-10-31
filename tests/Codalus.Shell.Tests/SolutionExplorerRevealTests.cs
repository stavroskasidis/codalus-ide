using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Solution Explorer's reveals (M56): Sync with Active Document and Track Active Item jump to the row in one step, without
/// building the tree again, once per burst of activations, and not while the explorer is hidden.
/// </summary>
public class SolutionExplorerRevealTests
{
    /// <summary>
    /// The fixture with a folder of 200 files in App, the project above Core, opened and shown, without a restore and once
    /// its design-time builds are done: nothing builds the tree then but the tests. A restore's evaluations, and the
    /// design-time builds after them, came during the steps that count the tree's builds, on a busy machine (a load
    /// average of 9 on 4 cores failed three of these tests, a build more each, before M56's review fixes as after).
    /// </summary>
    private static async Task<(ShellFixture F, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        try
        {
            var solution = f.CopyFixture();
            var folder = Path.GetDirectoryName(solution)!;
            var many = Path.Combine(folder, "src", "App", "Many");
            Directory.CreateDirectory(many);
            for (var i = 0; i < 200; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(many, $"F{i:000}.cs"), $"namespace App.Many;\n\ninternal static class F{i:000};\n", TestContext.Current.CancellationToken);
            }

            await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, Codalus.Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            f.Window.Height = 500;
            await f.Shell.OpenSolutionAsync(solution);
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.DesignTimeProgress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);
            await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.FirstOrDefault(r => r.Name == "App") is { HasChildren: true });
            f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => View(f) is { IsEffectivelyVisible: true });
            return (f, folder);
        }
        catch
        {
            await f.DisposeAsync(); // the test's own disposal never comes: its window and hosts would stay for the tests after it
            throw;
        }
    }

    private static SolutionExplorerView? View(ShellFixture f) => f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().FirstOrDefault(v => v.IsEffectivelyVisible);

    private static ListBox List(ShellFixture f) => View(f)!.FindControl<ListBox>("List")!;

    private static bool InView(ShellFixture f, ExplorerRowViewModel row)
    {
        var list = List(f);
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        return list.ContainerFromItem(row) is Control { IsEffectivelyVisible: true } container
            && container.TranslatePoint(default, scroller) is { } at && at.Y >= -1 && at.Y + container.Bounds.Height <= scroller.Viewport.Height + 1;
    }

    private static readonly int[] s_spread = [5, 60, 120, 180, 199];

    private static List<(string Id, int Depth, bool Expanded)> Shape(SolutionExplorerViewModel explorer) => [.. explorer.Rows.Select(r => (r.Id, r.Depth, r.IsExpanded))];

    [AvaloniaFact]
    public async Task A_reveal_above_the_selected_row_jumps_to_it_in_one_step()
    {
        // The maintainer's report: the reveal "traverses the tree". Each row inserted above the selected one moved the
        // list's selection anchor, and Avalonia queued a scroll to the selected row for each: 200 rows, 200 scrolls, a
        // frame each, then a jump back to the revealed row.
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var last = explorer.Rows[^1]; // below App, whose rows come in above it
        Assert.True(explorer.Rows.IndexOf(last) > explorer.Rows.IndexOf(explorer.Rows.First(r => r.Name == "App")));
        explorer.Reveal(last.Id);
        ShellFixture.Pump();
        Assert.True(InView(f, explorer.Selected!), $"{last.Name} isn't in view");

        var scroller = List(f).GetVisualDescendants().OfType<ScrollViewer>().First();
        var scrolls = 0;
        var changes = new List<NotifyCollectionChangedAction>();
        scroller.PropertyChanged += (_, e) => scrolls += e.Property == ScrollViewer.OffsetProperty ? 1 : 0; // ScrollChanged comes once per layout pass
        var anchors = 0;
        ((System.ComponentModel.INotifyPropertyChanged)List(f).Selection).PropertyChanged += (_, e) => anchors += e.PropertyName == "AnchorIndex" ? 1 : 0;
        explorer.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        var builds = explorer.Builds;

        var target = Path.Combine(folder, "src", "App", "Many", "F150.cs");
        explorer.Reveal("f:" + target);
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(scrolls <= 1 && anchors <= 2, $"the list scrolled {scrolls} times, its anchor moved {anchors} times");
        Assert.Equal(target, explorer.Selected?.Node.Path);
        Assert.True(InView(f, explorer.Selected!), "F150.cs isn't in view");
        Assert.Equal([NotifyCollectionChangedAction.Add], changes); // App's rows, with Many's, in one insertion
        Assert.Equal(builds, explorer.Builds); // and the tree wasn't built again

        // Collapse All with the last row selected: one removal, and the list stays where it is.
        explorer.Reveal(explorer.Rows[^1].Id);
        ShellFixture.Pump();
        changes.Clear();
        scrolls = 0;
        explorer.CollapseAll();
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(scrolls <= 1, $"Collapse All scrolled {scrolls} times");
        Assert.True(changes.Count <= 2, string.Join(", ", changes));
    }

    [AvaloniaFact]
    public async Task Expanding_collapsing_and_revealing_give_the_rows_a_full_refresh_gives_without_building_the_tree()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var builds = explorer.Builds;
        ExplorerRowViewModel Row(string name) => explorer.Rows.First(r => r.Name == name);
        void Check(string step)
        {
            var incremental = Shape(explorer);
            explorer.Refresh();
            Assert.True(incremental.SequenceEqual(Shape(explorer)), $"after {step}: the rows differ from a full refresh's");
            builds = explorer.Builds;
        }

        explorer.Reveal("f:" + Path.Combine(folder, "src", "App", "Many", "F010.cs"));
        Assert.Equal(builds, explorer.Builds);
        Check("a reveal that expands App and Many");
        explorer.SetExpanded(Row("Many"), false);
        Assert.Equal(builds, explorer.Builds);
        Check("collapsing Many");
        explorer.SetExpanded(Row("App"), false);
        explorer.SetExpanded(Row("App"), true);
        Assert.Equal(builds, explorer.Builds);
        Check("collapsing and expanding App (Many stays collapsed)");
        explorer.Toggle(Row("Core"));
        explorer.Reveal("f:" + Path.Combine(folder, "src", "App", "Many", "F199.cs"));
        Assert.Equal(builds, explorer.Builds);
        Check("toggling Core and revealing the last file of Many");
        Assert.Equal("F199.cs", explorer.Selected?.Name);
    }

    [AvaloniaFact]
    public async Task Track_active_item_reveals_once_per_burst_and_not_while_the_explorer_is_hidden()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        string[] files = [.. s_spread.Select(i => Path.Combine(folder, "src", "App", "Many", $"F{i:000}.cs")), Path.Combine(folder, "src", "Core", "Greeter.cs")];
        foreach (var file in files)
        {
            await f.Shell.OpenFileAsync(file);
        }

        explorer.TrackActiveItem = true;
        await ShellFixture.PumpUntil(() => explorer.TrackActiveItem && explorer.Selected?.Node.Path == files[^1]);
        var revealed = new List<string?>();
        explorer.RevealRequested += (_, row) => revealed.Add(row.Node.Path);

        // Every tab activated in turn (Next Document held down): one reveal comes, for the last one.
        var editors = f.Shell.Editors.Where(e => files.Contains(e.Model.Path)).ToList();
        foreach (var editor in editors)
        {
            f.Shell.Layout.OpenDocument(editor);
        }

        await ShellFixture.PumpUntil(() => revealed.Count > 0);
        ShellFixture.Pump();
        Assert.Equal([editors[^1].Model.Path], revealed);
        Assert.Equal(editors[^1].Model.Path, explorer.Selected?.Node.Path);

        // Close All passes through every tab: nothing is revealed and nothing expands.
        explorer.CollapseAll();
        var shape = Shape(explorer);
        revealed.Clear();
        f.Shell.CloseAllDocuments(exceptActive: false);
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        Assert.Empty(revealed);
        Assert.True(shape.SequenceEqual(Shape(explorer)), "Close All changed the explorer's rows");

        // Hidden, the explorer doesn't follow; shown again, it catches up with the active document.
        f.Shell.Layout.CloseToolWindow(SolutionExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => View(f) is null);
        var builds = explorer.Builds;
        foreach (var file in files.Take(3))
        {
            await f.Shell.OpenFileAsync(file);
        }

        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(revealed.Count == 0, $"revealed while hidden: {string.Join(", ", revealed)}; shown: {explorer.IsViewShown}; views: {string.Join(", ", f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Select(v => $"{v.IsAttachedToVisualTree()}/{v.IsEffectivelyVisible}"))}");
        Assert.Equal(builds, explorer.Builds);
        f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == files[2] && View(f) is not null && InView(f, explorer.Selected!));
    }

    [AvaloniaFact]
    public async Task A_view_built_again_while_the_old_one_shows_keeps_the_explorer_shown()
    {
        // Dock builds a tool window's view again for a new layout while the old one still shows: the new view says it's
        // shown, then the old one says it isn't. With one flag for both, the explorer took itself for hidden while on
        // screen, and Track Active Item stopped following.
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var second = new Window { Width = 300, Height = 400, Content = new SolutionExplorerView { DataContext = explorer } };
        second.Show();
        ShellFixture.Pump();
        f.Shell.Layout.CloseToolWindow(SolutionExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => View(f) is null);
        Assert.True(explorer.IsViewShown, "the explorer took itself for hidden while a view still shows it");

        explorer.TrackActiveItem = true;
        var file = Path.Combine(folder, "src", "App", "Many", "F120.cs");
        await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == file);

        second.Close();
        await ShellFixture.PumpUntil(() => !explorer.IsViewShown);
    }

    [AvaloniaFact]
    public async Task Sync_on_a_project_file_selects_its_project_and_the_search_box_applies_the_last_filter()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;

        // A project reference carries the referenced project's path: Sync found it before the project itself.
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        await f.Shell.OpenFileAsync(app);
        await f.RunAsync("explorer.syncWithActiveDocument");
        Assert.Equal(Codalus.Workbench.Projects.ExplorerNodeKind.Project, explorer.Selected?.Node.Kind);
        Assert.Equal(app, explorer.Selected?.Node.Path);

        // Typing a filter: the matches are found once typing pauses, off the UI thread, and only the last applies.
        var resets = 0;
        explorer.Rows.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        explorer.Filter = "f1";
        explorer.Filter = "f19";
        explorer.Filter = "f199";
        await ShellFixture.PumpUntil(() => !explorer.IsFiltering);
        Assert.Equal(1, resets);
        Assert.Contains(explorer.Rows, r => r.Name == "F199.cs");
        Assert.DoesNotContain(explorer.Rows, r => r.Name == "F190.cs");

        // Sync clears the filter and selects the file at once.
        await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Core", "Greeter.cs"));
        await f.RunAsync("explorer.syncWithActiveDocument");
        Assert.Equal(string.Empty, explorer.Filter);
        Assert.Equal("Greeter.cs", explorer.Selected?.Name);
    }
}
