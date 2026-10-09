using Daedalus.Projects.Protocol;
using Daedalus.Rpc;
using Daedalus.Workbench.Build;
using Daedalus.Workbench.Output;
using Daedalus.Workbench.Projects;

namespace Daedalus.Projects.Tests;

/// <summary>End-to-end builds and runs (ADR 0006): the real build host on a copy of samples/BuildAndRun.</summary>
public sealed class BuildIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private readonly string _root = Path.Combine(RealTempPath(), "daedalus-build-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;
    private BuildService _builds = null!;

    private string Fixture => Path.Combine(_root, "BuildAndRun");

    private string Solution => Path.Combine(Fixture, "BuildAndRun.slnx");

    private string HelloProject => Path.Combine(Fixture, "src", "Hello", "Hello.csproj");

    private string GreetingsProject => Path.Combine(Fixture, "src", "Greetings", "Greetings.csproj");

    /// <summary>
    /// The temp folder without symbolic links: on macOS /var is a link to /private/var, and a solution opened through
    /// the link gets its projects restored twice (once per spelling of the path), racing on project.assets.json.
    /// </summary>
    private static string RealTempPath()
    {
        var temp = Path.GetTempPath();
        return OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal) && Directory.Exists("/private" + temp) ? "/private" + temp : temp;
    }

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "BuildAndRun"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _builds = new BuildService(_solutions, _output);
        var solution = await _solutions.OpenAsync(Solution, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(
            () => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length,
            message: () => $"Evaluation: {_solutions.Progress}; projects: {string.Join(", ", _solutions.Projects.Values.Select(p => $"{p.Name} {p.State} {p.Error}"))}; solution: {string.Join(", ", solution.Projects.Select(p => p.Path))}");
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, Func<string>? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? s_timeout);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out. " + message?.Invoke());
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private string HelloOutput => Path.Combine(Fixture, "src", "Hello", "bin", "Debug", "net10.0");

    [Fact]
    public async Task Building_the_solution_restores_compiles_and_reports_errors_in_the_error_list()
    {
        var result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        var output = _builds.Channel.GetText();
        Assert.True(result.Succeeded, output);
        Assert.Equal(0, result.Errors);
        Assert.Contains("Hello -> ", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(HelloOutput, "Hello.dll")));
        Assert.True(File.Exists(Path.Combine(HelloOutput, "Greetings.dll")));

        // A compile error: structured, with the file and position, and in the output in MSBuild's format.
        var greeter = Path.Combine(Fixture, "src", "Greetings", "Greeter.cs");
        File.WriteAllText(greeter, "namespace Greetings;\n\npublic static class Greeter\n{\n    public static string Greet(string name) => missing;\n}\n");
        result = await _builds.BuildAsync(BuildTargets.Build, HelloProject, Timeout());
        Assert.False(result.Succeeded);
        Assert.False(result.Canceled);
        var error = Assert.Single(_builds.Diagnostics, d => d.Code == "CS0103");
        Assert.Equal(BuildSeverities.Error, error.Severity);
        Assert.Equal(greeter, error.File, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(5, error.Line);
        Assert.Equal(GreetingsProject, error.Project, ignoreCase: OperatingSystem.IsWindows());
        Assert.Contains("Greeter.cs(5,", _builds.Channel.GetText(), StringComparison.Ordinal);
        Assert.Contains("error CS0103", _builds.Channel.GetText(), StringComparison.Ordinal);

        // Clean removes the outputs (and starts with no diagnostics).
        result = await _builds.BuildAsync(BuildTargets.Clean, null, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        Assert.Empty(_builds.Diagnostics);
        Assert.False(File.Exists(Path.Combine(HelloOutput, "Hello.dll")));
    }

    [Fact]
    public async Task A_build_can_be_cancelled_and_only_one_runs_at_a_time()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Timeout());
        var build = _builds.BuildAsync(BuildTargets.Rebuild, null, cancellation.Token);
        Assert.True(_builds.IsBuilding);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _builds.BuildAsync(BuildTargets.Build, null, Timeout()));

        await Task.Delay(300, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await build;
        Assert.True(result.Canceled, _builds.Channel.GetText());
        Assert.False(result.Succeeded);
        Assert.False(_builds.IsBuilding);

        // The host is still usable afterwards.
        result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
    }

    [Fact]
    public async Task Run_info_and_launch_profiles_start_the_built_program()
    {
        Assert.True((await _builds.BuildAsync(BuildTargets.Build, HelloProject, Timeout())).Succeeded, _builds.Channel.GetText());
        var opened = new List<Uri>();
        using var runs = new RunService(_solutions, _output, opened.Add);
        Assert.Equal(HelloProject, runs.StartupProject); // the first executable project, not the first project
        Assert.False(runs.IsExecutable(GreetingsProject));

        var (info, profiles, error) = await runs.GetProfilesAsync(HelloProject, Timeout());
        Assert.Null(error);
        Assert.Equal("net10.0", info.TargetFramework);
        Assert.NotNull(info.RunCommand);
        Assert.Equal(["Hello", "Fails", "IIS Express"], profiles.Select(p => p.Name));

        var program = await runs.StartAsync(HelloProject, externalConsole: false, Timeout());
        Assert.Equal(0, await program.Exited.WaitAsync(Timeout()));
        await WaitUntilAsync(() => program.Channel!.GetText().Contains("has exited with code 0", StringComparison.Ordinal));
        var text = program.Channel!.GetText();
        Assert.Contains("Hello from Hello", text, StringComparison.Ordinal);
        Assert.Contains("args: one two words", text, StringComparison.Ordinal);
        Assert.Contains("GREETING: from the launch profile", text, StringComparison.Ordinal);
        Assert.Contains("to stderr", text, StringComparison.Ordinal);

        // The project folder (macOS reports /private/var for the temp folder's /var, so compare the end).
        var cwd = text.Split('\n').Single(l => l.StartsWith("cwd: ", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.EndsWith(Path.Combine("BuildAndRun", "src", "Hello"), cwd, StringComparison.OrdinalIgnoreCase);
        await WaitUntilAsync(() => runs.Running.Count == 0);

        runs.SetProfile(HelloProject, "Fails");
        program = await runs.StartAsync(HelloProject, externalConsole: false, Timeout());
        Assert.Equal(3, await program.Exited.WaitAsync(Timeout()));

        runs.SetProfile(HelloProject, "IIS Express");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartAsync(HelloProject, externalConsole: false, Timeout()));
        Assert.Empty(opened);
    }

    [Fact]
    public async Task Untrusted_solutions_do_not_build()
    {
        await _solutions.OpenAsync(Solution, trusted: false, restore: false, Timeout());
        var result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.False(result.Succeeded);
        Assert.Contains("trusted", _builds.Channel.GetText(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _solutions.GetRunInfoAsync(HelloProject, Timeout()));
    }
}
