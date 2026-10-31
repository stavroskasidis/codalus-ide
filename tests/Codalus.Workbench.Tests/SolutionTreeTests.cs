using System.Diagnostics;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The Solution Explorer's tree is built on the UI thread (<see cref="SolutionTree"/>), so a build does work in proportion
/// to the nodes and never waits on the disk (M56): nesting a big folder, the folder view's projects, and analyzers.
/// </summary>
public sealed class SolutionTreeTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "codalus-solution-tree");

    private static string P(params string[] segments) => Path.Combine([s_root, .. segments]);

    [Fact]
    public void A_folder_of_twenty_thousand_files_nests_in_one_pass()
    {
        // 2,000 forms with their designer files and resources, and 14,000 other files, in one folder. Each file looked for
        // its nested files among all its siblings: 400 million lookups, tens of seconds.
        var project = P("Big", "Big.csproj");
        var names = Enumerable.Range(0, 2000).SelectMany(i => new[] { $"Form{i:0000}.cs", $"Form{i:0000}.Designer.cs", $"Form{i:0000}.resx" })
            .Concat(Enumerable.Range(0, 14000).Select(i => $"F{i:00000}.cs"))
            .ToList();
        var info = new ProjectInfo(project, "Big", ProjectStates.Loaded, null, ["net10.0"], "Library",
            [.. names.Select(n => new ProjectItemInfo(n.EndsWith(".resx", StringComparison.Ordinal) ? "EmbeddedResource" : "Compile", P("Big", "Generated", n), null, null))], [], [], Restored: true);

        var time = Stopwatch.StartNew();
        var generated = Assert.Single(SolutionTree.Contents(project, info));
        time.Stop();

        Assert.Equal(16000, generated.Children.Count);
        var forms = generated.Children.Where(n => n.Name.StartsWith("Form", StringComparison.Ordinal)).ToList();
        Assert.Equal(2000, forms.Count);
        Assert.All(forms, form => Assert.Equal([form.Name[..^3] + ".Designer.cs", form.Name[..^3] + ".resx"], form.Children.Select(n => n.Name)));
        Assert.All(generated.Children.Except(forms), file => Assert.Empty(file.Children));
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(3), $"nesting 20,000 files took {time.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_folder_view_finds_each_nodes_project_in_one_pass()
    {
        // 450 projects, nested ones among them, and 20,000 entries. Each node looked for its project among all of them.
        var projects = new List<SolutionProjectInfo>();
        var entries = new List<DirectoryEntry>();
        void Folder(params string[] segments) => entries.Add(new DirectoryEntry(P(segments), true));
        void File(params string[] segments) => entries.Add(new DirectoryEntry(P(segments), false));
        void Project(params string[] segments)
        {
            File(segments);
            projects.Add(new SolutionProjectInfo(P(segments), Path.GetFileNameWithoutExtension(segments[^1]), null, null));
        }

        Folder("ws", "src");
        for (var g = 0; g < 150; g++)
        {
            var group = $"g{g:000}";
            Folder("ws", "src", group);
            Folder("ws", "src", group, "a");
            Folder("ws", "src", group, "a", "Inner");

            // Two projects in one folder (paths of the same length: the first wins), and one inside it with a shorter path
            // (the longer path wins, as it always has).
            Project("ws", "src", group, $"OneWithALongName{g:000}.csproj");
            Project("ws", "src", group, $"TwoWithALongName{g:000}.csproj");
            Project("ws", "src", group, "a", "Inner", "I.csproj");
            for (var f = 0; f < 40; f++)
            {
                File("ws", "src", group, $"F{f:00}.cs");
                File("ws", "src", group, "a", $"A{f:00}.cs");
                File("ws", "src", group, "a", "Inner", $"N{f:00}.cs");
            }
        }

        Folder("ws", "docs");
        for (var f = entries.Count; f < 20_000; f++)
        {
            File("ws", "docs", $"Page{f:00000}.md");
        }

        var solution = new SolutionInfo(P("ws"), SolutionFormats.Folder, [], [.. projects], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var time = Stopwatch.StartNew();
        var root = SolutionTree.FolderView(solution, FolderListing.FromTree(P("ws"), entries, truncated: false));
        time.Stop();

        // Every node has the project it had (a sample, checked the old way): of the projects whose folder holds it, the one
        // with the longest path.
        static bool IsUnder(string path, string directory) =>
            path.StartsWith(directory + Path.DirectorySeparatorChar, SolutionService.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        string? Owner(string path) => solution.Projects.Select(p => p.Path)
            .Where(p => IsUnder(path, Path.GetDirectoryName(p)!) || SolutionService.PathComparer.Equals(path, Path.GetDirectoryName(p)))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
        var loaded = projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer);
        var nodes = new List<ExplorerNode>();
        var stack = new Stack<ExplorerNode>(root.Children);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            nodes.Add(node);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        Assert.Equal(20_000, nodes.Count);
        Assert.All(nodes.Where((_, i) => i % 10 == 0), node => Assert.Equal(node.Kind == ExplorerNodeKind.File && loaded.Contains(node.Path!) ? node.Path : Owner(node.Path!), node.ProjectPath));
        Assert.Equal(P("ws", "src", "g007", "OneWithALongName007.csproj"), nodes.Single(n => n.Path == P("ws", "src", "g007", "F03.cs")).ProjectPath);
        Assert.Equal(P("ws", "src", "g007", "OneWithALongName007.csproj"), nodes.Single(n => n.Path == P("ws", "src", "g007", "a", "Inner", "N03.cs")).ProjectPath);
        Assert.Equal(P("ws", "src", "g007", "OneWithALongName007.csproj"), nodes.Single(n => n.Path == P("ws", "src", "g007", "a", "Inner")).ProjectPath);
        Assert.Equal(P("ws", "src", "g007", "a", "Inner", "I.csproj"), nodes.Single(n => n.Path == P("ws", "src", "g007", "a", "Inner", "I.csproj")).ProjectPath);
        Assert.Null(nodes.Single(n => n.Path == P("ws", "docs", "Page19999.md")).ProjectPath);
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(1), $"the folder view of 20,000 entries and 450 projects took {time.ElapsedMilliseconds} ms");
    }

    private static DirectoryEntry Dir(params string[] segments) => new(P(segments), true);

    private static DirectoryEntry Doc(params string[] segments) => new(P(segments), false);

    private static ExplorerNode Child(ExplorerNode node, string name) => node.Children.Single(n => n.Name == name);

    [Fact]
    public void The_folder_view_is_built_from_each_listed_folders_own_listing()
    {
        // Each expanded folder is listed alone (M57): the workspace folder, src and src/App here; docs and Models aren't
        // listed, and say so. A listed folder below a project knows it, as before, carried down the folders listed.
        var app = P("ws", "src", "App", "App.csproj");
        var solution = new SolutionInfo(P("ws"), SolutionFormats.Folder, [], [new SolutionProjectInfo(app, "App", null, null)], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var listings = new Dictionary<string, FolderListing>(SolutionService.PathComparer)
        {
            [P("ws")] = new([Dir("ws", "docs"), Dir("ws", "src"), Doc("ws", "README.md")], false),
            [P("ws", "src")] = new([Dir("ws", "src", "App")], false),
            [P("ws", "src", "App")] = new([Dir("ws", "src", "App", "Models"), Dir("ws", "src", "App", "Empty"), Doc("ws", "src", "App", "App.csproj"), Doc("ws", "src", "App", "Program.cs")], false),
            [P("ws", "src", "App", "Empty")] = new([], false),
        };

        var root = SolutionTree.FolderView(solution, listings);

        Assert.False(root.IsUnlisted);
        Assert.Equal(["docs", "src", "README.md"], root.Children.Select(n => n.Name));
        Assert.True(Child(root, "docs") is { IsUnlisted: true, Children.Count: 0, ProjectPath: null });
        var appFolder = Child(Child(root, "src"), "App");
        Assert.True(appFolder is { IsUnlisted: false, ProjectPath: var owner } && owner == app);
        Assert.Equal(["Empty", "Models", "App.csproj", "Program.cs"], appFolder.Children.Select(n => n.Name));
        Assert.True(Child(appFolder, "Models") is { IsUnlisted: true, ProjectPath: var models } && models == app);
        Assert.True(Child(appFolder, "Empty") is { IsUnlisted: false, Children.Count: 0 });
        Assert.Equal(app, Child(appFolder, "Program.cs").ProjectPath);
        Assert.All(new[] { root, appFolder }, n => Assert.DoesNotContain(n.Children, c => c.IsMuted));

        // Nothing listed yet: the workspace folder itself is unlisted.
        Assert.True(SolutionTree.FolderView(solution, new Dictionary<string, FolderListing>()) is { IsUnlisted: true, Children.Count: 0 });
    }

    [Fact]
    public void The_folder_view_dims_folders_listed_on_demand_and_says_where_a_listing_was_cut()
    {
        var solution = new SolutionInfo(P("ws"), SolutionFormats.Folder, [], [], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var listings = new Dictionary<string, FolderListing>(SolutionService.PathComparer)
        {
            [P("ws")] = new([Dir("ws", "node_modules") with { Skipped = true }, Dir("ws", ".vs") with { Skipped = true }, Dir("ws", "big"), Doc("ws", "package.json")], false),
            [P("ws", "node_modules")] = new([Dir("ws", "node_modules", "left-pad"), Doc("ws", "node_modules", ".package-lock.json")], false),
            [P("ws", "node_modules", "left-pad")] = new([Doc("ws", "node_modules", "left-pad", "index.js")], false),
            [P("ws", "big")] = new([.. Enumerable.Range(0, 3).Select(i => Doc("ws", "big", $"F{i}.txt"))], true),
        };

        var root = SolutionTree.FolderView(solution, listings);

        // node_modules and .vs are dimmed, with everything in them; the rest isn't.
        var modules = Child(root, "node_modules");
        Assert.True(modules.IsMuted && !modules.IsUnlisted);
        Assert.True(Child(modules, "left-pad") is { IsMuted: true } leftPad && Child(leftPad, "index.js").IsMuted);
        Assert.True(Child(modules, ".package-lock.json").IsMuted);
        Assert.True(Child(root, ".vs") is { IsMuted: true, IsUnlisted: true });
        Assert.False(Child(root, "big").IsMuted || Child(root, "package.json").IsMuted);

        // big's listing was cut at the build host's limit: a muted row at its end says so, its files before it.
        var big = Child(root, "big");
        Assert.Equal(["F0.txt", "F1.txt", "F2.txt"], big.Children.SkipLast(1).Select(n => n.Name));
        Assert.True(big.Children[^1] is { Kind: ExplorerNodeKind.Message, IsMuted: true } cut && cut.Id == "d:" + P("ws", "big") + SolutionTree.CutSuffix);
        Assert.Contains("3", big.Children[^1].Name, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Children, n => n.Kind == ExplorerNodeKind.Message);
    }

    [Fact]
    public void A_whole_listing_becomes_each_folders_listing_for_the_search_box()
    {
        // The search box lists the workspace folder in one go: every folder it went into has a listing, an empty one too;
        // node_modules, which it doesn't go into, has none.
        var entries = new[] { Dir("ws", "src"), Dir("ws", "src", "Empty"), Doc("ws", "src", "A.cs"), Dir("ws", "node_modules") with { Skipped = true }, Doc("ws", "README.md") };

        var listings = FolderListing.FromTree(P("ws") + Path.DirectorySeparatorChar, entries, truncated: false);

        Assert.Equal(new[] { P("ws"), P("ws", "src"), P("ws", "src", "Empty") }.Order(StringComparer.Ordinal), listings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([P("ws", "src"), P("ws", "node_modules"), P("ws", "README.md")], listings[P("ws")].Entries.Select(e => e.Path));
        Assert.Equal([P("ws", "src", "Empty"), P("ws", "src", "A.cs")], listings[P("ws", "src")].Entries.Select(e => e.Path));
        Assert.Empty(listings[P("ws", "src", "Empty")].Entries);
        Assert.DoesNotContain(listings.Values, l => l.Truncated);
        Assert.True(listings[P("ws")].HasFolder(P("ws", "src")) && !listings[P("ws")].HasFolder(P("ws", "README.md")));
    }

    [Fact]
    public void A_whole_listing_cut_at_the_limit_counts_its_entries_and_leaves_the_folders_it_didnt_reach_unlisted()
    {
        // The build host walked the workspace folder, then src, and stopped in src at its limit: other and src/Later, listed
        // in their parents, were never gone into. They showed as listed, empty folders, and the workspace folder's row
        // counted its own three entries, not the listing's six (M57).
        var entries = new[]
        {
            Dir("ws", "other"), Dir("ws", "src"), Doc("ws", "README.md"),
            Dir("ws", "src", "Later"), Doc("ws", "src", "A.cs"), Doc("ws", "src", "B.cs"),
        };
        var solution = new SolutionInfo(P("ws"), SolutionFormats.Folder, [], [], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);

        var listings = FolderListing.FromTree(P("ws"), entries, truncated: true);
        var root = SolutionTree.FolderView(solution, listings);

        Assert.Equal(new[] { P("ws"), P("ws", "src") }.Order(StringComparer.Ordinal), listings.Keys.Order(StringComparer.Ordinal));
        Assert.True(Child(root, "other") is { IsUnlisted: true, Children.Count: 0 });
        var src = Child(root, "src");
        Assert.True(Child(src, "Later") is { IsUnlisted: true });
        var six = 6.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        foreach (var cut in new[] { root.Children[^1], src.Children[^1] })
        {
            Assert.True(cut is { Kind: ExplorerNodeKind.Message, IsMuted: true }, cut.Id);
            Assert.Equal($"Only the first {six} entries are shown. Find the others with Find in Files or the terminal.", cut.Name);
        }
    }

    [Fact]
    public void Analyzers_are_missing_when_the_build_host_says_so_without_the_tree_looking_at_the_disk()
    {
        // The tree checked each analyzer with File.Exists on the UI thread (300 projects, 6,000 checks per build of the tree;
        // and on this machine's disk when the build host runs on another).
        var project = P("Gen", "Gen.csproj");
        var info = new ProjectInfo(project, "Gen", ProjectStates.Loaded, null, ["net10.0"], "Library", [], [new(ReferenceKinds.Framework, "Microsoft.NETCore.App", null, null, "net10.0", true)], [], Restored: true);
        var onDisk = typeof(SolutionTreeTests).Assembly.Location;
        var elsewhere = P("nowhere", "Elsewhere.dll");
        var inputs = new CompilerInputsInfo(project, "net10.0", "C#", CompilerInputStates.Ok, null, [$"/analyzer:{onDisk}", $"/analyzer:{elsewhere}"], [], null, null)
        {
            MissingAnalyzers = [onDisk],
        };

        var analyzers = SolutionTree.Project(new SolutionProjectInfo(project, "Gen", null, null), info, trusted: true, inputs)
            .Children.Single(n => n.Kind == ExplorerNodeKind.Dependencies).Children.Single(n => n.Name == "Analyzers");

        Assert.True(analyzers.IsWarning);
        Assert.Equal(
            new[] { (Path.GetFileNameWithoutExtension(elsewhere), false), (Path.GetFileNameWithoutExtension(onDisk), true) }.OrderBy(a => a.Item1, StringComparer.OrdinalIgnoreCase),
            analyzers.Children.Select(n => (n.Name, n.IsWarning)));
    }

    [Fact]
    public void Show_All_Files_dims_node_modules_vs_and_idea_with_what_is_in_them_even_when_they_hold_items()
    {
        // A plain SDK project's default globs take all of node_modules as items: in Show All Files it showed at full
        // strength, while the folder view dims it and Show All Files dims the same folder when it holds no items (M57).
        var project = P("Web", "Web.csproj");
        var info = new ProjectInfo(project, "Web", ProjectStates.Loaded, null, ["net10.0"], "Library",
        [
            new("Compile", P("Web", "Program.cs"), null, null),
            new("None", P("Web", "node_modules", "left-pad", "index.js"), null, null),
            new("None", P("Web", ".idea", "workspace.xml"), null, null),
            new("Compile", P("Web", "Models", "M.cs"), null, null),
        ], [], [], Restored: true);
        DirectoryEntry[] disk = [Dir("Web", "node_modules") with { Skipped = true }, Dir("Web", ".idea") with { Skipped = true }, Dir("Web", ".vs") with { Skipped = true }, Dir("Web", "Models")];

        var contents = SolutionTree.Contents(project, info, disk);
        var modules = contents.Single(n => n.Name == "node_modules");
        Assert.True(modules is { IsExcluded: false, IsMuted: true });
        Assert.True(Child(modules, "left-pad") is { IsMuted: true } leftPad && Child(leftPad, "index.js").IsMuted);
        Assert.True(contents.Single(n => n.Name == ".idea") is { IsExcluded: false, IsMuted: true } idea && Child(idea, "workspace.xml").IsMuted);
        Assert.True(contents.Single(n => n.Name == ".vs").IsExcluded);
        Assert.DoesNotContain(contents.Where(n => n.Name is "Models" or "Program.cs").SelectMany(n => n.Children.Prepend(n)), n => n.IsMuted);

        // Without Show All Files, the project's items show as they are.
        Assert.DoesNotContain(SolutionTree.Contents(project, info), n => n.IsMuted);
    }

    [Fact]
    public void A_projects_item_folders_are_the_folders_its_tree_has_for_items()
    {
        var project = P("Items", "Items.csproj");
        var info = new ProjectInfo(project, "Items", ProjectStates.Loaded, null, ["net10.0"], "Library",
        [
            new("Compile", P("Items", "Program.cs"), null, null),
            new("Compile", P("Items", "Models", "Deep", "M.cs"), null, null),
            new("Folder", P("Items", "Empty"), null, null),
            new("Compile", P("Shared", "Linked.cs"), Path.Combine("Links", "Linked.cs"), null),
            new("Compile", P("Shared", "Outside.cs"), null, null),
        ], [], [], Restored: true);

        Assert.Equal(
            new[] { P("Items"), P("Items", "Empty"), P("Items", "Links"), P("Items", "Models"), P("Items", "Models", "Deep") }.Order(StringComparer.Ordinal),
            SolutionTree.ItemFolders(project, info).Order(StringComparer.Ordinal));

        // The same folders as the tree's.
        var tree = new List<string>();
        void Walk(IEnumerable<ExplorerNode> nodes)
        {
            foreach (var node in nodes.Where(n => n.Kind == ExplorerNodeKind.Folder))
            {
                tree.Add(node.Path!);
                Walk(node.Children);
            }
        }

        Walk(SolutionTree.Contents(project, info));
        Assert.Equal(tree.Order(StringComparer.Ordinal), SolutionTree.ItemFolders(project, info).Where(f => f != P("Items")).Order(StringComparer.Ordinal));
    }
}
