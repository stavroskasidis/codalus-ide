using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Show All Files lists again only the projects a change concerns (M56): for a folder that changed, the projects whose
/// folders hold it; after a build, the project built and the projects it references. Every change listed every project.
/// A build also has the analyzers the build host marked missing looked for again.
/// </summary>
public class SolutionExplorerShowAllFilesTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "codalus-show-all-files");

    private static string P(params string[] segments) => Path.Combine([s_root, .. segments]);

    private static string[] Names(IEnumerable<string> projects) => [.. projects.Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_folder_that_changed_lists_the_projects_whose_folders_hold_it()
    {
        string[] projects = [P("src", "App", "App.csproj"), P("src", "App", "Plugins", "Plugins.csproj"), P("src", "Core", "Core.csproj"), P("src", "Core", "Core.Tests.csproj")];

        Assert.Equal(["App"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "App", "Models")])));
        Assert.Equal(["App", "Plugins"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "App", "Plugins", "Views") + Path.DirectorySeparatorChar])));
        Assert.Equal(["App", "Core", "Core.Tests"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "Core"), P("src", "App")])));
        Assert.Empty(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src"), P("docs", "images"), P("src", "Apps")]));
    }

    [Fact]
    public void A_build_lists_the_project_built_and_the_projects_it_references()
    {
        static ProjectInfo Project(string name, params string[] references) =>
            new(P(name, name + ".csproj"), name, ProjectStates.Loaded, null, ["net10.0"], "Library", [],
            [
                .. references.Select(r => new ReferenceInfo(ReferenceKinds.Project, r, null, P(r, r + ".csproj"), "net10.0", true)),
                new ReferenceInfo(ReferenceKinds.Package, "Some.Package", "1.0.0", null, "net10.0", true),
            ], [], Restored: true);
        var projects = new[] { Project("App", "Ui"), Project("Ui", "Core"), Project("Core", "Ui"), Project("Other") }.ToDictionary(p => p.Path, SolutionService.PathComparer);

        Assert.Equal(["App", "Core", "Ui"], Names(SolutionExplorerViewModel.WithReferences(projects, P("App", "App.csproj"))));
        Assert.Equal(["Core", "Ui"], Names(SolutionExplorerViewModel.WithReferences(projects, P("Core", "Core.csproj")))); // a cycle ends
        Assert.Equal(["Other"], Names(SolutionExplorerViewModel.WithReferences(projects, P("Other", "Other.csproj"))));
    }

    // ------------------------------------------------------------------ in the shell, against the real build host

    /// <summary>
    /// The fixture's projects (App and Core.Tests reference Core) with what a build left in App's and Core's bin, opened
    /// without a restore and with Show All Files on, once every listing has come: the build host then has nothing more to
    /// evaluate or build, so every listing and build of the tree counted is the test's own.
    /// </summary>
    private static async Task<(ShellFixture F, string Folder)> OpenAsync(Action<string>? prepare = null, int projects = 3)
    {
        var f = ShellFixture.Create();
        try
        {
            var solution = f.CopyFixture();
            var folder = Path.GetDirectoryName(solution)!;
            foreach (var output in new[] { Path.Combine(folder, "src", "App", "bin", "Debug", "App.dll"), Path.Combine(folder, "src", "Core", "bin", "Debug", "Core.dll") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, string.Empty, TestContext.Current.CancellationToken);
            }

            prepare?.Invoke(folder);
            await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, SettingScope.User, TestContext.Current.CancellationToken);
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(solution);
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == projects && f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.DesignTimeProgress is { Completed: true }, seconds: 120);
            f.Shell.Explorer.ShowAllFiles = true;
            await SettledAsync(f.Shell.Explorer);
            Assert.Equal(projects, f.Shell.Explorer.Listings);
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

    [AvaloniaFact]
    public async Task A_change_lists_only_the_projects_it_concerns_and_the_tree_is_built_only_when_a_listing_changed()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        async Task ListedAsync(Action change, int projects)
        {
            var listings = explorer.Listings;
            change();
            await SettledAsync(explorer);
            Assert.Equal(listings + projects, explorer.Listings);
        }

        // A folder that changed: the projects whose folders hold it. Nothing changed on disk, so each listing keeps its
        // instance and the tree isn't built again.
        var builds = explorer.Builds;
        await ListedAsync(() => explorer.OnFilesChanged([Path.Combine(folder, "src", "Core")]), projects: 1);
        await ListedAsync(() => explorer.OnFilesChanged([Path.Combine(folder, "src", "App", "Forms"), Path.Combine(folder, "tests", "Core.Tests")]), projects: 2);

        // A build: the project built and those it references (App and Core), or every project.
        await ListedAsync(() => explorer.OnBuildFinished(app), projects: 2);
        await ListedAsync(() => explorer.OnBuildFinished(null), projects: 3);

        // The watcher dropped events: it reports the folders it watches, which hold none of these projects, and says that
        // anything below may have changed. Each project kept its listing, deleted files and all, until its folder changed.
        await ListedAsync(() => explorer.OnFilesChanged([folder]), projects: 0);
        await ListedAsync(() => explorer.OnFilesChanged([folder], rescan: true), projects: 3);
        Assert.Equal(builds, explorer.Builds);

        // bin shows at once, dimmed, with a "Loading…" row until it's expanded and listed: App alone is listed for it, and
        // the tree is built once, with Core's and Core.Tests' subtrees kept.
        var bin = Path.Combine(folder, "src", "App", "bin");
        explorer.Reveal("d:" + bin);
        var row = explorer.Selected!;
        Assert.True(row.IsDimmed && row.HasChildren);
        Assert.Empty(Find(explorer.Root!, "d:" + bin)!.Children);
        await ListedAsync(() =>
        {
            explorer.SetExpanded(row, true);
            Assert.Equal("d:" + bin + "|listing", explorer.Rows[explorer.Rows.IndexOf(row) + 1].Id);
        }, projects: 1);
        Assert.NotNull(Find(explorer.Root!, "d:" + Path.Combine(bin, "Debug")));
        Assert.Equal((builds + 1, 2), (explorer.Builds, explorer.ReusedProjects));

        // A build of App wrote in it: App and Core are listed, and the tree is built once, with Core's subtree kept.
        var later = Path.Combine(bin, "Later.dll");
        await File.WriteAllTextAsync(later, string.Empty, TestContext.Current.CancellationToken);
        await ListedAsync(() => explorer.OnBuildFinished(app), projects: 2);
        Assert.True(Find(explorer.Root!, "f:" + later) is { IsExcluded: true });
        Assert.Equal((builds + 2, 2), (explorer.Builds, explorer.ReusedProjects));
    }

    /// <summary>
    /// Turns Show All Files on and starts its batch: on its way until the test pumps, as nothing runs on the UI thread in
    /// between. Unless the build host answered every listing before the batch even awaited them (the UI thread preempted
    /// on a busy machine), and it was taken at once: then it's turned off and started again. The listings taken before it.
    /// </summary>
    private static async Task<(Task Batch, int Listings)> BatchOnItsWayAsync(SolutionExplorerViewModel explorer)
    {
        for (var tries = 0; ; tries++)
        {
            var listings = explorer.Listings;
            explorer.ShowAllFiles = true;
            var batch = explorer.LoadDiskAsync();
            if (!batch.IsCompleted)
            {
                return (batch, listings);
            }

            Assert.True(tries < 20, "Every batch was answered before it awaited its listings.");
            explorer.ShowAllFiles = false;
            await SettledAsync(explorer);
        }
    }

    /// <summary>
    /// Until the batch's listings are back from the build host, without running the UI thread, so the batch hasn't taken
    /// them yet: turned off then, only the batch's check that Show All Files went off keeps them out. Turned off before
    /// they came back, they were cancelled, and the batch had nothing to keep out.
    /// </summary>
    private static void ListingsCame(SolutionExplorerViewModel explorer) =>
        Assert.True(SpinWait.SpinUntil(() => explorer.ListingsOnTheirWay!.IsCompleted, TimeSpan.FromSeconds(60)), "The build host didn't answer the listings.");

    [AvaloniaFact]
    public async Task A_listing_that_comes_after_show_all_files_went_off_takes_nothing()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var bin = Path.Combine(folder, "src", "App", "bin");
        explorer.ShowAllFiles = false;
        await SettledAsync(explorer);

        // On, its batch's listings back, and off before the batch took them.
        var (batch, listings) = await BatchOnItsWayAsync(explorer);
        ListingsCame(explorer);
        explorer.ShowAllFiles = false;
        var builds = explorer.Builds;
        await ShellFixture.PumpUntil(() => batch.IsCompleted);
        await SettledAsync(explorer);
        Assert.Equal((listings, builds), (explorer.Listings, explorer.Builds));
        Assert.Null(Find(explorer.Root!, "d:" + bin));

        // On, off and on again while the first batch is on its way: only the last one's listings are taken. Without the
        // check, the first batch took its listings too, and cleared the mark of the batch after it.
        (batch, listings) = await BatchOnItsWayAsync(explorer);
        ListingsCame(explorer);
        explorer.ShowAllFiles = false;
        explorer.ShowAllFiles = true;
        await ShellFixture.PumpUntil(() => batch.IsCompleted);
        await SettledAsync(explorer);
        Assert.Equal(listings + 3, explorer.Listings);
        Assert.True(Find(explorer.Root!, "d:" + bin) is { IsExcluded: true });
    }

    [AvaloniaFact]
    public async Task Listings_queued_while_a_batch_is_on_its_way_go_with_the_next_one()
    {
        // Batches ran side by side, each up to four listings at a time on the build host, and each that changed a listing
        // built the tree at once: about one build of the tree for each project during a solution's open (M56).
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        explorer.ShowAllFiles = false;
        await SettledAsync(explorer);

        var (batch, listings) = await BatchOnItsWayAsync(explorer);
        var builds = explorer.Builds;
        explorer.OnFilesChanged([Path.Combine(folder, "src", "Core")]);
        Assert.True(explorer.LoadDiskAsync().IsCompleted); // no second batch beside it
        await ShellFixture.PumpUntil(() => batch.IsCompleted);
        await SettledAsync(explorer);
        Assert.Equal(listings + 3 + 1, explorer.Listings); // Core again, after
        Assert.Equal(builds + 1, explorer.Builds); // the first batch's change; the next one changed nothing
    }

    [AvaloniaFact]
    public async Task Package_and_ide_folders_show_dimmed_and_are_listed_once_theyre_expanded()
    {
        // The build host left node_modules, .vs and .idea out of every listing, so Show All Files never showed them (M57).
        // App's node_modules isn't in the project (as the Web SDK leaves it out), nor is .vs (a dot folder).
        var (f, folder) = await OpenAsync(folder =>
        {
            foreach (var file in new[] { Path.Combine(folder, "src", "App", "node_modules", "left-pad", "index.js"), Path.Combine(folder, "src", "App", ".vs", "App", "v18", ".suo") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, string.Empty);
            }

            var app = Path.Combine(folder, "src", "App", "App.csproj");
            File.WriteAllText(app, File.ReadAllText(app).Replace("<Folder Include=\"Empty\\\" />", "<Folder Include=\"Empty\\\" />\n    <None Remove=\"node_modules\\**\" />", StringComparison.Ordinal));
        });
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        foreach (var name in new[] { "node_modules", ".vs" })
        {
            var skipped = Path.Combine(folder, "src", "App", name);
            explorer.Reveal("d:" + skipped);
            var row = explorer.Selected!;
            Assert.True(row is { IsDimmed: true, HasChildren: true } && row.Id == "d:" + skipped);
            Assert.Empty(Find(explorer.Root!, "d:" + skipped)!.Children);
            var listings = explorer.Listings;
            explorer.SetExpanded(row, true);
            await SettledAsync(explorer);
            Assert.Equal(listings + 1, explorer.Listings);
            var inside = Find(explorer.Root!, "d:" + skipped)!.Children.Single();
            Assert.True(inside is { IsExcluded: true, Kind: ExplorerNodeKind.Folder }, inside.Id);
        }
    }

    [AvaloniaFact]
    public async Task A_node_modules_that_holds_items_is_gone_into_only_once_its_expanded()
    {
        // A plain SDK project's default globs take all of node_modules as items, so it was in the folders each listing went
        // into: every listing walked its thousands of files, up to the limit, after which the project's other files went
        // missing (M57). .bin, a dot folder, isn't an item: it shows only once node_modules is listed.
        var (f, folder) = await OpenAsync(folder =>
        {
            foreach (var file in new[] { Path.Combine(folder, "src", "App", "node_modules", "left-pad", "index.js"), Path.Combine(folder, "src", "App", "node_modules", ".bin", "left-pad") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, string.Empty);
            }
        });
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var modules = Path.Combine(folder, "src", "App", "node_modules");
        var bin = Path.Combine(modules, ".bin");

        Assert.True(Find(explorer.Root!, "f:" + Path.Combine(modules, "left-pad", "index.js")) is { IsExcluded: false }); // an item
        Assert.Null(Find(explorer.Root!, "d:" + bin));

        explorer.Reveal("d:" + modules);
        var row = explorer.Selected!;
        Assert.True(row is { IsExcluded: false, IsDimmed: true, HasChildren: true } && row.Id == "d:" + modules); // dimmed, as in the folder view
        var listings = explorer.Listings;
        explorer.SetExpanded(row, true);
        await SettledAsync(explorer);
        Assert.Equal(listings + 1, explorer.Listings);
        Assert.True(Find(explorer.Root!, "d:" + bin) is { IsExcluded: true, Kind: ExplorerNodeKind.Folder });
        Assert.True(explorer.Rows.Single(r => r.Id == "d:" + Path.Combine(modules, "left-pad")) is { IsExcluded: false, IsDimmed: true });
        Assert.DoesNotContain(explorer.Rows, r => r.Id.EndsWith("|listing", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Expanding_a_folder_that_two_projects_share_lists_both()
    {
        // Core's folder gets a second project: both have its bin, under one id, and it expands in both.
        var (f, folder) = await OpenAsync(folder =>
        {
            File.WriteAllText(Path.Combine(folder, "src", "Core", "Core.Twin.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            var slnx = Path.Combine(folder, "ProjectSystem.slnx");
            File.WriteAllText(slnx, File.ReadAllText(slnx).Replace("<Project Path=\"src/Core/Core.csproj\" />", "<Project Path=\"src/Core/Core.csproj\" />\n    <Project Path=\"src/Core/Core.Twin.csproj\" />", StringComparison.Ordinal));
        }, projects: 4);
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var core = Path.Combine(folder, "src", "Core", "Core.csproj");
        var twin = Path.Combine(folder, "src", "Core", "Core.Twin.csproj");
        var bin = Path.Combine(folder, "src", "Core", "bin");
        foreach (var project in new[] { core, twin })
        {
            explorer.Reveal("p:" + project);
            explorer.SetExpanded(explorer.Selected!, true);
        }

        var listings = explorer.Listings;
        explorer.Reveal("d:" + bin);
        explorer.SetExpanded(explorer.Selected!, true);
        await SettledAsync(explorer);
        Assert.Equal(listings + 2, explorer.Listings);
        Assert.All(new[] { core, twin }, project => Assert.NotNull(Find(Find(explorer.Root!, "p:" + project)!, "d:" + Path.Combine(bin, "Debug"))));
        Assert.Equal(2, explorer.Rows.Count(r => r.Id == "d:" + Path.Combine(bin, "Debug")));
        Assert.DoesNotContain(explorer.Rows, r => r.Id.EndsWith("|listing", StringComparison.Ordinal)); // no "Loading…" left in either
    }

    [AvaloniaFact]
    public async Task A_shared_folder_expanded_from_its_listed_copy_lists_the_other_projects_copy()
    {
        // Models is an item folder of Core (listed) and outside Core.Twin, which has no items (unlisted): expanding Core's
        // copy expands Twin's too, one id for both, and Twin's showed "Loading…" with no listing on its way.
        var (f, folder) = await OpenAsync(folder =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "src", "Core", "Models"));
            File.WriteAllText(Path.Combine(folder, "src", "Core", "Models", "Person.cs"), "namespace Core.Models;\n\npublic sealed class Person;\n");
            File.WriteAllText(Path.Combine(folder, "src", "Core", "Core.Twin.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <EnableDefaultItems>false</EnableDefaultItems>\n  </PropertyGroup>\n</Project>\n");
            var slnx = Path.Combine(folder, "ProjectSystem.slnx");
            File.WriteAllText(slnx, File.ReadAllText(slnx).Replace("<Project Path=\"src/Core/Core.csproj\" />", "<Project Path=\"src/Core/Core.csproj\" />\n    <Project Path=\"src/Core/Core.Twin.csproj\" />", StringComparison.Ordinal));
        }, projects: 4);
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var core = Path.Combine(folder, "src", "Core", "Core.csproj");
        var twin = Path.Combine(folder, "src", "Core", "Core.Twin.csproj");
        var models = Path.Combine(folder, "src", "Core", "Models");
        foreach (var project in new[] { core, twin })
        {
            explorer.Reveal("p:" + project);
            explorer.SetExpanded(explorer.Selected!, true);
        }

        var row = explorer.Rows.First(r => r.Id == "d:" + models && r.Node.ProjectPath == core);
        Assert.False(row.IsDimmed); // Core's own folder, listed
        var listings = explorer.Listings;
        explorer.SetExpanded(row, true);
        explorer.Refresh(); // the next build of the tree (an evaluation's, a design-time build's) shows Twin's copy expanded
        await SettledAsync(explorer);
        Assert.Equal(listings + 1, explorer.Listings); // Twin's
        Assert.NotNull(Find(Find(explorer.Root!, "p:" + twin)!, "f:" + Path.Combine(models, "Person.cs")));
        Assert.DoesNotContain(explorer.Rows, r => r.Id.EndsWith("|listing", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_sync_waiting_for_its_listing_gives_way_once_another_document_is_active()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        var ignored = Path.Combine(folder, "src", "App", "Excluded", "Ignored.cs");
        await f.Shell.OpenFileAsync(program);
        await f.Shell.OpenFileAsync(ignored);
        await SettledAsync(explorer);
        explorer.Reveal("f:" + program);
        f.Shell.StatusBar.ClearMessage();

        // Sync on a file in a folder not listed yet waits for the listing; Program.cs is active before it comes. It was
        // selected when the listing came, the user's row and the active document's left behind.
        explorer.SyncWithActiveDocument();
        f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == program));
        Assert.Equal(program, f.Shell.ActiveEditor?.Model.Path);
        await SettledAsync(explorer);
        Assert.NotNull(Find(explorer.Root!, "f:" + ignored)); // the listing came
        Assert.Equal(program, explorer.Selected?.Node.Path);
        Assert.Null(f.Shell.StatusBar.Message);
    }

    [AvaloniaFact]
    public async Task Sync_waits_for_the_listing_on_its_way_and_says_so_when_the_file_doesnt_come()
    {
        var (f, folder) = await OpenAsync(folder =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "src", "App", ".git"));
            File.WriteAllText(Path.Combine(folder, "src", "App", ".git", "description"), "Unnamed repository.\n");
        });
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var ignored = Path.Combine(folder, "src", "App", "Excluded", "Ignored.cs");
        await f.Shell.OpenFileAsync(ignored);
        await SettledAsync(explorer);
        const string NotInSolution = "The active document isn't in the solution.";

        // Its folder expanded, the listing that brings it queued: Sync waits for it, then selects the file. It said the
        // file wasn't in the solution, just before it came.
        explorer.Reveal("d:" + Path.GetDirectoryName(ignored));
        explorer.SetExpanded(explorer.Selected!, true);
        f.Shell.StatusBar.ClearMessage();
        explorer.SyncWithActiveDocument();
        Assert.Null(f.Shell.StatusBar.Message);
        await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == ignored, seconds: 60);
        Assert.Null(f.Shell.StatusBar.Message);

        // A file no listing has (the build host leaves .git out, and it isn't an item): the folders down to it are listed,
        // then the message, and nothing waits for it. It waited for good, with no message.
        var description = Path.Combine(folder, "src", "App", ".git", "description");
        await f.Shell.OpenFileAsync(description);
        var listings = explorer.Listings;
        explorer.SyncWithActiveDocument();
        Assert.Null(f.Shell.StatusBar.Message);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Message == NotInSolution, seconds: 60);
        Assert.Equal(listings + 1, explorer.Listings);
        Assert.Equal(ignored, explorer.Selected?.Node.Path);
    }

    [AvaloniaFact]
    public async Task A_listing_that_fails_leaves_no_loading_row_and_logs_one_line_for_its_batch()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var app = Path.Combine(folder, "src", "App");
        var core = Path.Combine(folder, "src", "Core");

        // A folder no file system takes, expanded under App and Core: their listings fail on the build host.
        var (expanded, collapsed) = explorer.CaptureExpansion();
        explorer.LoadExpansion([.. expanded, "d:" + Path.Combine(app, "bad\0name"), "d:" + Path.Combine(core, "bad\0name")], collapsed);
        var log = f.Shell.Workbench.Log.GetText().Length;
        foreach (var bin in new[] { Path.Combine(app, "bin"), Path.Combine(core, "bin") })
        {
            explorer.Reveal("d:" + bin);
            explorer.SetExpanded(explorer.Selected!, true);
        }

        Assert.Equal(2, explorer.Rows.Count(r => r.Id.EndsWith("|listing", StringComparison.Ordinal))); // "Loading…"
        await SettledAsync(explorer);
        Assert.DoesNotContain(explorer.Rows, r => r.Id.EndsWith("|listing", StringComparison.Ordinal));
        var lines = f.Shell.Workbench.Log.GetText()[log..].Split('\n').Where(l => l.Contains("[explorer]", StringComparison.Ordinal)).ToList();
        Assert.True(lines is [var line] && line.Contains("of 2 project(s)", StringComparison.Ordinal), string.Join('\n', lines));
    }

    [AvaloniaFact]
    public async Task A_source_generator_built_from_the_solution_loses_its_warning_and_a_clean_brings_it_back()
    {
        // On a fresh clone, a generator the solution's build makes isn't there yet: the build host marks it missing. A build
        // doesn't run design-time builds again, so it kept its warning until the project's next one. (One project, restored
        // in a session of its own: a restore's late file events, or another project's evaluation, can build App's again at
        // any time, and the build host then marks the new inputs itself.)
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "generator");
        var app = Path.Combine(folder, "App", "App.csproj");
        var generator = Path.Combine(folder, "Gen", "bin", "Debug", "net10.0", "Gen.dll");
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(folder, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Codalus.Tests.FixtureSdk.Pin(folder);
        File.WriteAllText(app, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <Analyzer Include=\"..\\Gen\\bin\\Debug\\net10.0\\Gen.dll\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(folder, "App", "Class1.cs"), "namespace App;\n\npublic static class Class1;\n");
        File.WriteAllText(Path.Combine(folder, "Generator.slnx"), "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        f.Shell.Trust.Trust(folder);
        bool Built() => f.Shell.Solutions.CompilerInputs.TryGetValue(app, out var inputs) && inputs.State == CompilerInputStates.Ok && f.Shell.Solutions.DesignTimeProgress is { Completed: true };
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Generator.slnx"));
        await ShellFixture.PumpUntil(() => Built() && f.Shell.Solutions.RestoreState == "succeeded", seconds: 180);
        await f.Shell.CloseSolutionAsync();
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, SettingScope.User, TestContext.Current.CancellationToken);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Generator.slnx"));
        await ShellFixture.PumpUntil(Built, seconds: 180);

        // The Analyzer item's path is relative, the project folder's.
        Assert.Equal([generator], (f.Shell.Solutions.CompilerInputs[app].MissingAnalyzers ?? []).Select(a => Path.GetFullPath(a, Path.GetDirectoryName(app)!)));
        var explorer = f.Shell.Explorer;
        ExplorerNode? Analyzer() => explorer.Root is { } root && Find(root, "p:" + app) is { } project
            ? project.Children.SingleOrDefault(n => n.Kind == ExplorerNodeKind.Dependencies)?.Children.SingleOrDefault(n => n.Name == "Analyzers")?.Children.SingleOrDefault(n => n.Name == "Gen")
            : null;
        await ShellFixture.PumpUntil(() => Analyzer() is { IsWarning: true }, seconds: 60);

        // The build writes the generator, then finishes (what the shell does at the build service's Finished): the build host
        // sends nothing new, the explorer looks again.
        var inputs = f.Shell.Solutions.CompilerInputs[app];
        Directory.CreateDirectory(Path.GetDirectoryName(generator)!);
        File.Copy(typeof(SolutionExplorerShowAllFilesTests).Assembly.Location, generator);
        explorer.OnBuildFinished(app);
        await ShellFixture.PumpUntil(() => Analyzer() is { IsWarning: false }, seconds: 60);
        Assert.Same(inputs, f.Shell.Solutions.CompilerInputs[app]);

        // A Clean deletes it, then finishes: it's missing again. The look started from the last one's result, which had
        // nothing missing, so it looked for nothing and showed the generator as there.
        File.Delete(generator);
        explorer.OnBuildFinished(app);
        await ShellFixture.PumpUntil(() => Analyzer() is { IsWarning: true }, seconds: 60);
        Assert.Same(inputs, f.Shell.Solutions.CompilerInputs[app]);

        // Built again, then App's next design-time build: the build host's own look finds it too, and the warning stays
        // away.
        File.Copy(typeof(SolutionExplorerShowAllFilesTests).Assembly.Location, generator);
        await f.Shell.Solutions.DesignTimeBuildAsync(app, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !ReferenceEquals(inputs, f.Shell.Solutions.CompilerInputs[app]) && Built(), seconds: 120);
        Assert.Null(f.Shell.Solutions.CompilerInputs[app].MissingAnalyzers);
        await ShellFixture.PumpUntil(() => Analyzer() is { IsWarning: false }, seconds: 60);
    }
}
