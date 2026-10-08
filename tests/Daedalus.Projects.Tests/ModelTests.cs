using Daedalus.Projects.Protocol;
using Daedalus.Workbench.Projects;

namespace Daedalus.Projects.Tests;

public class NestingTests
{
    private static string? Parent(string name, params string[] siblings) =>
        SolutionTree.NestParent(name, new HashSet<string>([name, .. siblings], StringComparer.OrdinalIgnoreCase), dependentUpon: null);

    [Theory]
    [InlineData("MainWindow.xaml.cs", "MainWindow.xaml", "MainWindow.xaml")]
    [InlineData("Index.razor.css", "Index.razor", "Index.razor")]
    [InlineData("appsettings.Development.json", "appsettings.json", "appsettings.json")]
    [InlineData("Form1.Designer.cs", "Form1.cs", "Form1.cs")]
    [InlineData("site.min.css", "site.css", "site.css")]
    [InlineData("Program.cs", "Program.Designer.cs", null)]
    [InlineData("appsettings.json", "appsettings.Development.json", null)]
    [InlineData("Foo.Bar.cs", "Baz.cs", null)]
    public void Default_rules(string file, string sibling, string? expected) => Assert.Equal(expected, Parent(file, sibling));

    [Fact]
    public void DependentUpon_wins_and_must_name_a_sibling()
    {
        var siblings = new HashSet<string>(["Strings.resx", "Strings.Designer.cs", "Other.cs"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Strings.resx", SolutionTree.NestParent("Strings.Designer.cs", siblings, "Strings.resx"));
        Assert.Null(SolutionTree.NestParent("Strings.Designer.cs", siblings, "Missing.resx"));
        Assert.Equal("Strings.resx", SolutionTree.NestParent("Strings.Designer.cs", siblings, @"Resources\Strings.resx")); // as written in project files, on every OS
    }

    [Fact]
    public void Contents_build_folders_links_and_nesting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proj");
        var project = Path.Combine(dir, "P.csproj");
        string F(string relative) => Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        var info = new ProjectInfo(project, "P", ProjectStates.Loaded, null, ["net10.0"], "Library",
        [
            new("Compile", F("A.cs"), null, null),
            new("Compile", F("Views/Main.xaml.cs"), null, null),
            new("Page", F("Views/Main.xaml"), null, null),
            new("Compile", Path.Combine(Path.GetTempPath(), "shared", "S.cs"), "Linked/S.cs", null),
            new("Folder", F("Empty"), null, null),
            new("None", project, null, null),
        ], [], [], Restored: true);

        var contents = SolutionTree.Contents(project, info);

        Assert.Equal(["Empty", "Linked", "Views", "A.cs"], contents.Select(n => n.Name));
        var main = Assert.Single(contents.Single(n => n.Name == "Views").Children);
        Assert.Equal("Main.xaml", main.Name);
        Assert.Equal("Main.xaml.cs", Assert.Single(main.Children).Name);
        Assert.True(Assert.Single(contents.Single(n => n.Name == "Linked").Children).IsLinked);
    }
}

public sealed class TrustAndRecentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "daedalus-trust-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Trust_covers_subfolders_and_persists()
    {
        var repos = Path.Combine(_dir, "repos");
        var store = new TrustStore(Path.Combine(_dir, "trust.json"));
        Assert.False(store.IsTrusted(Path.Combine(repos, "a", "a.sln")));

        store.Trust(repos);
        store.Trust(Path.Combine(repos, "a")); // already covered

        var reloaded = new TrustStore(Path.Combine(_dir, "trust.json"));
        Assert.True(reloaded.IsTrusted(Path.Combine(repos, "a", "a.sln")));
        Assert.False(reloaded.IsTrusted(Path.Combine(_dir, "repos2", "b.sln")));
        Assert.Single(reloaded.Folders);

        reloaded.Revoke(repos);
        Assert.False(reloaded.IsTrusted(Path.Combine(repos, "a", "a.sln")));
    }

    [Fact]
    public void Recent_solutions_are_most_recent_first_without_duplicates()
    {
        var recent = new RecentSolutions(Path.Combine(_dir, "recent.json"));
        recent.Add(Path.Combine(_dir, "a.sln"));
        recent.Add(Path.Combine(_dir, "b.slnx"));
        recent.Add(Path.Combine(_dir, "a.sln"));

        Assert.Equal(["a.sln", "b.slnx"], new RecentSolutions(Path.Combine(_dir, "recent.json")).Items.Select(Path.GetFileName));
    }
}
