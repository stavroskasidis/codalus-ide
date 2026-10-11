using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Projects.Tests;

/// <summary>
/// SDK-style SQL database projects (M27b, ADR 0038): samples/Data/InventoryDb (Microsoft.Build.Sql) loads with its
/// scripts, gets no design-time build, and builds into a .dacpac through MSBuild.
/// </summary>
public sealed class SqlProjectTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-sqlproj-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        var fixture = Path.Combine(_root, "Data");
        Directory.CreateDirectory(fixture);
        File.Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Data", "Database.slnx"), Path.Combine(fixture, "Database.slnx"));
        CopyFolder(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Data", "InventoryDb"), Path.Combine(fixture, "InventoryDb"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        DaedalusStudio.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        await _solutions.OpenAsync(Path.Combine(fixture, "Database.slnx"), trusted: true, restore: true, Timeout());
        var deadline = DateTime.UtcNow + s_timeout;
        while (!(_solutions.Progress is { Completed: true } && _solutions.Projects.Count == 1))
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

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            CopyFolder(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    [Fact]
    public async Task A_sqlproj_loads_its_scripts_and_builds_a_dacpac()
    {
        var project = Assert.Single(_solutions.Projects.Values);
        Assert.Equal(ProjectStates.Loaded, project.State);
        Assert.Contains(project.Items, i => i.Kind == "Build" && i.Path.EndsWith(Path.Combine("Tables", "Products.sql"), StringComparison.Ordinal));
        Assert.Contains(project.Items, i => i.Kind == "Build" && i.Path.EndsWith(Path.Combine("Views", "ProductsInStock.sql"), StringComparison.Ordinal));

        // No design-time build: no compiler inputs (and no failure) for it.
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        Assert.Empty(_solutions.CompilerInputs);

        var builds = new BuildService(_solutions, _output);
        var result = await builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText());
        Assert.True(File.Exists(Path.Combine(_root, "Data", "InventoryDb", "bin", "Debug", "InventoryDb.dacpac")), builds.Channel.GetText());
    }
}
