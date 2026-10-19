using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// Build depth (ADR 0034) through the real build host on a copy of samples/BuildAndRun: the fast up-to-date check, each
/// verdict checked against what a real MSBuild build then does; timings and the critical path; binary logs; batch builds;
/// build events; verbosity and log files.
/// </summary>
public sealed class BuildDepthIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-builddepth-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;
    private BuildService _builds = null!;
    private BuildDepthService _depth = null!;
    private BuildOptions _options = new();

    private string Fixture => Path.Combine(_root, "BuildAndRun");

    private string Solution => Path.Combine(Fixture, "BuildAndRun.slnx");

    private string HelloProject => Path.Combine(Fixture, "src", "Hello", "Hello.csproj");

    private string GreetingsProject => Path.Combine(Fixture, "src", "Greetings", "Greetings.csproj");

    private string HelloOutput => Path.Combine(Fixture, "src", "Hello", "bin", "Debug", "net11.0");

    private string HelloDll => Path.Combine(HelloOutput, "Hello.dll");

    private string GreetingsDll => Path.Combine(Fixture, "src", "Greetings", "bin", "Debug", "net11.0", "Greetings.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "BuildAndRun"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _builds = new BuildService(_solutions, _output) { Options = () => _options };
        _depth = new BuildDepthService(_solutions);
        var solution = await _solutions.OpenAsync(Solution, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private async Task<BuildResultInfo> BuildAsync(string? project = null, string target = BuildTargets.Build)
    {
        var result = await _builds.BuildAsync(target, project, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        return result;
    }

    /// <summary>What a real MSBuild build (no up-to-date check) wrote: the files whose write time changed.</summary>
    private async Task<string[]> MSBuildWritesAsync(params string[] files)
    {
        var before = files.ToDictionary(f => f, f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue);
        var options = _options;
        _options = options with { UpToDateCheck = false };
        try
        {
            var result = await BuildAsync();
            Assert.False(result.UpToDate);
        }
        finally
        {
            _options = options;
        }

        return [.. files.Where(f => (File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue) != before[f]).Select(Path.GetFileName).OfType<string>()];
    }

    private async Task<UpToDateResult> CheckAsync(string? project = null) => await _depth.CheckUpToDateAsync(project, Timeout());

    /// <summary>
    /// Two up-to-date builds of the solution report the same result, down to the milliseconds when the check is quick:
    /// a wait for "a build after this one" compared them as equal and never ended (the smoke test's gallery, on the macOS
    /// package). Each finished build is a build of its own.
    /// </summary>
    [Fact]
    public async Task Each_finished_build_is_distinct_even_when_up_to_date_builds_report_the_same()
    {
        await BuildAsync();
        Assert.True((await BuildAsync()).UpToDate, _builds.Channel.GetText());
        var first = _builds.LastBuild!;
        Assert.True((await BuildAsync()).UpToDate, _builds.Channel.GetText());
        var second = _builds.LastBuild!;

        Assert.NotSame(first, second);
        Assert.Equal(first.Request, second.Request);
        Assert.NotEqual(first, second with { Result = first.Result }); // the same request and result: still another build
        Assert.True(second.Number > first.Number);
    }

    [Fact]
    public async Task The_fast_up_to_date_check_agrees_with_MSBuild()
    {
        // Never built here: not up to date, and the build runs MSBuild and records the state.
        var verdict = await CheckAsync();
        Assert.False(verdict.UpToDate);
        Assert.Contains(verdict.Projects, p => p.Reason.Contains("wasn't built here", StringComparison.Ordinal));
        var result = await BuildAsync();
        Assert.False(result.UpToDate);
        Assert.True(File.Exists(HelloDll));

        // Nothing changed: up to date, the build skips MSBuild, and MSBuild itself would not write the assemblies.
        Assert.True((await CheckAsync()).UpToDate, string.Join("; ", (await CheckAsync()).Projects.Select(p => $"{p.Project}: {p.Reason}")));
        result = await BuildAsync();
        Assert.True(result.UpToDate, _builds.Channel.GetText());
        Assert.Contains("up to date, so MSBuild didn't run", _builds.Channel.GetText(), StringComparison.Ordinal); // the build's output is in before its result
        Assert.Empty(await MSBuildWritesAsync(HelloDll, GreetingsDll));
        Assert.True((await CheckAsync()).UpToDate, "an MSBuild no-op build keeps the projects up to date");

        // A source edit in the referenced project: both out of date (Hello through its reference); MSBuild recompiles both.
        var greeter = Path.Combine(Fixture, "src", "Greetings", "Greeter.cs");
        File.AppendAllText(greeter, "\n// edited\n");
        verdict = await CheckAsync();
        Assert.False(verdict.UpToDate);
        Assert.Contains("Greeter.cs", Assert.Single(verdict.Projects, p => p.Project.EndsWith("Greetings.csproj", StringComparison.OrdinalIgnoreCase)).Reason, StringComparison.Ordinal);
        Assert.Contains("Greetings is out of date", (await CheckAsync(HelloProject)).Projects[0].Reason, StringComparison.Ordinal);
        Assert.Contains("Greetings.dll", await MSBuildWritesAsync(HelloDll, GreetingsDll));
        Assert.True((await CheckAsync()).UpToDate);

        // A new file under the glob: out of date; MSBuild compiles it into Hello.dll.
        File.WriteAllText(Path.Combine(Fixture, "src", "Hello", "Extra.cs"), "namespace Hello;\n\ninternal static class Extra { }\n");
        verdict = await CheckAsync(HelloProject);
        Assert.False(verdict.UpToDate);
        Assert.Contains("Extra.cs is a new input", verdict.Projects[0].Reason, StringComparison.Ordinal);
        Assert.Contains("Hello.dll", await MSBuildWritesAsync(HelloDll, GreetingsDll));

        // A file copied to the output: the project file changes (out of date), then the file itself.
        var data = Path.Combine(Fixture, "src", "Hello", "data.txt");
        File.WriteAllText(data, "one");
        var csproj = File.ReadAllText(HelloProject);
        File.WriteAllText(HelloProject, csproj.Replace("</Project>", "  <ItemGroup>\n    <None Update=\"data.txt\" CopyToOutputDirectory=\"PreserveNewest\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
        await _solutions.ReevaluateAsync(HelloProject, Timeout());
        Assert.Contains("Hello.csproj changed", (await CheckAsync(HelloProject)).Projects[0].Reason, StringComparison.Ordinal);
        await BuildAsync(HelloProject);
        var copied = Path.Combine(HelloOutput, "data.txt");
        Assert.Equal("one", File.ReadAllText(copied));
        Assert.True((await CheckAsync()).UpToDate);
        File.WriteAllText(data, "two!");
        Assert.Contains("data.txt changed", (await CheckAsync(HelloProject)).Projects[0].Reason, StringComparison.Ordinal);
        Assert.Contains("data.txt", await MSBuildWritesAsync(HelloDll, copied));
        Assert.Equal("two!", File.ReadAllText(copied));

        // A deleted output: out of date; MSBuild puts it back.
        File.Delete(copied);
        Assert.Contains("data.txt is missing", (await CheckAsync(HelloProject)).Projects[0].Reason, StringComparison.Ordinal);
        Assert.Contains("data.txt", await MSBuildWritesAsync(copied));

        // Another configuration is another state; Clean forgets the state; Rebuild records it.
        await BuildAsync(target: BuildTargets.Clean);
        Assert.False((await CheckAsync()).UpToDate);
        await BuildAsync(target: BuildTargets.Rebuild);
        Assert.True((await CheckAsync()).UpToDate);

        // A project can opt out.
        File.WriteAllText(GreetingsProject, File.ReadAllText(GreetingsProject).Replace("<Nullable>", "<DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>\n    <Nullable>", StringComparison.Ordinal));
        await _solutions.ReevaluateAsync(GreetingsProject, Timeout());
        await BuildAsync();
        verdict = await CheckAsync();
        Assert.Contains("DisableFastUpToDateCheck", Assert.Single(verdict.Projects, p => !p.UpToDate && p.Project.EndsWith("Greetings.csproj", StringComparison.OrdinalIgnoreCase)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builds_record_timings_the_critical_path_and_a_binary_log_that_can_be_browsed_and_searched()
    {
        var binlog = Path.Combine(_root, "logs", "build.binlog");
        var logFile = Path.Combine(_root, "logs", "build.log");
        _options = new BuildOptions
        {
            UpToDateCheck = false,
            BinaryLog = binlog,
            LogFile = logFile,
            LogFileVerbosity = BuildVerbosities.Detailed,
            Verbosity = BuildVerbosities.Normal,
            MaxParallelProjects = 2,
        };
        var result = await BuildAsync();
        Assert.Equal(binlog, result.BinaryLog);
        Assert.True(File.Exists(binlog));
        Assert.Contains("Target \"CoreCompile\"", File.ReadAllText(logFile), StringComparison.Ordinal);
        Assert.Contains("Project \"", _builds.Channel.GetText(), StringComparison.Ordinal); // normal verbosity

        var timings = await _depth.GetTimingsAsync(Timeout());
        Assert.Equal(binlog, timings.BinaryLog);
        Assert.True(timings.TotalMilliseconds > 0);
        Assert.Contains(timings.Projects, p => p.Name == "Hello");
        Assert.Contains(timings.Projects, p => p.Name == "Greetings");
        Assert.Contains(timings.Targets, t => t.Name == "CoreCompile");
        Assert.Contains(timings.Tasks, t => t.Name == "Csc");
        var path = timings.CriticalPath.Select(id => timings.Projects.Single(p => p.Id == id).Name).ToList();
        Assert.Equal("Hello", path[^1]);
        Assert.Contains("Greetings", path);

        var log = await _depth.OpenBinaryLogAsync(binlog, Timeout());
        Assert.True(log.Succeeded);
        Assert.Equal(0, log.Errors);
        Assert.True(log.Nodes > 100);
        Assert.True(log.Root.HasChildren);
        var children = await _depth.GetChildrenAsync(log.Id, log.Root.Id, Timeout());
        Assert.NotEmpty(children);
        var found = await _depth.SearchAsync(log.Id, "Greeter.cs", Timeout());
        Assert.NotEmpty(found.Results);
        Assert.All(found.Results, r => Assert.Equal(log.Root.Id, r.Path[0]));
        var compile = await _depth.SearchAsync(log.Id, "CoreCompile", Timeout());
        Assert.Contains(compile.Results, r => r.Node.Kind == "Target");

        // A level of the tree under a search result's ancestor contains the next node on its path.
        var hit = compile.Results.First(r => r.Node.Kind == "Target");
        var level = await _depth.GetChildrenAsync(log.Id, hit.Path[^2], Timeout());
        Assert.Contains(level, n => n.Id == hit.Node.Id);
        await _depth.CloseBinaryLogAsync(log.Id, Timeout());
    }

    [Fact]
    public async Task Batch_builds_build_each_configuration_and_skip_up_to_date_ones()
    {
        _options = new BuildOptions();
        var batch = await _builds.BatchBuildAsync(BuildTargets.Build, [new BatchBuildEntry(HelloProject, "Debug", "AnyCPU"), new BatchBuildEntry(HelloProject, "Release", "AnyCPU")], Timeout());
        Assert.True(batch.Succeeded, _builds.Channel.GetText());
        Assert.Equal(2, batch.Entries.Length);
        Assert.True(File.Exists(HelloDll));
        Assert.True(File.Exists(Path.Combine(Fixture, "src", "Hello", "bin", "Release", "net11.0", "Hello.dll")));
        Assert.Contains("Batch build: 2 succeeded, 0 failed", _builds.Channel.GetText(), StringComparison.OrdinalIgnoreCase);

        batch = await _builds.BatchBuildAsync(BuildTargets.Build, [new BatchBuildEntry(HelloProject, "Debug", "AnyCPU"), new BatchBuildEntry(HelloProject, "Release", "AnyCPU")], Timeout());
        Assert.True(batch.Succeeded);
        Assert.All(batch.Entries, e => Assert.True(e.Skipped, _builds.Channel.GetText()));
        Assert.Same(batch, _builds.LastBatch);
        Assert.Equal(2, _builds.LastBuild?.Request.Batch);
    }

    [Fact]
    public async Task Build_events_are_written_as_targets_and_run_with_the_build()
    {
        _options = new BuildOptions { UpToDateCheck = false };
        var events = await _depth.SetBuildEventsAsync(HelloProject, "echo pre-build-ran", "echo built> \"$(TargetDir)post.txt\"", "Always", Timeout());
        Assert.Equal("echo pre-build-ran", events.PreBuild);
        Assert.Equal("Always", events.RunPostBuildEvent);
        var text = File.ReadAllText(HelloProject);
        Assert.Contains("<Target Name=\"PreBuild\" BeforeTargets=\"PreBuildEvent\">", text, StringComparison.Ordinal);
        Assert.Contains("<Target Name=\"PostBuild\" AfterTargets=\"PostBuildEvent\">", text, StringComparison.Ordinal);
        Assert.Contains("<RunPostBuildEvent>Always</RunPostBuildEvent>", text, StringComparison.Ordinal);

        _options = new BuildOptions { UpToDateCheck = false, Verbosity = BuildVerbosities.Normal };
        await BuildAsync(HelloProject);
        Assert.Contains("pre-build-ran", _builds.Channel.GetText(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(HelloOutput, "post.txt")));

        events = await _depth.SetBuildEventsAsync(HelloProject, string.Empty, string.Empty, "OnBuildSuccess", Timeout());
        Assert.Equal(string.Empty, events.PreBuild);
        Assert.Equal(string.Empty, events.PostBuild);
        Assert.Equal("OnBuildSuccess", events.RunPostBuildEvent);
        text = File.ReadAllText(HelloProject);
        Assert.DoesNotContain("PreBuild", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RunPostBuildEvent", text, StringComparison.Ordinal);
    }
}
