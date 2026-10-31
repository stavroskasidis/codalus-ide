using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// The folder view (Open Folder) lists each folder alone, once it's expanded (M57): a change lists again only the listed
/// folders it names, four at a time in batches, and the tree is built once a batch changed something. It listed the whole
/// workspace folder, up to 20,000 entries, for every change, build and change of the loaded projects, built every node on
/// the UI thread, and showed what was past the 20,000th entry as empty folders.
/// </summary>
public class SolutionExplorerFolderViewTests
{
    private const string NotInFolder = "The active document isn't in the folder.";

    private static void Write(string folder, params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }
    }

    /// <summary>
    /// A folder of plain files (and a project file nobody loads, so nothing evaluates), opened in the folder view once its
    /// first listing has come: every listing counted after it is the test's own.
    /// </summary>
    private static async Task<(ShellFixture F, string Folder)> OpenAsync(Action<string>? prepare = null)
    {
        var f = ShellFixture.Create();
        try
        {
            var folder = Path.Combine(f.UserData, "ws");
            Write(folder, "README.md", "src/App/App.csproj", "src/App/Program.cs", "src/App/Models/Person.cs", "src/App/bin/Debug/App.dll",
                "docs/guide.md", "docs/deep/notes.md", "node_modules/left-pad/index.js", ".git/description");
            prepare?.Invoke(folder);
            f.Shell.Trust.Trust(folder);
            await f.Shell.OpenFolderWorkspaceAsync(folder);
            await ShellFixture.PumpUntil(() => f.Shell.Explorer.IsFolderView && f.Shell.Explorer.Rows.Any(r => r.Name == "README.md"), seconds: 60);
            await SettledAsync(f.Shell.Explorer);
            return (f, folder);
        }
        catch
        {
            await f.DisposeAsync(); // the test's own disposal never comes: its window and hosts would stay for the tests after it
            throw;
        }
    }

    /// <summary>Until no listing is queued or on its way, and the refresh it queued has run.</summary>
    private static async Task SettledAsync(SolutionExplorerViewModel explorer)
    {
        await ShellFixture.PumpUntil(() => !explorer.IsListingDisk, seconds: 60);
        ShellFixture.Pump();
    }

    private static ExplorerRowViewModel? Row(SolutionExplorerViewModel explorer, string id) => explorer.Rows.FirstOrDefault(r => r.Id == id);

    private static async Task ExpandAsync(SolutionExplorerViewModel explorer, string folder)
    {
        await ShellFixture.PumpUntil(() => Row(explorer, "d:" + folder) is not null, seconds: 60);
        explorer.SetExpanded(Row(explorer, "d:" + folder)!, true);
        await SettledAsync(explorer);
    }

    /// <summary>Runs <paramref name="change"/>, waits for what it listed, and checks how many folders that was.</summary>
    private static async Task ListedAsync(SolutionExplorerViewModel explorer, Action change, int folders)
    {
        var listings = explorer.FolderListings;
        change();
        await SettledAsync(explorer);
        Assert.Equal(listings + folders, explorer.FolderListings);
    }

    private static ExplorerNode? Find(ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsLoadingRow(ExplorerRowViewModel row) => row.Id.EndsWith("|listing", StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task Only_the_workspace_folder_is_listed_at_open_and_each_folder_once_its_expanded()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var docs = Path.Combine(folder, "docs");
        var modules = Path.Combine(folder, "node_modules");

        // The workspace folder alone: node_modules shows, dimmed, and .git doesn't.
        Assert.Equal(1, explorer.FolderListings);
        Assert.Equal(["docs", "node_modules", "src", "README.md"], explorer.Rows.Where(r => r.Depth == 1 && r.Name != ".codalus").Select(r => r.Name));
        Assert.True(Row(explorer, "d:" + modules) is { IsDimmed: true, HasChildren: true });
        Assert.False(Row(explorer, "d:" + docs)!.IsDimmed);
        Assert.True(Find(explorer.Root!, "d:" + docs) is { IsUnlisted: true, Children.Count: 0 });

        // docs shows "Loading…" until its own listing has come, and its folders aren't listed.
        var builds = explorer.Builds;
        await ListedAsync(explorer, () =>
        {
            var row = Row(explorer, "d:" + docs)!;
            explorer.SetExpanded(row, true);
            Assert.True(IsLoadingRow(explorer.Rows[explorer.Rows.IndexOf(row) + 1]));
        }, folders: 1);
        Assert.NotNull(Row(explorer, "f:" + Path.Combine(docs, "guide.md")));
        Assert.True(Find(explorer.Root!, "d:" + Path.Combine(docs, "deep")) is { IsUnlisted: true });
        Assert.DoesNotContain(explorer.Rows, IsLoadingRow);
        Assert.Equal(builds + 1, explorer.Builds);

        // node_modules is listed once it's expanded, dimmed with what is in it.
        await ListedAsync(explorer, () => explorer.SetExpanded(Row(explorer, "d:" + modules)!, true), folders: 1);
        Assert.True(Row(explorer, "d:" + Path.Combine(modules, "left-pad")) is { IsDimmed: true });
    }

    [AvaloniaFact]
    public async Task A_change_lists_only_the_listed_folders_it_names_and_the_tree_is_built_only_when_one_changed()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var docs = Path.Combine(folder, "docs");
        var src = Path.Combine(folder, "src");

        // docs isn't listed: its change lists nothing (it is once it's expanded).
        var builds = explorer.Builds;
        await ListedAsync(explorer, () => explorer.OnFilesChanged([docs]), folders: 0);
        await ExpandAsync(explorer, docs);
        builds = explorer.Builds;

        // Listed: docs alone is listed again (src isn't listed); nothing changed on disk, so the tree isn't built again.
        await ListedAsync(explorer, () => explorer.OnFilesChanged([docs + Path.DirectorySeparatorChar]), folders: 1);
        await ListedAsync(explorer, () => explorer.OnFilesChanged([docs, src]), folders: 1);
        Assert.Equal(builds, explorer.Builds);

        // A new file: the build host's watcher reports docs, which is listed, and the tree built once. (The steps that count
        // listings change nothing on disk, or wait for the watcher's report: it lists too.)
        var added = Path.Combine(docs, "added.md");
        await File.WriteAllTextAsync(added, string.Empty, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + added) is not null, seconds: 60);
        await SettledAsync(explorer);
        Assert.Equal(builds + 1, explorer.Builds);

        // Collapsed, docs keeps its listing and isn't listed again; the watcher's overflow lists the folders shown (the
        // workspace folder's alone).
        explorer.SetExpanded(Row(explorer, "d:" + docs)!, false);
        await ListedAsync(explorer, () => explorer.OnFilesChanged([docs]), folders: 0);
        await ListedAsync(explorer, () => explorer.OnFilesChanged([folder], rescan: true), folders: 1);

        // The file deleted while docs is collapsed: the watcher's report lists the workspace folder (a file added there,
        // reported after the deletion), not docs.
        var fence = Path.Combine(folder, "fence.txt");
        var listings = explorer.FolderListings;
        File.Delete(added);
        await File.WriteAllTextAsync(fence, string.Empty, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + fence) is not null, seconds: 60);
        await SettledAsync(explorer);
        Assert.Equal(listings + 1, explorer.FolderListings);

        // Expanded again, its last listing shows at once, deleted file and all, while a new listing refreshes it.
        await ListedAsync(explorer, () =>
        {
            explorer.SetExpanded(Row(explorer, "d:" + docs)!, true);
            Assert.NotNull(Row(explorer, "f:" + added));
            Assert.DoesNotContain(explorer.Rows, IsLoadingRow);
        }, folders: 1);
        Assert.Null(Row(explorer, "f:" + added));

        // A listed folder deleted on disk goes from its parent's listing, and its own listing with it: a change of it lists
        // nothing, and it's listed again when it comes back (still expanded).
        var deep = Path.Combine(docs, "deep");
        await ExpandAsync(explorer, deep);
        Directory.Delete(deep, recursive: true);
        await ShellFixture.PumpUntil(() => Row(explorer, "d:" + deep) is null, seconds: 60);
        await SettledAsync(explorer);
        await ListedAsync(explorer, () => explorer.OnFilesChanged([deep]), folders: 0);
        Write(docs, "deep/back.md");
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + Path.Combine(deep, "back.md")) is not null, seconds: 60);
    }

    [AvaloniaFact]
    public async Task The_expansion_restored_with_the_folder_is_listed_in_one_batch()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var models = Path.Combine(folder, "src", "App", "Models");
        foreach (var expand in new[] { Path.Combine(folder, "src"), Path.Combine(folder, "src", "App"), models })
        {
            await ExpandAsync(explorer, expand);
        }

        f.Shell.SaveEditorSession();
        await f.Shell.SolutionState.FlushAsync();
        await f.Shell.CloseSolutionAsync();

        // The workspace folder, src, App and Models, in the open's first batch: one listing each, one batch, one build of
        // the tree with them. Each expanded folder waited for its parent's listing to show it, a batch each.
        var batches = 0;
        explorer.DiskLoaded += (_, _) => batches++;
        var listings = explorer.FolderListings;
        await f.Shell.OpenFolderWorkspaceAsync(folder);
        await ShellFixture.PumpUntil(() => explorer.IsFolderView && Row(explorer, "f:" + Path.Combine(models, "Person.cs")) is not null, seconds: 60);
        await SettledAsync(explorer);
        Assert.Equal((listings + 4, 1), (explorer.FolderListings, batches));
        Assert.DoesNotContain(explorer.Rows, IsLoadingRow);
    }

    /// <summary>
    /// Queues a listing of the workspace folder and starts its batch: on its way until the test pumps. Unless the build host
    /// answered before the batch even awaited it: then it's waited for and started again. The listings taken before it.
    /// </summary>
    private static async Task<(Task Batch, int Listings)> BatchOnItsWayAsync(SolutionExplorerViewModel explorer, string folder)
    {
        for (var tries = 0; ; tries++)
        {
            var listings = explorer.FolderListings;
            explorer.OnFilesChanged([folder]);
            var batch = explorer.LoadDiskAsync();
            if (!batch.IsCompleted)
            {
                return (batch, listings);
            }

            Assert.True(tries < 20, "Every batch was answered before it awaited its listings.");
            await SettledAsync(explorer);
        }
    }

    [AvaloniaFact]
    public async Task Listings_queued_while_a_batch_is_on_its_way_go_with_the_next_one_and_a_batch_is_stale_once_the_view_switched()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var docs = Path.Combine(folder, "docs");
        await ExpandAsync(explorer, docs);

        // docs changes while the workspace folder's batch is on its way: no second batch beside it, docs goes with the next.
        var batches = 0;
        explorer.DiskLoaded += (_, _) => batches++;
        var (batch, listings) = await BatchOnItsWayAsync(explorer, folder);
        batches = 0;
        explorer.OnFilesChanged([docs]);
        Assert.True(explorer.LoadDiskAsync().IsCompleted);
        await ShellFixture.PumpUntil(() => batch.IsCompleted);
        await SettledAsync(explorer);
        Assert.Equal((listings + 2, 2), (explorer.FolderListings, batches));

        // The solution view shown while a batch's listings are back but not taken: they're stale, and taken by nobody. The
        // folder view's return lists the folders shown again.
        (batch, listings) = await BatchOnItsWayAsync(explorer, folder);
        Assert.True(SpinWait.SpinUntil(() => explorer.FolderListingsOnTheirWay!.IsCompleted, TimeSpan.FromSeconds(60)), "The build host didn't answer the listing.");
        explorer.SetFolderView(false);
        await ShellFixture.PumpUntil(() => batch.IsCompleted);
        await SettledAsync(explorer);
        Assert.Equal(listings, explorer.FolderListings);
        explorer.SetFolderView(true);
        await SettledAsync(explorer);
        Assert.Equal(listings + 2, explorer.FolderListings); // the workspace folder and docs
        Assert.NotNull(Row(explorer, "f:" + Path.Combine(docs, "guide.md")));
    }

    [AvaloniaFact]
    public async Task A_failed_listing_logs_one_line_for_its_batch_and_nothing_waits_for_it()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;

        // Two folders no file system takes, expanded: their listings fail on the build host, in the batch with the
        // workspace folder's.
        var (expanded, collapsed) = explorer.CaptureExpansion();
        explorer.LoadExpansion([.. expanded, "d:" + Path.Combine(folder, "bad\0one"), "d:" + Path.Combine(folder, "bad\0two")], collapsed);
        var log = f.Shell.Workbench.Log.GetText().Length;
        await ListedAsync(explorer, () => explorer.SetFolderView(true), folders: 3);
        var lines = f.Shell.Workbench.Log.GetText()[log..].Split('\n').Where(l => l.Contains("[explorer]", StringComparison.Ordinal)).ToList();
        Assert.True(lines is [var line] && line.Contains("Could not list 2 folder(s)", StringComparison.Ordinal), string.Join('\n', lines));
        Assert.DoesNotContain(explorer.Rows, IsLoadingRow);

        // Gone from the workspace folder's listing, they keep nothing: a change lists the folders shown, not them.
        await ListedAsync(explorer, () => explorer.OnFilesChanged([folder], rescan: true), folders: 1);
    }

    [AvaloniaFact]
    public async Task After_the_build_host_restarts_the_folders_shown_are_listed_again_and_a_failed_one_with_them()
    {
        // The build host reopens the same folder after its restart, whose change listed nothing: a file added while it was
        // down never showed in the expanded folder, and a folder whose listing failed meanwhile stayed empty, with no chevron
        // to expand it again by (M57).
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var docs = Path.Combine(folder, "docs");
        var src = Path.Combine(folder, "src");
        var added = Path.Combine(docs, "while-down.md");
        await ExpandAsync(explorer, docs);

        // Held down: the supervisor restarts it once its crash's handlers have run. The file comes then, with no watcher.
        using var crashed = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var restarted = false;
        f.Shell.Solutions.HostCrashed += (_, _) =>
        {
            File.WriteAllText(added, string.Empty);
            crashed.Set();
            resume.Wait(TimeSpan.FromSeconds(60));
        };
        f.Shell.Solutions.HostRestarted += (_, _) => Volatile.Write(ref restarted, true);
        using (var host = System.Diagnostics.Process.GetProcessById(f.Shell.Solutions.HostProcessId!.Value))
        {
            host.Kill();
        }

        await ShellFixture.PumpUntil(() => crashed.IsSet, seconds: 60);
        await ListedAsync(explorer, () => explorer.SetExpanded(Row(explorer, "d:" + src)!, true), folders: 1);
        Assert.True(Find(explorer.Root!, "d:" + src) is { IsUnlisted: false, Children.Count: 0 }); // failed: empty, nothing waits
        Assert.DoesNotContain(explorer.Rows, IsLoadingRow);

        resume.Set();
        await ShellFixture.PumpUntil(() => Volatile.Read(ref restarted), seconds: 120);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + added) is not null && Row(explorer, "d:" + Path.Combine(src, "App")) is not null, seconds: 60,
            describe: () => string.Join(", ", explorer.Rows.Select(r => r.Name)));
        await SettledAsync(explorer);
        Assert.DoesNotContain(explorer.Rows, IsLoadingRow);
    }

    [AvaloniaFact]
    public async Task A_build_lists_again_the_bin_and_obj_folders_listed_which_the_watcher_leaves_out()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var app = Path.Combine(folder, "src", "App");
        var debug = Path.Combine(app, "bin", "Debug");
        foreach (var expand in new[] { Path.Combine(folder, "src"), app, Path.Combine(app, "bin"), debug })
        {
            await ExpandAsync(explorer, expand);
        }

        // bin and bin/Debug, not the folders the watcher reports (no project is loaded, so no project's folder either).
        var later = Path.Combine(debug, "Later.dll");
        await File.WriteAllTextAsync(later, string.Empty, TestContext.Current.CancellationToken);
        await ListedAsync(explorer, () => explorer.OnBuildFinished(null), folders: 2);
        Assert.True(Row(explorer, "f:" + later) is { IsDimmed: false });
    }

    [AvaloniaFact]
    public async Task Sync_lists_the_folders_down_to_the_file_and_says_when_it_isnt_in_the_folder()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var notes = Path.Combine(folder, "docs", "deep", "notes.md");
        var person = Path.Combine(folder, "src", "App", "Models", "Person.cs");
        var readme = Path.Combine(folder, "README.md");
        await f.Shell.OpenFileAsync(readme);
        await f.Shell.OpenFileAsync(person);
        await f.Shell.OpenFileAsync(notes);
        await SettledAsync(explorer);
        f.Shell.StatusBar.ClearMessage();

        // docs and docs/deep, in one batch; then the file is selected, with no message.
        await ListedAsync(explorer, () =>
        {
            explorer.SyncWithActiveDocument();
            Assert.Null(f.Shell.StatusBar.Message);
        }, folders: 2);
        Assert.Equal(notes, explorer.Selected?.Node.Path);
        Assert.Null(f.Shell.StatusBar.Message);

        // Waiting for src, App and Models, it gives way to another document, and to Collapse All.
        f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == person));
        explorer.SyncWithActiveDocument();
        f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == readme));
        await SettledAsync(explorer);
        Assert.NotNull(Row(explorer, "f:" + person)); // the listings came
        Assert.Equal(notes, explorer.Selected?.Node.Path);
        Assert.Null(f.Shell.StatusBar.Message);
        explorer.CollapseAll();
        f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == person));
        explorer.SyncWithActiveDocument();
        explorer.CollapseAll();
        await SettledAsync(explorer);
        Assert.Null(explorer.Selected);
        Assert.Null(f.Shell.StatusBar.Message);

        // A file the listings never have (.git isn't listed): the folders down to it are listed, then the message, which
        // names the folder.
        var description = Path.Combine(folder, ".git", "description");
        await f.Shell.OpenFileAsync(description);
        explorer.SyncWithActiveDocument();
        Assert.Null(f.Shell.StatusBar.Message);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Message == NotInFolder, seconds: 60);
        await SettledAsync(explorer);

        // Outside the workspace folder: the message at once.
        var outside = Path.Combine(f.UserData, "outside.txt");
        await File.WriteAllTextAsync(outside, string.Empty, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(outside);
        f.Shell.StatusBar.ClearMessage();
        explorer.SyncWithActiveDocument();
        Assert.Equal(NotInFolder, f.Shell.StatusBar.Message);
    }

    [AvaloniaFact]
    public async Task Sync_in_folder_modes_solution_view_names_the_folder_or_the_loaded_projects_not_a_solution()
    {
        // Folder mode has no solution: its solution view said "isn't in the solution" (M57).
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        explorer.SetFolderView(false);
        await SettledAsync(explorer);

        await f.Shell.OpenFileAsync(Path.Combine(folder, "README.md"));
        f.Shell.StatusBar.ClearMessage();
        explorer.SyncWithActiveDocument();
        Assert.Equal("The active document isn't in a loaded project.", f.Shell.StatusBar.Message);

        var outside = Path.Combine(f.UserData, "outside.txt");
        await File.WriteAllTextAsync(outside, string.Empty, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(outside);
        f.Shell.StatusBar.ClearMessage();
        explorer.SyncWithActiveDocument();
        Assert.Equal(NotInFolder, f.Shell.StatusBar.Message);
    }

    [AvaloniaFact]
    public async Task A_sync_waits_for_the_tree_built_with_its_listings_while_another_folder_is_listed()
    {
        // Another listed folder (docs) changes while the listings Sync waits for are on their way: its batch started before
        // the tree was built with theirs, found the file in the old tree nowhere and no listing of its folders on its way,
        // and said it wasn't in the folder (the Everyday flow's flake, M57).
        const int Chains = 20;
        var (f, folder) = await OpenAsync(folder => Write(folder, [.. Enumerable.Range(0, Chains).Select(i => $"chains/c{i:00}/a/b/File.cs")]));
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var docs = Path.Combine(folder, "docs");
        await ExpandAsync(explorer, docs);

        // The chain's batch has to be on its way when docs changes. The build host can answer it before the batch even
        // awaited it (the UI thread held up on a loaded machine): then that chain's folders are listed, and the next chain's
        // file is synced instead. The test assumed it never did, and failed 1 run in 9 beside another class.
        for (var i = 0; ; i++)
        {
            Assert.True(i < Chains, "Every chain's batch was answered before it awaited its listings.");
            var file = Path.Combine(folder, "chains", $"c{i:00}", "a", "b", "File.cs");
            await f.Shell.OpenFileAsync(file);
            await SettledAsync(explorer);
            f.Shell.StatusBar.ClearMessage();

            explorer.SyncWithActiveDocument();
            var chain = explorer.LoadDiskAsync(); // chains, c<i>, a and b, on their way
            if (chain.IsCompleted)
            {
                await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == file || f.Shell.StatusBar.Message is not null, seconds: 60);
                continue;
            }

            explorer.OnFilesChanged([docs]);
            await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == file || f.Shell.StatusBar.Message is not null, seconds: 60);
            Assert.Null(f.Shell.StatusBar.Message);
            Assert.Equal(file, explorer.Selected?.Node.Path);
            return;
        }
    }

    [AvaloniaFact]
    public async Task Track_active_item_selects_a_file_in_a_folder_not_listed_once_its_listed()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var person = Path.Combine(folder, "src", "App", "Models", "Person.cs");
        explorer.TrackActiveItem = true;
        await ShellFixture.PumpUntil(() => explorer.TrackActiveItem && explorer.IsViewShown);

        await f.Shell.OpenFileAsync(person);
        await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == person, seconds: 60);
        await SettledAsync(explorer);
        Assert.Null(f.Shell.StatusBar.Message);
    }

    [AvaloniaFact]
    public async Task The_search_box_finds_files_in_folders_not_listed_and_sync_from_it_lists_them()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var notes = Path.Combine(folder, "docs", "deep", "notes.md");

        // One listing of the whole folder, off the UI thread: the file shows with its folders. Neither was listed.
        explorer.Filter = "notes";
        await ShellFixture.PumpUntil(() => !explorer.IsFiltering && Row(explorer, "f:" + notes) is not null, seconds: 60,
            describe: () => string.Join(", ", explorer.Rows.Select(r => r.Name)));
        Assert.Equal(["Folder 'ws'", "docs", "deep", "notes.md"], explorer.Rows.Select(r => r.Name));
        Assert.Equal(0, explorer.FolderListings - 1); // the folder view's own listings: the workspace folder's alone

        // Sync from it on the file: the box is cleared, and the folders down to the file are listed, then it's selected.
        await f.Shell.OpenFileAsync(notes);
        await ListedAsync(explorer, explorer.SyncWithActiveDocument, folders: 2);
        Assert.Equal(string.Empty, explorer.Filter);
        Assert.Equal(notes, explorer.Selected?.Node.Path);

        // Cleared, the tree is the folders listed again, at once.
        explorer.Filter = "guide";
        await ShellFixture.PumpUntil(() => !explorer.IsFiltering && Row(explorer, "f:" + Path.Combine(folder, "docs", "guide.md")) is not null, seconds: 60);
        explorer.Filter = string.Empty;
        Assert.False(explorer.IsFiltering);
        Assert.NotNull(Row(explorer, "d:" + Path.Combine(folder, "src")));
    }

    [AvaloniaFact]
    public async Task A_listing_cut_at_the_limit_ends_with_a_row_that_says_so()
    {
        var (f, folder) = await OpenAsync(folder =>
        {
            var big = Path.Combine(folder, "big");
            Directory.CreateDirectory(big);
            for (var i = 0; i < 20_010; i++)
            {
                File.WriteAllText(Path.Combine(big, $"F{i:00000}.txt"), string.Empty);
            }
        });
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var big = Path.Combine(folder, "big");
        var limit = 20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

        // The search box's listing of the whole folder is cut too, in big: big and the workspace folder say so, counting the
        // listing's entries (the workspace folder's row counted its own six), and node_modules, which the listing doesn't
        // go into, says it wasn't searched (it showed as an empty folder, as did the folders the listing stopped before).
        explorer.Filter = "node_modules";
        await ShellFixture.PumpUntil(() => !explorer.IsFiltering && Row(explorer, "d:" + Path.Combine(folder, "node_modules")) is not null, seconds: 60);
        var cutText = $"Only the first {limit} entries are shown. Find the others with Find in Files or the terminal.";
        Assert.Equal(
            [("Folder 'ws'", 0), ("big", 1), (cutText, 2), ("node_modules", 1), ("Not searched. Clear the search box and expand the folder to see what is in it.", 2), (cutText, 1)],
            explorer.Rows.Select(r => (r.Name, r.Depth)));
        Assert.True(explorer.Rows.Where(r => r.Node.Kind == ExplorerNodeKind.Message).All(r => r.IsDimmed));
        explorer.Filter = string.Empty;

        await ExpandAsync(explorer, big);
        var node = Find(explorer.Root!, "d:" + big)!;
        Assert.Equal(20_001, node.Children.Count);
        Assert.True(node.Children[^1] is { Kind: ExplorerNodeKind.Message } cut && cut.Id.EndsWith(SolutionTree.CutSuffix, StringComparison.Ordinal));
        Assert.True(Row(explorer, node.Children[^1].Id) is { IsDimmed: true } row
                    && row.Name.Contains(20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal));
    }
}
