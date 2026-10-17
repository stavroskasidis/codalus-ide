using Codalus.Platform;
using Codalus.Plugins;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Testing;

namespace Codalus.Plugins.Tests;

/// <summary>
/// M43b (ADR 0076), on Windows only (D31): .NET Framework tests in the Test Explorer through the .NET test plugin, on
/// VSTest's .NET Framework test hosts. samples/NetFramework is restored (packages.config included) and built by the build
/// host; its three net48 test projects (xUnit 2, NUnit 3, and an old-style MSTest v2 project found by its
/// ProjectTypeGuids) are discovered and run, and a net48 test is debugged on the desktop CLR to its breakpoint.
/// Elsewhere they're skipped: .NET Framework test hosts run only on Windows (OlderFrameworksTests covers the rules).
/// </summary>
public sealed class NetFrameworkTestingTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(8);
    private readonly string _root = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-netfx-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private WorkbenchServices? _workbench;
    private SolutionService? _solutions;
    private LanguageService? _languages;
    private DebugService? _debug;
    private TestService? _tests;
    private PluginManager? _plugins;
    private WorkbenchProposedApi? _proposed;

    private string Folder => Path.Combine(_root, "NetFramework");

    private static string Configuration => PluginHostIntegrationTests.Configuration;

    private static string Bin(string project, string assembly) =>
        Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", project, "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, assembly);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private string Log
    {
        get
        {
            lock (_log)
            {
                return string.Join('\n', _log);
            }
        }
    }

    private void Record(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Opens, restores and builds the fixture, and activates the test plugin (only when a test runs here).</summary>
    private async Task StartAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), ".NET Framework test hosts run only on Windows (D31).");
        var timeout = Timeout();
        Copy(Path.Combine(PluginHostIntegrationTests.RepoRoot, "samples", "NetFramework"), Folder);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Codalus.Tests.FixtureSdk.Pin(_root);

        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, ".userdata"));
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        _solutions = new SolutionService(() => Bin("Codalus.Projects.Host", "codalus-buildhost.dll"), policy);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(Bin("Codalus.LanguageServices.Host", "codalus-lshost.dll"))));
        _languages = new LanguageService(registry, _solutions, policy);
        var builds = new BuildService(_solutions, _workbench.Output);
        var debughost = Bin("Codalus.Debugging.Host", "codalus-debughost.dll");
        _debug = new DebugService(Codalus.Debugging.DebugAdapterLocator.Resolve)
        {
            DesktopClrAdapter = () => new Codalus.Debugging.DebugAdapterStartInfo("dotnet", [debughost, Codalus.Debugging.DesktopClr.AdapterArgument], Path.GetDirectoryName(debughost)),
        };
        _debug.Output += (_, output) => Record("[debug] " + output.Text.TrimEnd());
        _tests = new TestService();
        _proposed = new WorkbenchProposedApi(_solutions, builds, _debug, _languages);
        _plugins = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(Bin("Codalus.Plugins.Host", "codalus-pluginhost.dll")), "test", policy)
        {
            ProposedApi = _proposed,
            TestSink = _tests,
        };
        _solutions.Log += (_, line) => Record("[build host] " + line);
        builds.Channel.Appended += (_, text) => Record("[build] " + text?.TrimEnd());
        _workbench.Output.GetOrCreate("codalus.plugins", "Plugins").Appended += (_, text) => Record("[plugins] " + text?.TrimEnd());
        _plugins.AddPlugins(PluginDiscovery.Scan([(DotNetTestFixture.PluginDir, true)], PlatformInfo.Current));

        // The build host restores packages.config (the old-style MSTest project's) and builds; dotnet build alone can't.
        var solution = await _solutions.OpenAsync(Path.Combine(Folder, "NetFramework.slnx"), trusted: true, restore: true, timeout);
        var legacyTests = Path.Combine(Folder, "LegacyTests", "LegacyTests.csproj");
        await WaitUntilAsync(() => _solutions.Projects.Count == solution.Projects.Length && _solutions.Projects.Values.All(p => p.State != "loading")
                                   && _solutions.Projects.TryGetValue(legacyTests, out var tests) && tests.References.Count(r => r.Kind == Codalus.Projects.Protocol.ReferenceKinds.Package && r.Resolved) == 2, timeout);
        Assert.True(_solutions.Projects[legacyTests].IsTestProject, "The old-style MSTest project is a test project by its ProjectTypeGuids.");
        var build = await builds.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, null, timeout);
        Assert.True(build.Succeeded, builds.Channel.GetText());

        await _plugins.ActivateByEventAsync("onTestProjects", timeout);
        await WaitUntilAsync(() => _tests.Read(roots => roots.Count) == 3, timeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (_plugins is not null)
        {
            await _plugins.DisposeAsync();
        }

        _proposed?.Dispose();
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }

        if (_languages is not null)
        {
            await _languages.DisposeAsync();
        }

        if (_solutions is not null)
        {
            await _solutions.DisposeAsync();
        }

        _tests?.Dispose();
        _workbench?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private TestNode Node(params string[] labels) => _tests!.Read(roots =>
    {
        IReadOnlyList<TestNode> level = roots;
        TestNode? node = null;
        foreach (var label in labels)
        {
            node = level.FirstOrDefault(n => n.Label == label) ?? throw new InvalidOperationException($"No '{label}' under {node?.FullLabel ?? "the roots"}: {string.Join(", ", level.Select(n => n.Label))}");
            level = node.Children;
        }

        return node!;
    });

    [Fact]
    public async Task Net48_tests_of_xunit_nunit_and_an_old_style_mstest_project_are_discovered_and_run()
    {
        await StartAsync();
        var timeout = Timeout();
        Assert.Equal(["ConsoleApp.Tests", "LegacyTests", "NUnitTests"], _tests!.Read(roots => roots.Select(r => r.Label).Order(StringComparer.Ordinal).ToList()));

        await _tests.RunAsync(TestRunKind.Run, null, timeout);
        int Passed(string project) => _tests.Read(roots => roots.Single(r => r.Label == project).Leaves().Count(l => l.Outcome == TestOutcome.Passed));
        try
        {
            // NUnit's test cases are tests of their own; MSTest v2's data rows may come as one test with a result per row.
            await WaitUntilAsync(() => !_tests.IsRunning && Passed("ConsoleApp.Tests") == 2 && Passed("NUnitTests") >= 3 && Passed("LegacyTests") >= 2, timeout);
        }
        catch (TimeoutException ex)
        {
            Assert.Fail($"{ex.Message} xUnit {Passed("ConsoleApp.Tests")}, NUnit {Passed("NUnitTests")}, MSTest {Passed("LegacyTests")}. Runs: {string.Join(" || ", _tests.Runs.Select(r => $"{r.Name}: {r.Output}"))}");
        }

        Assert.DoesNotContain(_tests.Read(roots => roots.SelectMany(r => r.Leaves()).ToList()), l => l.Outcome == TestOutcome.Failed);
    }

    [Fact]
    public async Task Debugging_a_net48_test_stops_at_its_breakpoint_on_the_desktop_clr()
    {
        await StartAsync();
        var timeout = Timeout();
        var file = Path.Combine(Folder, "ConsoleApp.Tests", "GreeterTests.cs");
        var line = Array.FindIndex(File.ReadAllLines(file), l => l.Contains("Assert.Equal(\"Hello from .NET Framework, Ada!\"", StringComparison.Ordinal)) + 1;
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _debug!.Stopped += (_, stop) => stopped.TrySetResult(stop);
        _debug.Breakpoints.Toggle(file, line);
        var test = Node("ConsoleApp.Tests", "ConsoleApp.Tests", "GreeterTests", "Greets_by_name");
        var run = _tests!.RunAsync(TestRunKind.Debug, [test.Key], timeout);
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(180), timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No stop. Debug state: {_debug.State}. Log:\n{Log}");
        }

        var frame = _debug.Frames.First(f => f.Source?.Path is not null);
        Assert.Equal("GreeterTests.cs", Path.GetFileName(frame.Source!.Path));
        Assert.Equal(line, frame.Line);
        Assert.Contains(_debug.Modules, m => m.Name == "ConsoleApp.Tests.dll");
        Assert.StartsWith("\".NET Framework 4.", (await _debug.EvaluateAsync("Library.Greeter.RuntimeName", "watch", timeout)).Result, StringComparison.Ordinal);
        await _debug.ContinueAsync();
        await run.WaitAsync(TimeSpan.FromMinutes(3), timeout);
        Assert.Equal(TestOutcome.Passed, Node("ConsoleApp.Tests", "ConsoleApp.Tests", "GreeterTests", "Greets_by_name").AggregateOutcome());
        await WaitUntilAsync(() => !_debug.IsActive, timeout);
    }

    private async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out. Log:\n" + Log);
            }
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "packages")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
