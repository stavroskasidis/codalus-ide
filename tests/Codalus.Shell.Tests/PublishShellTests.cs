using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Views;
using Codalus.Workbench.Publishing;

namespace Codalus.Shell.Tests;

/// <summary>The Publish page (M17c, ADR 0042) on samples/Containers/Web: a folder profile made, edited, saved and published.</summary>
public sealed class PublishShellTests
{
    [AvaloniaFact]
    public async Task The_publish_page_saves_a_profile_and_publishes_a_single_file_app()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Containers");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Containers.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        var web = Path.Combine(folder, "Web", "Web.csproj");

        var page = f.Shell.OpenPublish(web);
        Assert.NotNull(page);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PublishView>().Any());
        Assert.Equal(["FolderProfile"], page.Profiles);
        Assert.Equal("net10.0", page.TargetFramework);
        Assert.True(File.Exists(Path.Combine(folder, "Web", "Properties", "PublishProfiles", "FolderProfile.pubxml")));

        // Single-file needs a runtime: the page says so until one is chosen.
        page.SingleFile = true;
        Assert.True(page.HasProblem);
        page.RuntimeIdentifier = PublishProfiles.CurrentRuntimeIdentifier();
        Assert.False(page.HasProblem);
        page.TargetIndex = 1;
        Assert.True(page.IsContainer);
        page.TargetIndex = 0;

        await page.PublishAsync();
        Assert.StartsWith("Published to ", page.Status, StringComparison.Ordinal);
        var output = PublishProfiles.OutputFolder(web, page.Profile!);
        Assert.True(File.Exists(Path.Combine(output, OperatingSystem.IsWindows() ? "Web.exe" : "Web")), page.Status);
        var saved = PublishProfiles.List(web).Single();
        Assert.True(saved.SingleFile);
        Assert.Equal(PublishProfiles.CurrentRuntimeIdentifier(), saved.RuntimeIdentifier);
    }
}
