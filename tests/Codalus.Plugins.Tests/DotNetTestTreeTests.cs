using Codalus.Plugins.DotNetTest;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Tests;

/// <summary>The .NET test plugin's items (ADR 0010): which item reports for each runner test, as projects are rebuilt.</summary>
public sealed class DotNetTestTreeTests
{
    private static RunnerTest Test(string project, string method) =>
        new(method, project, null, "Tests.Calc." + method, "Tests.Calc." + method, "Tests.Calc", method, null, null, [], new object());

    private static string MethodId(ProjectTests project, string method) => $"{project.Path}|m:Tests.Calc.{method}";

    [Fact]
    public void A_projects_rebuild_forgets_its_own_runner_tests_only()
    {
        // Each project's rebuild forgets the runner tests it mapped before, through an index of them by project: it looked
        // through every runner test of the solution, for each project (M56).
        var controller = new Controller();
        var tree = new TestTree(controller);
        var projects = Enumerable.Range(0, 3).Select(i => new ProjectTests($"/src/P{i}/P{i}.csproj", $"P{i}")).ToList();
        foreach (var project in projects)
        {
            project.RunnerTests[string.Empty] = [Test(project.Path, "Adds"), Test(project.Path, "Fails")];
        }

        tree.Update(projects);
        Assert.All(projects.SelectMany(p => p.AllRunnerTests), test => Assert.Equal($"{test.Project}|m:Tests.Calc.{test.Method}", tree.ItemFor(test)?.Id));

        // P1's runner no longer lists Fails, which its source still declares: the method's item stays, and no longer
        // reports for the runner test. The other projects' Fails still have theirs.
        var p1 = projects[1];
        var range = new TestRange(9, 4, 12, 5);
        p1.Candidates["file:///src/P1/Calc.cs"] = [new TestCandidate(TestCandidate.MethodKind, "Fails", "Tests.Calc", "Tests", range, range, Parameterized: false)];
        var fails = p1.RunnerTests[string.Empty][1];
        p1.RunnerTests[string.Empty] = [p1.RunnerTests[string.Empty][0]];
        tree.Update(projects);

        Assert.NotNull(controller.Find(MethodId(p1, "Fails")));
        Assert.Null(tree.ItemFor(fails));
        Assert.Equal(MethodId(p1, "Adds"), tree.ItemFor(p1.RunnerTests[string.Empty][0])?.Id);
        Assert.Equal(MethodId(projects[0], "Fails"), tree.ItemFor(projects[0].RunnerTests[string.Empty][1])?.Id);
        Assert.Equal(MethodId(projects[2], "Fails"), tree.ItemFor(projects[2].RunnerTests[string.Empty][1])?.Id);

        // P2 leaves the solution: its runner tests leave the index (they stayed, for as long as the plugin ran).
        tree.Update([projects[0], p1]);
        Assert.Null(tree.ItemFor(projects[2].RunnerTests[string.Empty][0]));
        Assert.Equal(3, tree.MappedRunnerTests);
    }

    /// <summary>A controller whose <see cref="Find"/> walks the tree (the trees here are small).</summary>
    private sealed class Controller : ITestController
    {
        public string Id => "dotnet";

        public string Label => ".NET";

        public TestItemCollection Items { get; } = TestItemCollection.CreateRoot();

        public Func<CancellationToken, Task>? RefreshHandler { get; set; }

        public ITestRunProfile CreateRunProfile(string label, TestRunProfileKind kind, Func<TestRunRequest, CancellationToken, Task> handler, bool isDefault = true) => throw new NotSupportedException();

        public ITestRun CreateRun(TestRunRequest request, string? name = null) => throw new NotSupportedException();

        public TestItem? Find(string id) => Find(Items, id);

        private static TestItem? Find(TestItemCollection items, string id) =>
            items.Get(id) ?? items.Select(item => Find(item.Children, id)).FirstOrDefault(found => found is not null);

        public void Dispose()
        {
        }
    }
}
