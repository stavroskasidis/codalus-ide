using System.Diagnostics;
using DaedalusStudio.Projects.Host;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Projects.Tests;

/// <summary>
/// M30 (ADR 0025): the build host loads the SDK that <c>dotnet</c> resolves for the solution's folder, and a newer
/// .NET's SDK through rolling forward. samples/Net11 needs the .NET 11 SDK: skipped without it locally, failing in CI.
/// </summary>
public sealed class DotnetVersionsTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-versions-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private readonly List<string> _log = [];
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    /// <summary>The installed SDK versions (<c>dotnet --list-sdks</c>).</summary>
    internal static IReadOnlyList<string> InstalledSdks { get; } = ListSdks();

    /// <summary>Skips without the .NET 11 SDK, except in CI, where it must be installed.</summary>
    internal static void RequireNet11Sdk() =>
        Assert.SkipWhen(!InstalledSdks.Any(s => s.StartsWith("11.0.", StringComparison.Ordinal)) && Environment.GetEnvironmentVariable("CI") != "true",
            "The .NET 11 SDK is not installed (samples/Net11/global.json).");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) =>
        {
            lock (_log)
            {
                _log.Add(line);
            }

            Console.Error.WriteLine("[host] " + line);
        };
        return ValueTask.CompletedTask;
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

    [Fact]
    public async Task A_net11_solution_opens_builds_and_runs_with_the_net11_sdk()
    {
        RequireNet11Sdk();
        var fixture = Path.Combine(_root, "Net11");
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Net11"), fixture);
        var app = Path.Combine(fixture, "src", "App", "App.csproj");
        var shapes = Path.Combine(fixture, "src", "Shapes", "Shapes.csproj");

        var solution = await _solutions.OpenAsync(Path.Combine(fixture, "Net11.slnx"), trusted: true, restore: true, Timeout());
        Assert.Contains(_log, l => l.Contains("DOTNET_ROLL_FORWARD=LatestMajor", StringComparison.Ordinal)); // the host is on .NET 10
        await WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length);
        Assert.StartsWith("11.0.", Path.GetFileName(Path.TrimEndingDirectorySeparator(_solutions.SdkPath!)), StringComparison.Ordinal);
        Assert.Null(_solutions.SdkWarning);
        Assert.Equal(["net11.0"], _solutions.Projects[app].TargetFrameworks);
        Assert.Equal(["net10.0", "net11.0"], _solutions.Projects[shapes].TargetFrameworks);
        Assert.All(_solutions.Projects.Values, p => Assert.Equal(ProjectStates.Loaded, p.State));
        Assert.All(_solutions.Projects.Values, p => Assert.DoesNotContain(p.Diagnostics, d => d.Contains("NETSDK", StringComparison.Ordinal)));

        // The design-time build: the .NET 11 reference assemblies feed the language service.
        await WaitUntilAsync(() => _solutions.RestoreState == "succeeded" && _solutions.CompilerInputs.ContainsKey(app));
        Assert.Contains(_solutions.CompilerInputs[app].Arguments, a => a.Replace('\\', '/').Contains("/Microsoft.NETCore.App.Ref/11.0.", StringComparison.Ordinal));

        var builds = new BuildService(_solutions, _output);
        var result = await builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText());

        using var runs = new RunService(_solutions, _output, _ => { });
        var (info, _, error) = await runs.GetProfilesAsync(app, Timeout());
        Assert.Null(error);
        Assert.Equal("net11.0", info.TargetFramework);
        var program = await runs.StartAsync(app, externalConsole: false, Timeout());
        Assert.Equal(0, await program.Exited.WaitAsync(Timeout()));
        await WaitUntilAsync(() => program.Channel!.GetText().Contains("has exited", StringComparison.Ordinal));
        Assert.Contains("runtime=11 library=net11.0 area=12.57", program.Channel!.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_build_host_uses_the_sdk_that_global_json_pins()
    {
        // The oldest installed SDK that MSBuild on this runtime surely loads; with several installed, not the newest.
        var pinned = InstalledSdks.Where(v => int.Parse(v.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) is >= 8 && !v.Contains('-', StringComparison.Ordinal)
                && int.Parse(v.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) <= Environment.Version.Major)
            .OrderBy(v => Version.Parse(v))
            .First();
        var folder = Path.Combine(_root, "Pinned");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{pinned}}", "rollForward": "disable" } }""");
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>""");

        await _solutions.OpenAsync(Path.Combine(folder, "Lib.csproj"), trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == 1);
        Assert.Equal(pinned, Path.GetFileName(Path.TrimEndingDirectorySeparator(_solutions.SdkPath!)));
        Assert.Null(_solutions.SdkWarning);
        Assert.Equal(ProjectStates.Loaded, Assert.Single(_solutions.Projects.Values).State);
    }

    [Fact]
    public void Sdks_load_up_to_the_runtimes_version_and_the_warning_names_both()
    {
        Assert.True(SdkSelection.CanLoad(new Version(10, 0, 401), new Version(10, 0, 12)));
        Assert.True(SdkSelection.CanLoad(new Version(8, 0, 425), new Version(10, 0, 12)));
        Assert.False(SdkSelection.CanLoad(new Version(11, 0, 100), new Version(10, 0, 12)));

        var warning = SdkSelection.Unloadable(new Version(11, 0, 100), new Version(10, 0, 401), new Version(10, 0, 12));
        Assert.Contains("SDK 11.0.100", warning, StringComparison.Ordinal);
        Assert.Contains("uses the .NET SDK 10.0.401 instead", warning, StringComparison.Ordinal);
        Assert.Contains("Install the .NET 11.0 runtime", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_floor_is_the_2_1_200_band_and_the_message_below_it_names_the_sdk_and_why()
    {
        Assert.True(SdkSelection.AboveFloor(new Version(2, 1, 202)));
        Assert.True(SdkSelection.AboveFloor(new Version(3, 1, 426)));
        Assert.False(SdkSelection.AboveFloor(new Version(2, 1, 105)));
        Assert.False(SdkSelection.AboveFloor(new Version(2, 0, 3)));
        Assert.False(SdkSelection.AboveFloor(new Version(1, 1, 14)));

        var below = SdkSelection.BelowFloor("2.0.3", "/src/global.json", new Version(10, 0, 401));
        Assert.Contains("resolves to the .NET SDK 2.0.3 (/src/global.json)", below, StringComparison.Ordinal);
        Assert.Contains("with the .NET SDK 2.1.200 or later", below, StringComparison.Ordinal);
        Assert.Contains("uses the .NET SDK 10.0.401 instead", below, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6.0.428", "disable", true, "6.0.428", true)]
    [InlineData("6.0.428", "disable", true, "6.0.429", false)]
    [InlineData("6.0.400", "latestPatch", true, "6.0.428", true)]
    [InlineData("6.0.400", "latestPatch", true, "6.0.300", false)]
    [InlineData("6.0.400", "latestPatch", true, "6.0.500", false)]
    [InlineData("2.1.202", "latestPatch", true, "2.1.818", false)]
    [InlineData("6.0.100", "latestFeature", true, "6.0.428", true)]
    [InlineData("6.0.100", "latestFeature", true, "7.0.100", false)]
    [InlineData("6.0.100", "latestMinor", true, "6.1.100", true)]
    [InlineData("6.0.100", "latestMajor", true, "10.0.401", true)]
    [InlineData("10.0.100", "latestMajor", false, "11.0.100-rc.1.26425.128", false)]
    [InlineData("10.0.100", "latestMajor", true, "11.0.100-rc.1.26425.128", true)]
    [InlineData("6.0.999", "disable", true, "10.0.401", false)]
    public void Global_json_accepts_the_sdks_its_roll_forward_policy_allows(string version, string rollForward, bool prerelease, string candidate, bool accepted) =>
        Assert.Equal(accepted, new GlobalJsonSdk("global.json", version, rollForward, prerelease).Accepts(candidate));

    [Fact]
    public void Global_json_is_read_from_the_nearest_folder_with_its_defaults()
    {
        var folder = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "sdk": { "version": "8.0.100", "rollForward": "latestMajor" } }""");
        File.WriteAllText(Path.Combine(_root, "a", "global.json"), """
            {
              // comments are allowed
              "sdk": { "version": "6.0.400", },
            }
            """);
        var found = GlobalJsonSdk.Find(folder)!;
        Assert.Equal(("6.0.400", "latestPatch", true), (found.Version, found.RollForward, found.AllowPrerelease));
        Assert.Equal(Path.Combine(_root, "a", "global.json"), found.Path);
        Assert.Contains("asks for the .NET SDK 6.0.400 (rollForward: latestPatch), and no installed SDK matches", SdkSelection.NotInstalled(found, new Version(10, 0, 401)), StringComparison.Ordinal);
    }

    private static List<string> ListSdks()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')[0])];
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
}
