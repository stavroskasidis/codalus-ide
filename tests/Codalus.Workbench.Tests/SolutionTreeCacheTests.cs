using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The Solution Explorer builds its tree again whenever a project evaluates, which a large solution's open does one
/// project at a time: the projects whose inputs didn't change keep their subtrees (<see cref="SolutionTreeCache"/>).
/// </summary>
public sealed class SolutionTreeCacheTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "codalus-tree-cache");

    private static string ProjectPath(int i) => Path.Combine(s_root, $"Lib{i}", $"Lib{i}.csproj");

    private static ProjectInfo Evaluated(int i, params string[] files) =>
        new(ProjectPath(i), $"Lib{i}", ProjectStates.Loaded, null, ["net10.0"], "Library",
            [.. files.Select(f => new ProjectItemInfo("Compile", Path.Combine(s_root, $"Lib{i}", f), null, null))], [], [], Restored: true);

    [Fact]
    public void A_build_reuses_the_subtrees_of_the_projects_that_didnt_change()
    {
        var solution = new SolutionInfo(Path.Combine(s_root, "All.slnx"), SolutionFormats.Slnx, [],
            [.. Enumerable.Range(0, 3).Select(i => new SolutionProjectInfo(ProjectPath(i), $"Lib{i}", null, null))], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var projects = Enumerable.Range(0, 3).ToDictionary(ProjectPath, i => Evaluated(i, "A.cs", "B.cs"));
        var cache = new SolutionTreeCache();

        var first = SolutionTree.Build(solution, projects, cache: cache);
        Assert.Equal(0, cache.Reused);
        Assert.Equal(first, SolutionTree.Build(solution, projects), new NodeComparer()); // the same tree as without a cache

        // Nothing changed: every subtree is the same instance.
        var second = SolutionTree.Build(solution, projects, cache: cache);
        Assert.Equal(3, cache.Reused);
        Assert.All(first.Children.Zip(second.Children), pair => Assert.Same(pair.First, pair.Second));

        // One project evaluated again (a file added): only its subtree is built again, and shows the change.
        projects[ProjectPath(1)] = Evaluated(1, "A.cs", "B.cs", "C.cs");
        var third = SolutionTree.Build(solution, projects, cache: cache);
        Assert.Equal(2, cache.Reused);
        Assert.Same(second.Children[0], third.Children[0]);
        Assert.Equal(["A.cs", "B.cs", "C.cs"], third.Children[1].Children.Where(n => n.Kind == ExplorerNodeKind.File).Select(n => n.Name));

        // The solution lost a project: it isn't kept, and the project coming back is built again.
        var smaller = solution with { Projects = solution.Projects[..2] };
        Assert.Equal(["Lib0", "Lib1"], SolutionTree.Build(smaller, projects, cache: cache).Children.Select(n => n.Name));
        Assert.Equal(2, cache.Reused);
        Assert.Equal(["Lib0", "Lib1", "Lib2"], SolutionTree.Build(solution, projects, cache: cache).Children.Select(n => n.Name));
        Assert.Equal(2, cache.Reused);

        // Trust changes every project's subtree.
        var untrusted = solution with { Trusted = false };
        SolutionTree.Build(untrusted, projects, cache: cache);
        Assert.Equal(0, cache.Reused);
    }

    [Fact]
    public void A_change_of_the_solution_file_reuses_the_subtrees_of_the_projects_it_left_as_they_were()
    {
        // The build host sends the whole solution again for each change of the solution file (a project or solution folder
        // added, a git pull): every entry is a new instance, and each built every project's subtree again.
        var solution = new SolutionInfo(Path.Combine(s_root, "All.slnx"), SolutionFormats.Slnx, [],
            [.. Enumerable.Range(0, 3).Select(i => new SolutionProjectInfo(ProjectPath(i), $"Lib{i}", null, null))], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var projects = Enumerable.Range(0, 4).ToDictionary(ProjectPath, i => Evaluated(i, "A.cs"));
        var cache = new SolutionTreeCache();
        var first = SolutionTree.Build(solution, projects, cache: cache);

        var added = solution with
        {
            Folders = [new SolutionFolderInfo("/tests/", "tests", null, [])],
            Projects = [.. solution.Projects.Select(p => p with { }), new SolutionProjectInfo(ProjectPath(3), "Lib3", "/tests/", null)],
        };
        var second = SolutionTree.Build(added, projects, cache: cache);
        Assert.Equal(3, cache.Reused);
        Assert.All(first.Children, project => Assert.Contains(project, second.Children));

        // A renamed project is built again.
        SolutionTree.Build(added with { Projects = [added.Projects[0] with { Name = "Renamed" }, .. added.Projects[1..]] }, projects, cache: cache);
        Assert.Equal(3, cache.Reused);
    }

    /// <summary>Two trees with the same nodes (records compare their children lists by reference).</summary>
    private sealed class NodeComparer : IEqualityComparer<ExplorerNode>
    {
        public bool Equals(ExplorerNode? x, ExplorerNode? y) =>
            x is not null && y is not null && x with { Children = [] } == y with { Children = [] } && x.Children.SequenceEqual(y.Children, this);

        public int GetHashCode(ExplorerNode obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
