using System.Diagnostics;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Containers;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// The user's container engine (M17a, ADR 0042): docker, or podman (its docker command too). Tests use
/// <c>DAEDALUS_TEST_CONTAINER_TOOL</c> when set, else the engine the shell would pick. Without an engine that runs Linux
/// containers they skip with the reason; on CI's Linux runner they fail instead (GitHub's macOS runners have no engine,
/// and its Windows runners run only Windows containers).
/// </summary>
internal static class ContainerRuntime
{
    public static async Task<ContainerEngine> RequireAsync()
    {
        var engine = ContainerEngine.Resolve(Environment.GetEnvironmentVariable("DAEDALUS_TEST_CONTAINER_TOOL"));
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None);
        if (os is null || !os.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            var reason = os is null ? $"No container engine answers ({engine.Name}: {error})" : $"{engine.Name} runs {os} containers, not Linux ones";
            if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }

        return engine;
    }

    /// <summary>Builds a copied project with the repository's SDK (Debug), as the shell's build would.</summary>
    public static void Build(string project)
    {
        var start = new ProcessStartInfo("dotnet", ["build", project, "-nologo", "-v", "q"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "dotnet build timed out");
        Assert.True(process.ExitCode == 0, output.Result + error.Result);
    }

    /// <summary>Copies samples/Containers (without build output) into <paramref name="root"/>, with the SDK pinned above it.</summary>
    public static string CopyFixture(string root)
    {
        static void Copy(string source, string target)
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

        var fixture = Path.Combine(root, "Containers");
        Copy(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Containers"), fixture);
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        DaedalusStudio.Tests.FixtureSdk.Pin(root);
        return fixture;
    }
}

/// <summary>Fast mode against the real engine: the base stage built, the build output mounted, the app run through <c>exec</c>.</summary>
public sealed class ContainerRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-containers-" + Guid.NewGuid().ToString("N"));
    private ContainerEngine? _engine;
    private ContainerLaunch? _launch;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null && _launch is not null)
        {
            await _engine.RemoveAsync(_launch.Name, CancellationToken.None);
            await _engine.RunAsync(["rmi", _launch.Image], CancellationToken.None);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_project_runs_in_a_fast_mode_container_and_the_engine_describes_it()
    {
        var engine = _engine = await ContainerRuntime.RequireAsync();
        var ct = TestContext.Current.CancellationToken;
        var fixture = ContainerRuntime.CopyFixture(_root);
        var project = Path.Combine(fixture, "Web", "Web.csproj");
        ContainerRuntime.Build(project);

        var output = Path.Combine(fixture, "Web", "bin", "Debug", "net10.0");
        var info = new RunInfo(project, "net10.0", "Exe", Path.Combine(output, "Web.dll"), "dotnet", "exec Web.dll", output, Path.Combine(fixture, "Web", "Properties", "launchSettings.json"));
        var profile = LaunchSettings.Read(info.LaunchSettings, out _).Single(p => p.CommandName == LaunchProfile.Docker);
        var launch = _launch = ContainerLaunch.Plan(info, profile, DockerSupport.FindDockerfile(project)!, fixture, fastMode: true, new Dictionary<string, string> { ["ASPNETCORE_HTTP_PORTS"] = "8080" });

        var build = await engine.BuildAsync(launch.Dockerfile, launch.Context, launch.Image, launch.Target, null, ct);
        Assert.True(build.Succeeded, build.Output + build.Error);
        await engine.RemoveAsync(launch.Name, ct);
        await engine.RunDetachedAsync(launch.Run, ct);

        var ours = await engine.ContainersAsync(ct, ContainerEngine.ProjectLabel);
        Assert.Contains(ours, c => c.Name == launch.Name && c.IsRunning);
        var details = await engine.InspectAsync(launch.Name, ct);
        Assert.NotNull(details);
        Assert.Contains(details.Environment, e => e is { Key: "ASPNETCORE_HTTP_PORTS", Value: "8080" });
        Assert.Contains(details.Labels, l => l.Key == ContainerEngine.ProjectLabel && l.Value == project);
        Assert.Contains(details.Mounts, m => m.Destination == "/app");
        var hostPort = await engine.HostPortAsync(launch.Name, "8080/tcp", ct);
        Assert.NotNull(hostPort);
        Assert.Contains(await engine.ListFilesAsync(launch.Name, "/app", ct), f => f.Name == "Web.dll");

        // The app, started in the container as Run does it: its output is the exec's.
        var (program, arguments) = engine.Command(launch.ExecArguments());
        using var app = Process.Start(new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false })!;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string? answer = null;
            for (var attempt = 0; attempt < 60 && answer is null; attempt++)
            {
                try
                {
                    answer = await http.GetStringAsync($"http://127.0.0.1:{hostPort}/", ct);
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(1000, ct);
                }
            }

            Assert.NotNull(answer);
            Assert.StartsWith($"Hello from {details.Id} on ", answer, StringComparison.Ordinal); // the container's host name
        }
        finally
        {
            app.Kill(entireProcessTree: true);
        }

        await engine.RemoveAsync(launch.Name, ct);
        Assert.DoesNotContain(await engine.ContainersAsync(ct, ContainerEngine.ProjectLabel), c => c.Name == launch.Name);
    }
}
