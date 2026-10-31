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
        var root = SolutionTree.FolderView(solution, entries);
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
