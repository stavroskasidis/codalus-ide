using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Workbench.Testing;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The coverage model (ADR 0011): merging a run's reports, replacing older runs, the results tree and Cobertura export.</summary>
public sealed class CoverageStoreTests
{
    private const string A = "file:///c:/src/A.cs";
    private const string B = "file:///c:/src/B.cs";

    private static TestRunCoverageParams Report(string runId, string uri, LineCoverageDto[] lines, params MethodCoverageDto[] methods) =>
        new(runId, [new FileCoverageDto(uri, lines)], methods);

    private static MethodCoverageDto Method(string cls, string method, string uri, int line, int covered, int total, int branchesCovered = 0, int branchesTotal = 0, string assembly = "Lib", string? ns = "Lib") =>
        new(assembly, ns, cls, method, uri, line, covered, total, branchesCovered, branchesTotal);

    [Fact]
    public void Reports_of_one_run_merge_and_a_new_run_replaces_them()
    {
        var store = new CoverageStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        // Two projects (or frameworks) of one run: hits add up, branches take the best.
        store.Add(Report("r1", A, [new(1, 1, 1, 2), new(2, 0, 0, 0)]));
        store.Add(Report("r1", A, [new(1, 2, 2, 2), new(3, 4, 0, 0)]));
        Assert.Equal(
            [new CoverageLine(1, 3, 2, 2), new CoverageLine(2, 0, 0, 0), new CoverageLine(3, 4, 0, 0)],
            store.LinesOf(A));
        Assert.Equal(new CoverageCounts(2, 3, 2, 2), store.Totals);
        Assert.Equal(store.LinesOf(A), store.LinesOf(A.ToUpperInvariant().Replace("FILE:", "file:", StringComparison.Ordinal)));

        store.Add(Report("r2", B, [new(5, 1, 0, 0)]));
        Assert.Empty(store.LinesOf(A));
        Assert.Single(store.LinesOf(B));
        Assert.Equal("r2", store.RunId);
        Assert.Equal(3, changes);

        store.Clear();
        Assert.False(store.HasCoverage);
        Assert.Null(store.CollectedAt);
    }

    [Fact]
    public void Line_states_follow_hits_and_branches()
    {
        Assert.Equal(LineCoverageState.Uncovered, new CoverageLine(0, 0, 0, 2).State);
        Assert.Equal(LineCoverageState.Partial, new CoverageLine(0, 1, 1, 2).State);
        Assert.Equal(LineCoverageState.Covered, new CoverageLine(0, 1, 2, 2).State);
        Assert.Equal(LineCoverageState.Covered, new CoverageLine(0, 7, 0, 0).State);
        Assert.Null(default(CoverageCounts).LinePercent);
        Assert.Equal(75, new CoverageCounts(3, 4, 0, 0).LinePercent);
    }

    [Fact]
    public void The_tree_groups_by_assembly_namespace_and_class_with_totals()
    {
        var store = new CoverageStore();
        store.Add(new TestRunCoverageParams("r1", [], [
            Method("Calc", "Add(int, int)", A, 4, 1, 1),
            Method("Calc", "Divide(int, int)", A, 8, 3, 4, 1, 2),
            Method("Parser", "Parse(string)", B, 2, 0, 5),
            Method("Program", "Main()", null!, 0, 2, 2, assembly: "App", ns: null),
        ]));

        var tree = store.Tree();
        Assert.Equal(["App", "Lib"], tree.Select(n => n.Label));
        Assert.Equal("(global namespace)", tree[0].Children.Single().Label);
        var lib = tree[1];
        Assert.Equal(new CoverageCounts(4, 10, 1, 2), lib.Counts);
        var calc = lib.Children.Single().Children.First(c => c.Label == "Calc");
        Assert.Equal(["Add(int, int)", "Divide(int, int)"], calc.Children.Select(m => m.Label));
        Assert.Equal((A, 4, new CoverageCounts(4, 5, 1, 2)), (calc.Uri, calc.Line, calc.Counts));
        Assert.Equal(CoverageNodeKind.Method, calc.Children[1].Kind);
        Assert.Same(tree, store.Tree()); // cached until the next report
    }

    [Fact]
    public void Cobertura_export_assigns_lines_to_the_method_above_them()
    {
        var store = new CoverageStore();
        store.Add(Report("r1", A, [new(4, 2, 0, 0), new(8, 1, 1, 2), new(9, 0, 0, 0)],
            Method("Calc", "Add(int, int)", A, 4, 1, 1),
            Method("Calc", "Divide(int, int)", A, 8, 1, 2, 1, 2)), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        var root = store.ToCobertura().Root!;
        Assert.Equal(("coverage", "0.6667", "0.5", "1800000000"), (root.Name.LocalName, root.Attribute("line-rate")!.Value, root.Attribute("branch-rate")!.Value, root.Attribute("timestamp")!.Value));
        var cls = root.Descendants("class").Single();
        Assert.Equal("Lib.Calc", cls.Attribute("name")!.Value);
        Assert.EndsWith("A.cs", cls.Attribute("filename")!.Value, StringComparison.Ordinal);
        var methods = cls.Element("methods")!.Elements("method").ToList();
        Assert.Equal(["5"], methods[0].Descendants("line").Select(l => l.Attribute("number")!.Value));
        Assert.Equal(["9", "10"], methods[1].Descendants("line").Select(l => l.Attribute("number")!.Value));
        var branchLine = cls.Element("lines")!.Elements("line").Single(l => l.Attribute("number")!.Value == "9");
        Assert.Equal(("True", "50% (1/2)"), (branchLine.Attribute("branch")!.Value, branchLine.Attribute("condition-coverage")!.Value));
    }
}
