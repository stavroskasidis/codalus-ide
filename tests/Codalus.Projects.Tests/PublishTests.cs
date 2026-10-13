using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Publishing;

namespace Codalus.Projects.Tests;

/// <summary>
/// Publishing through the build host (M17c, ADR 0042) on samples/Containers/Web: a folder profile (framework-dependent,
/// single-file for this machine's runtime) and, with a container engine, a container image through the SDK's
/// <c>PublishContainer</c> (its base image comes from mcr.microsoft.com; the image is removed afterwards).
/// </summary>
public sealed class PublishTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-publish-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private string Project => Path.Combine(_root, "Web", "Web.csproj");

    public async ValueTask InitializeAsync()
    {
        var source = Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Containers", "Web");
        Directory.CreateDirectory(Path.Combine(_root, "Web", "Properties"));
        foreach (var file in new[] { "Web.csproj", "Program.cs", "Dockerfile", Path.Combine("Properties", "launchSettings.json") })
        {
            File.Copy(Path.Combine(source, file), Path.Combine(_root, "Web", file));
        }

        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        await _solutions.OpenAsync(Project, trusted: true, restore: true, Timeout());
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

    [Fact]
    public async Task A_folder_profile_publishes_a_single_file_app()
    {
        var rid = PublishProfiles.CurrentRuntimeIdentifier();
        var profile = new PublishProfile { Name = "FolderProfile", TargetFramework = "net10.0", RuntimeIdentifier = rid, SingleFile = true, Folder = "out/folder/" };
        PublishProfiles.Save(Project, profile);
        var builds = new BuildService(_solutions, _output);
        var result = await builds.BuildAsync(BuildTargets.Publish, Project, PublishProfiles.Properties(profile), Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText());

        var folder = PublishProfiles.OutputFolder(Project, profile);
        var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "Web.exe" : "Web");
        Assert.True(File.Exists(executable), $"No {executable}:\n{builds.Channel.GetText()}");
        Assert.False(File.Exists(Path.Combine(folder, "Web.dll")), "a single-file publish has no separate Web.dll");
        Assert.Contains("Publish started", builds.Channel.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_container_profile_publishes_an_image_to_the_local_engine()
    {
        var engine = ContainerEngine.Resolve(Environment.GetEnvironmentVariable("CODALUS_TEST_CONTAINER_TOOL"));
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None);
        if (os is not "linux")
        {
            var reason = os is null ? $"No container engine answers ({engine.Name}: {error})" : $"{engine.Name} runs {os} containers";
            Assert.True(!(Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux()), reason);
            Assert.Skip(reason);
        }

        var repository = "codalus-publish-test-" + Guid.NewGuid().ToString("N")[..8];
        var profile = new PublishProfile { Name = "Image", Target = PublishTarget.Container, TargetFramework = "net10.0", ContainerRepository = repository, ContainerImageTag = "1.0" };
        PublishProfiles.Save(Project, profile);
        var builds = new BuildService(_solutions, _output);
        try
        {
            var result = await builds.BuildAsync(BuildTargets.PublishContainer, Project, PublishProfiles.Properties(profile, engine.Name), Timeout());
            Assert.True(result.Succeeded, builds.Channel.GetText());
            var images = await engine.ImagesAsync(CancellationToken.None);
            Assert.Contains(images, i => i.Repository.EndsWith(repository, StringComparison.Ordinal) && i.Tag == "1.0");
        }
        finally
        {
            await engine.RunAsync(["rmi", $"{repository}:1.0"], CancellationToken.None);
        }
    }
}
