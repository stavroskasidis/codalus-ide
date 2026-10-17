using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

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
    [InlineData("MainForm.resx", "MainForm.cs", "MainForm.cs")] // M40a: Windows Forms
    [InlineData("MainForm.resx", "MainForm.vb", "MainForm.vb")]
    [InlineData("Resources.Designer.cs", "Resources.resx", "Resources.resx")]
    [InlineData("Settings.Designer.cs", "Settings.settings", "Settings.settings")]
    [InlineData("MainForm.de.resx", "MainForm.resx", "MainForm.resx")]
    [InlineData("Strings.resx", "Other.cs", null)]
    public void Default_rules(string file, string sibling, string? expected) => Assert.Equal(expected, Parent(file, sibling));

    [Fact]
    public void A_forms_designer_file_and_resources_nest_under_its_code()
    {
        Assert.Equal("MainForm.cs", Parent("MainForm.Designer.cs", "MainForm.cs", "MainForm.resx"));
        Assert.Equal("MainForm.cs", Parent("MainForm.resx", "MainForm.cs", "MainForm.Designer.cs"));
        Assert.Null(Parent("MainForm.cs", "MainForm.resx", "MainForm.Designer.cs"));
    }

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

    [Fact]
    public void Analyzers_come_from_the_design_time_build_of_the_active_framework()
    {
        Assert.Equal([@"C:\a\One.dll", "/b/Two.dll", "/c/Three.dll"], SolutionTree.AnalyzerPaths(["/reference:x.dll", @"/analyzer:""C:\a\One.dll""", "/analyzer:/b/Two.dll,/c/Three.dll", "Program.cs"]));

        var project = Path.Combine(Path.GetTempPath(), "proj", "P.csproj");
        var info = new ProjectInfo(project, "P", ProjectStates.Loaded, null, ["net10.0", "netstandard2.0"], "Library", [], [], [], Restored: true);
        var inputs = new CompilerInputsInfo(project, "netstandard2.0", "C#", CompilerInputStates.Ok, null, ["/analyzer:/sdk/Gen.dll"], [], null, null);
        var solution = new SolutionInfo(project, "project", [], [new SolutionProjectInfo(project, "P", null, null)], [], null, Trusted: true);

        var dependencies = SolutionTree.Build(solution, new Dictionary<string, ProjectInfo> { [project] = info }, new Dictionary<string, CompilerInputsInfo> { [project] = inputs })
            .Children.Single().Children.Single(n => n.Kind == ExplorerNodeKind.Dependencies);

        Assert.Empty(dependencies.Children.Single(n => n.Name == "net10.0").Children);
        var analyzers = dependencies.Children.Single(n => n.Name == "netstandard2.0").Children.Single(n => n.Name == "Analyzers");
        Assert.Equal("Gen", Assert.Single(analyzers.Children).Name);
    }
}

public sealed class TrustAndRecentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codalus-trust-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Recent_entries_keep_their_times_and_pins_group_by_date_and_read_the_older_file()
    {
        // Wednesday 14 October 2026, noon.
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = now;
        var file = Path.Combine(_dir, "recent.json");
        var recent = new RecentSolutions(file, () => clock);
        void Opened(string name, TimeSpan ago)
        {
            clock = now - ago;
            recent.Add(Path.Combine(_dir, name));
        }

        Opened("older.sln", TimeSpan.FromDays(60));
        Opened("month.sln", TimeSpan.FromDays(10)); // 4 October: this month, last week
        Opened("week.sln", TimeSpan.FromDays(2)); // Monday
        Opened("yesterday.sln", TimeSpan.FromDays(1));
        Opened("today.sln", TimeSpan.FromHours(1));
        recent.SetPinned(Path.Combine(_dir, "older.sln"), true);

        var reloaded = new RecentSolutions(file, () => now);
        var groups = RecentSolutions.Group(reloaded.Entries, now);
        Assert.Equal(["Pinned", "Today", "Yesterday", "This week", "This month"], groups.Select(g => g.Group));
        Assert.Equal("older.sln", groups[0].Entries.Single().Name);
        Assert.Equal(now - TimeSpan.FromHours(1), reloaded.Entries[0].LastOpened);

        // Opening a pinned entry keeps it pinned; the others are capped, the pinned stay.
        reloaded.Add(Path.Combine(_dir, "older.sln"));
        Assert.True(reloaded.Entries[0].Pinned);
        for (var i = 0; i < RecentSolutions.Max + 5; i++)
        {
            reloaded.Add(Path.Combine(_dir, $"many{i}.sln"));
        }

        Assert.Equal(RecentSolutions.Max + 1, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, e => e is { Pinned: true, Name: "older.sln" });

        // A file from before M38 (paths only): the same order, no times, nothing pinned.
        var old = Path.Combine(_dir, "old-recent.json");
        File.WriteAllText(old, $$"""{ "solutions": [ {{JsonSerializer.Serialize(Path.Combine(_dir, "x.sln"))}}, {{JsonSerializer.Serialize(Path.Combine(_dir, "y.sln"))}} ] }""");
        var migrated = new RecentSolutions(old);
        Assert.Equal(["x.sln", "y.sln"], migrated.Items.Select(Path.GetFileName));
        Assert.Equal("Older", RecentSolutions.Group(migrated.Entries, now).Single().Group);
    }
}
