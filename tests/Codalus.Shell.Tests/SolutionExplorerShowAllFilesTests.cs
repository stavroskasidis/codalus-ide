using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// Show All Files lists again only the projects a change concerns (M56): for a folder that changed, the projects whose
/// folders hold it; after a build, the project built and the projects it references. Every change listed every project.
/// </summary>
public class SolutionExplorerShowAllFilesTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "codalus-show-all-files");

    private static string P(params string[] segments) => Path.Combine([s_root, .. segments]);

    private static string[] Names(IEnumerable<string> projects) => [.. projects.Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_folder_that_changed_lists_the_projects_whose_folders_hold_it()
    {
        string[] projects = [P("src", "App", "App.csproj"), P("src", "App", "Plugins", "Plugins.csproj"), P("src", "Core", "Core.csproj"), P("src", "Core", "Core.Tests.csproj")];

        Assert.Equal(["App"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "App", "Models")])));
        Assert.Equal(["App", "Plugins"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "App", "Plugins", "Views") + Path.DirectorySeparatorChar])));
        Assert.Equal(["App", "Core", "Core.Tests"], Names(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src", "Core"), P("src", "App")])));
        Assert.Empty(SolutionExplorerViewModel.ProjectsHolding(projects, [P("src"), P("docs", "images"), P("src", "Apps")]));
    }

    [Fact]
    public void A_build_lists_the_project_built_and_the_projects_it_references()
    {
        static ProjectInfo Project(string name, params string[] references) =>
            new(P(name, name + ".csproj"), name, ProjectStates.Loaded, null, ["net10.0"], "Library", [],
            [
                .. references.Select(r => new ReferenceInfo(ReferenceKinds.Project, r, null, P(r, r + ".csproj"), "net10.0", true)),
                new ReferenceInfo(ReferenceKinds.Package, "Some.Package", "1.0.0", null, "net10.0", true),
            ], [], Restored: true);
        var projects = new[] { Project("App", "Ui"), Project("Ui", "Core"), Project("Core", "Ui"), Project("Other") }.ToDictionary(p => p.Path, Workbench.Projects.SolutionService.PathComparer);

        Assert.Equal(["App", "Core", "Ui"], Names(SolutionExplorerViewModel.WithReferences(projects, P("App", "App.csproj"))));
        Assert.Equal(["Core", "Ui"], Names(SolutionExplorerViewModel.WithReferences(projects, P("Core", "Core.csproj")))); // a cycle ends
        Assert.Equal(["Other"], Names(SolutionExplorerViewModel.WithReferences(projects, P("Other", "Other.csproj"))));
    }
}
