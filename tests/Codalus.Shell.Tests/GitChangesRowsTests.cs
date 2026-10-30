using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// Git Changes with thousands of changed files (M56): a status read builds the window's files off the UI thread and changes
/// nothing when nothing changed; a hidden window builds no tree; a shown one shows each section's tree as a virtualized list
/// of rows, updated in place, whose folders expand and collapse without building the tree again. On a plain folder made a
/// repository, so no build or language host starts.
/// </summary>
public sealed class GitChangesRowsTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
        return output;
    }

    /// <summary>A repository with one commit and <paramref name="folders"/> × <paramref name="files"/> untracked files under src/.</summary>
    internal static string CreateRepository(string folder, int folders, int files)
    {
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, "README.md"), "initial\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        for (var d = 0; d < folders; d++)
        {
            var directory = Path.Combine(folder, "src", $"D{d:000}");
            Directory.CreateDirectory(directory);
            for (var i = 0; i < files; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"F{i:00}.cs"), "// generated\n");
            }
        }

        return Path.GetFullPath(folder);
    }

    private static int Count(INotifyCollectionChanged collection, List<NotifyCollectionChangedAction> actions)
    {
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions.Count;
    }

    [AvaloniaFact]
    public async Task A_status_read_that_changes_nothing_changes_no_row_and_a_hidden_window_builds_no_tree()
    {
        await using var f = ShellFixture.Create();
        var folder = CreateRepository(Path.Combine(f.UserData, "Many"), folders: 200, files: 10);
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        var vm = f.Shell.GitChanges;
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 2000 && !vm.IsRefreshing, seconds: 60);

        // Hidden: the files are current (the commands read them), but no tree or list is built.
        Assert.False(vm.IsViewShown);
        Assert.Equal(0, vm.ViewsBuilt);
        Assert.Empty(vm.ChangesRows);
        Assert.Empty(vm.ChangesTree);

        // The same status again changes no file: no reset of 2,000 rows, nothing built.
        List<NotifyCollectionChangedAction> files = [];
        Count(vm.Changes, files);
        var first = vm.Changes[0];
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Empty(files);
        Assert.Same(first, vm.Changes[0]);
        Assert.Equal(0, vm.ViewsBuilt);

        // Shown: the tree is built once, as rows, and only the rows in view get controls.
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(
            () => vm.IsViewShown && vm.ViewsBuilt >= 1 && !vm.IsRefreshing,
            describe: () => $"shown {vm.IsViewShown}, built {vm.ViewsBuilt} times, refreshing {vm.IsRefreshing}, rows {vm.ChangesRows.Count}");
        Assert.Equal(1 + 1 + 200 + 2000, vm.ChangesRows.Count); // the repository, src, its folders, their files
        Assert.Equal([folder, "src", "D000", "F00.cs"], vm.ChangesRows.Take(4).Select(r => r.Text));
        Assert.Equal([0, 1, 2, 3], vm.ChangesRows.Take(4).Select(r => r.Depth));
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesTree");
        await ShellFixture.PumpUntil(() => list.GetRealizedContainers().Any());
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 150);
        Assert.InRange(view.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 200);

        // The same status again changes no row.
        List<NotifyCollectionChangedAction> rows = [];
        Count(vm.ChangesRows, rows);
        var row = vm.ChangesRows[5];
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Empty(rows);
        Assert.Empty(files);
        Assert.Same(row, vm.ChangesRows[5]);

        // A file gone: its row goes, with one removal; the rows around it stay, the counts change in place.
        File.Delete(Path.Combine(folder, "src", "D150", "F03.cs"));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 1999 && !vm.IsRefreshing);
        Assert.Equal([NotifyCollectionChangedAction.Remove], rows);
        Assert.Equal([NotifyCollectionChangedAction.Reset], files);
        Assert.Equal(2201, vm.ChangesRows.Count);
        Assert.Same(row, vm.ChangesRows[5]);
        Assert.Equal(1999, vm.ChangesRows[0].Node.TotalFiles);

        // Collapsing the repository hides its rows with one removal; expanding shows them with one insertion.
        rows.Clear();
        vm.SetExpanded(vm.ChangesRows[0], false);
        Assert.Single(vm.ChangesRows);
        Assert.Contains(", collapsed", vm.ChangesRows[0].AccessibleName, StringComparison.Ordinal);
        vm.SetExpanded(vm.ChangesRows[0], true);
        Assert.Equal(2201, vm.ChangesRows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], rows);

        // A collapsed folder stays collapsed through refreshes.
        var d010 = vm.ChangesRows.Single(r => r.Text == "D010");
        vm.SetExpanded(d010, false);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Equal(2191, vm.ChangesRows.Count);
        Assert.False(vm.ChangesRows.Single(r => r.Text == "D010").IsExpanded);

        // The same Git LFS state again (read after every status read) refreshes nothing.
        var built = vm.ViewsBuilt;
        vm.Lfs = new GitLfsStatus(false, false, [], [], null);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(built, vm.ViewsBuilt);
    }
}
