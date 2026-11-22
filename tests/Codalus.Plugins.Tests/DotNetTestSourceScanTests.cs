using Codalus.Plugins.DotNetTest;
using Codalus.Plugins.Testing;

namespace Codalus.Plugins.Tests;

/// <summary>The .NET test plugin's first source scan (ADR 0010): what the Test Explorer shows while sources are read.</summary>
public sealed class DotNetTestSourceScanTests
{
    [Fact]
    public async Task A_test_project_shows_while_its_sources_are_still_read()
    {
        // The project's item waited for the language server to read its sources, and an F# project's server is started for
        // that read (only to say it can't): on a loaded Windows runner the F# Test Explorer test waited its 120 s for the
        // project (stress run 44992968217). The project shows as soon as the solution says it's a test project.
        var project = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FsTests", "FsTests.fsproj"));
        var source = Path.Combine(Path.GetDirectoryName(project)!, "Tests.fs");
        var workspace = new Workspace(new WorkspaceSolution(Path.ChangeExtension(project, ".slnx"), true, "Debug", null, null,
            [new WorkspaceProject(project, "FsTests", "Loaded", ["net10.0"], "Exe", IsTestProject: true, "vstest", [source], [])]));
        var tests = new Tests();
        var context = new TestPluginContext("codalus.dotnettest") { Tests = tests, Workspace = workspace, Build = new Build(), Debug = new Debug() };
        await using var host = await PluginTestHost.ActivateAsync<DotNetTestPlugin>(context, TestContext.Current.CancellationToken);

        await workspace.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var shown = await Until(() => tests.Controller!.Items.Any(i => i.Label == "FsTests"), TimeSpan.FromSeconds(10));
        Assert.True(shown, "The project didn't show while its sources were read; the Test Explorer had: "
            + string.Join(", ", tests.Controller!.Items.Select(i => i.Label)));

        // The read's answer adds the project's tests to the item that shows.
        var range = new TestRange(2, 4, 2, 20);
        workspace.Answer.SetResult([new DocumentTestCandidates(new Uri(source).AbsoluteUri, [new TestCandidate(TestCandidate.MethodKind, "Adds", "Tests.Calc", "Tests", range, range, Parameterized: false)])]);
        await host.Plugin.Initialized.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains("Adds", Labels(tests.Controller!.Items.Single(i => i.Label == "FsTests")));
    }

    private static async Task<bool> Until(Func<bool> condition, TimeSpan limit)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > limit)
            {
                return false;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return true;
    }

    private static IEnumerable<string> Labels(TestItem item) => item.Children.SelectMany(c => Labels(c).Prepend(c.Label));

    /// <summary>A workspace whose source read answers only when the test says so.</summary>
    private sealed class Workspace(WorkspaceSolution solution) : IWorkspace
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<DocumentTestCandidates>> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? SolutionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<DocumentsChangedEventArgs>? DocumentsChanged
        {
            add { }
            remove { }
        }

        public Task<WorkspaceSolution?> GetSolutionAsync(CancellationToken cancellationToken) => Task.FromResult<WorkspaceSolution?>(solution);

        public Task<IReadOnlyList<DocumentTestCandidates>> FindTestCandidatesAsync(IReadOnlyList<string> documentUris, CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            return Answer.Task.WaitAsync(cancellationToken);
        }

        public Task<ProjectOutput> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TypeOutline?> GetTypeOutlineAsync(string documentUri, int line, int character, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspacePackage>> GetPackagesAsync(string projectPath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetProjectPropertiesAsync(string projectPath, IReadOnlyList<string> names, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Tests : ITests
    {
        public Controller? Controller { get; private set; }

        public ITestController CreateController(string id, string label) => Controller = new Controller(id, label);
    }

    private sealed class Controller(string id, string label) : ITestController
    {
        public string Id => id;

        public string Label => label;

        public TestItemCollection Items { get; } = TestItemCollection.CreateRoot();

        public Func<CancellationToken, Task>? RefreshHandler { get; set; }

        public ITestRunProfile CreateRunProfile(string label, TestRunProfileKind kind, Func<TestRunRequest, CancellationToken, Task> handler, bool isDefault = true) => new Profile(label, kind, isDefault);

        public ITestRun CreateRun(TestRunRequest request, string? name = null) => throw new NotSupportedException();

        public TestItem? Find(string id) => Find(Items, id);

        private static TestItem? Find(TestItemCollection items, string id) =>
            items.Get(id) ?? items.Select(item => Find(item.Children, id)).FirstOrDefault(found => found is not null);

        public void Dispose()
        {
        }
    }

    private sealed record Profile(string Label, TestRunProfileKind Kind, bool IsDefault) : ITestRunProfile
    {
        public void Dispose()
        {
        }
    }

    private sealed class Build : IBuild
    {
        public event EventHandler<BuildFinishedEventArgs>? Finished
        {
            add { }
            remove { }
        }

        public Task<BuildResult> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Debug : IDebug
    {
        public Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IDebugSession> AttachAsync(int processId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IDisposable RegisterVisualizer(DebugVisualizerOptions options, Func<IDebugVisualizerContext, CancellationToken, Task<VisualizerView>> visualize) => throw new NotSupportedException();
    }
}
