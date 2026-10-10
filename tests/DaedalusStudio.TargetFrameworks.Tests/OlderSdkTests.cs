using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// Solutions that pin an older SDK in <c>global.json</c> (M33, ADR 0030), from samples/TargetFrameworks/Sdks: down to
/// the build host's floor (2.1.200) they open and build with that SDK; below it, the newest SDK stands in and the
/// solution is told why.
/// </summary>
public sealed class OlderSdkTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-sdks-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    /// <summary>The folders of samples/TargetFrameworks/Sdks and the SDK each one pins.</summary>
    public static TheoryData<string, string, string> Pinned => new()
    {
        { "Sdk60", "6.0.428", "net6.0" },
        { "Sdk50", "5.0.408", "net5.0" },
        { "Sdk31", "3.1.426", "netcoreapp3.1" },
        { "Sdk21", "2.1.818", "netcoreapp2.1" },
        { "Sdk202", "2.1.202", "netcoreapp2.0" },
    };

    public ValueTask InitializeAsync()
    {
        _solutions = new SolutionService(() => Matrix.BuildHost, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _solutions.Log += (_, line) => Add("[host] " + line);
        _solutions.RestoreOutput += (_, line) => Add("[restore] " + line);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        Matrix.DeleteQuietly(_root);
    }

    private void Add(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

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

    private static void RequireSdk(string sdk)
    {
        CompatRoot.Require();
        Assert.SkipWhen(!CompatRoot.OlderSdks.Contains(sdk), $"The .NET SDK {sdk} is not in the matrix on this machine (eng/get-runtimes.sh leaves out SDKs without an Arm64 build on Apple silicon).");
        var reason = LinuxLegacy.Missing(new Version(sdk));
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
    }

    private async Task<string> OpenAsync(string name)
    {
        var folder = Path.Combine(Matrix.CopySample(_root), "Sdks", name);
        var solution = await _solutions.OpenAsync(Path.Combine(folder, name + ".sln"), trusted: true, restore: true, Timeout());
        await MatrixFixture.WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length
                                                 && _solutions.RestoreState is "succeeded" or "failed", Timeout());
        return folder;
    }

    [Theory]
    [MemberData(nameof(Pinned))]
    public async Task A_pinned_sdk_from_the_floor_on_opens_builds_and_runs_the_solution(string name, string sdk, string tfm)
    {
        RequireSdk(sdk);
        var folder = await OpenAsync(name);
        Assert.Equal(sdk, Path.GetFileName(Path.TrimEndingDirectorySeparator(_solutions.SdkPath!)));
        Assert.Null(_solutions.SdkWarning);
        Assert.True(_solutions.RestoreState == "succeeded", "The restore failed:\n" + Log);
        var app = Path.Combine(folder, "App", "App.csproj");
        Assert.All(_solutions.Projects.Values, p => Assert.Equal(ProjectStates.Loaded, p.State));
        Assert.Equal([tfm], _solutions.Projects[app].TargetFrameworks);

        // MSBuild before 17 (the 5.0 SDK and older) builds in the build host's own node.
        var builds = new BuildService(_solutions, new OutputService());
        var result = await builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText() + "\n" + Log);
        Assert.Equal(0, result.Errors);

        using var runs = new RunService(_solutions, new OutputService(), _ => { }) { DotnetRoot = CompatRoot.Path };
        var program = await runs.StartAsync(app, externalConsole: false, Timeout());
        Assert.Equal(0, await program.Exited.WaitAsync(Timeout()));
        await MatrixFixture.WaitUntilAsync(() => program.Channel!.GetText().Contains("has exited", StringComparison.Ordinal), Timeout());
        var expected = Version.Parse(tfm.Replace("netcoreapp", string.Empty, StringComparison.Ordinal).Replace("net", string.Empty, StringComparison.Ordinal));
        Assert.Contains($"runtime={expected.Major}.{expected.Minor}.", program.Channel!.GetText(), StringComparison.Ordinal);
        Assert.Contains($"built with the SDK that {name}/global.json pins", program.Channel!.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Below_the_floor_the_newest_sdk_stands_in_and_the_solution_is_told_why()
    {
        RequireSdk("2.0.3");
        var folder = await OpenAsync("Sdk20");
        var used = Path.GetFileName(Path.TrimEndingDirectorySeparator(_solutions.SdkPath!));
        Assert.Equal(CompatRoot.Stamp["sdk"].Single(), used);
        Assert.Contains("resolves to the .NET SDK 2.0.3", _solutions.SdkWarning, StringComparison.Ordinal);
        Assert.Contains("with the .NET SDK 2.1.200 or later", _solutions.SdkWarning, StringComparison.Ordinal);
        Assert.Contains($"uses the .NET SDK {used} instead", _solutions.SdkWarning, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(folder, "global.json"), _solutions.SdkWarning, StringComparison.Ordinal);

        // The project still opens and builds, with the SDK that stands in.
        Assert.All(_solutions.Projects.Values, p => Assert.Equal(ProjectStates.Loaded, p.State));
        var builds = new BuildService(_solutions, new OutputService());
        Assert.True((await builds.BuildAsync(BuildTargets.Build, null, Timeout())).Succeeded, builds.Channel.GetText());
    }

    [Fact]
    public async Task A_global_json_that_asks_for_a_missing_sdk_is_named()
    {
        CompatRoot.Require();
        var folder = Path.Combine(_root, "Missing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), """{ "sdk": { "version": "6.0.999", "rollForward": "disable" } }""");
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        await _solutions.OpenAsync(Path.Combine(folder, "Lib.csproj"), trusted: true, restore: false, Timeout());
        await MatrixFixture.WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == 1, Timeout());
        Assert.Contains("asks for the .NET SDK 6.0.999 (rollForward: disable), and no installed SDK matches", _solutions.SdkWarning, StringComparison.Ordinal);
        Assert.Equal(ProjectStates.Loaded, Assert.Single(_solutions.Projects.Values).State);
    }
}
