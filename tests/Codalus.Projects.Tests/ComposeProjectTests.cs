using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// Docker Compose projects (M17a, ADR 0042): samples/Containers has a <c>.dcproj</c> whose SDK only the classic IDE has.
/// It loads from its XML as a solution item with its compose files, gets no design-time build, and the solution's
/// restore and build leave it out, so they succeed.
/// </summary>
public sealed class ComposeProjectTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-dcproj-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private string Fixture => Path.Combine(_root, "Containers");

    public async ValueTask InitializeAsync()
    {
        var source = Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Containers");
        Directory.CreateDirectory(Path.Combine(Fixture, "Web", "Properties"));
        foreach (var file in new[] { "Containers.slnx", "docker-compose.dcproj", "docker-compose.yml", "docker-compose.override.yml", ".dockerignore", Path.Combine("Web", "Web.csproj"), Path.Combine("Web", "Program.cs"), Path.Combine("Web", "Dockerfile"), Path.Combine("Web", "Properties", "launchSettings.json") })
        {
            File.Copy(Path.Combine(source, file), Path.Combine(Fixture, file));
        }

        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        await _solutions.OpenAsync(Path.Combine(Fixture, "Containers.slnx"), trusted: true, restore: true, Timeout());
        var deadline = DateTime.UtcNow + s_timeout;
        while (!(_solutions.Progress is { Completed: true } && _solutions.Projects.Count == 2))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out evaluating.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
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

    [Fact]
    public async Task A_dcproj_loads_its_compose_files_and_the_solution_builds_without_it()
    {
        var compose = _solutions.Projects.Values.Single(p => p.IsComposeProject);
        Assert.Equal(ProjectStates.Loaded, compose.State);
        Assert.Null(compose.Error);
        Assert.Empty(compose.TargetFrameworks);
        Assert.Contains(compose.Items, i => i.Path == Path.Combine(Fixture, "docker-compose.yml"));
        Assert.Contains(compose.Items, i => i.Path == Path.Combine(Fixture, "docker-compose.override.yml") && i.DependentUpon == "docker-compose.yml");
        var web = _solutions.Projects.Values.Single(p => !p.IsComposeProject);
        Assert.Equal(ProjectStates.Loaded, web.State);
        Assert.False(web.IsAspireHost);

        // Only the web project gets a design-time build.
        var deadline = DateTime.UtcNow + s_timeout;
        while (_solutions.CompilerInputs.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the design-time build.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.All(_solutions.CompilerInputs.Values, i => Assert.EndsWith("Web.csproj", i.Path, StringComparison.Ordinal));

        var builds = new BuildService(_solutions, _output);
        var result = await builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText());
        Assert.True(File.Exists(Path.Combine(Fixture, "Web", "bin", "Debug", "net10.0", "Web.dll")), builds.Channel.GetText());
        Assert.DoesNotContain("Microsoft.Docker.Sdk", builds.Channel.GetText(), StringComparison.Ordinal);
    }
}
