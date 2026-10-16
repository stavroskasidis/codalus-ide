using Codalus.Projects.Protocol;
using Codalus.Workbench.FSharp;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M36 (ADR 0067): an F# project's files in Solution Explorer (compile order, signature files under their
/// implementation), Move Up and Move Down, F#'s item templates, and installing FsAutoComplete and Fantomas with
/// <c>dotnet tool install</c> (a stand-in: nothing installs for real).
/// </summary>
public sealed class FSharpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-fsharp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string F(string relative) => Path.Combine(_root, "P", relative.Replace('/', Path.DirectorySeparatorChar));

    private ProjectInfo Project(string extension, params ProjectItemInfo[] items) =>
        new(F("P" + extension), "P", ProjectStates.Loaded, null, ["net10.0"], "Library", items, [], [], Restored: true);

    [Fact]
    public void An_fsharp_project_shows_its_files_in_compile_order_with_signatures_nested()
    {
        var info = Project(".fsproj",
            new("None", F("README.md"), null, null),
            new("Compile", F("Zeta.fsi"), null, null),
            new("Compile", F("Zeta.fs"), null, null),
            new("Compile", F("Models/Order.fs"), null, null),
            new("Compile", F("Alpha.fs"), null, null),
            new("Compile", F("Program.fs"), null, null),
            new("None", F("Docs/notes.txt"), null, null));

        var contents = SolutionTree.Contents(info.Path, info);
        Assert.Equal(["Zeta.fs", "Models", "Alpha.fs", "Program.fs", "Docs", "README.md"], contents.Select(n => n.Name));
        Assert.Equal(["Zeta.fsi"], contents[0].Children.Select(n => n.Name));

        // The same items in a C# project: by name, folders first.
        var csharp = Project(".csproj", [.. info.Items.Select(i => i with { Path = Path.ChangeExtension(i.Path, i.Kind == "Compile" ? ".cs" : Path.GetExtension(i.Path)) })]);
        Assert.Equal(["Docs", "Models", "Alpha.cs", "Program.cs", "README.md", "Zeta.cs"], SolutionTree.Contents(csharp.Path, csharp).Select(n => n.Name));
        Assert.Equal("Shapes.fs", SolutionTree.NestParent("Shapes.fsi", new HashSet<string> { "Shapes.fs", "Shapes.fsi" }, null));
        Assert.Null(SolutionTree.NestParent("Shapes.fsi", new HashSet<string> { "Other.fs", "Shapes.fsi" }, null));
    }

    [Fact]
    public void Move_up_and_down_keep_a_signature_with_its_implementation_and_stay_in_the_folder()
    {
        IReadOnlyList<string> order = [F("A.fsi"), F("A.fs"), F("B.fs"), F("C.fs"), F("Sub/D.fs")];
        Assert.Equal([(F("B.fs"), F("A.fsi"))], CompileOrder.Step(order, F("B.fs"), down: false));
        Assert.Equal([(F("B.fs"), F("A.fsi"))], CompileOrder.Step(order, F("A.fs"), down: true)); // the pair goes down: B goes before it
        Assert.Equal([(F("A.fsi"), F("B.fs")), (F("A.fs"), F("B.fs"))], CompileOrder.Step([F("B.fs"), F("A.fsi"), F("A.fs")], F("A.fsi"), down: false));
        Assert.Empty(CompileOrder.Step(order, F("A.fsi"), down: false)); // first
        Assert.Empty(CompileOrder.Step(order, F("C.fs"), down: true)); // the next file is in another folder
        Assert.Empty(CompileOrder.Step(order, F("Missing.fs"), down: true));

        var info = Project(".fsproj", new("Compile", F("A.fs"), null, null), new("None", F("x.txt"), null, null), new("Compile", F("L.fs"), F("../Shared/L.fs"), null));
        Assert.Equal([F("A.fs")], CompileOrder.Of(info)); // linked files keep their own order outside the folders
    }

    [Fact]
    public void Fsharp_item_templates_are_compile_items_of_fsharp_projects_only()
    {
        var fsharp = new ItemTemplateProject(ItemTemplateProject.LanguageOf("P.fsproj"));
        var csharp = new ItemTemplateProject(ItemTemplateProject.LanguageOf("P.csproj"));
        var module = ItemTemplates.BuiltIn.Single(t => t.ShortName == "fsmodule");
        Assert.True(ItemTemplates.AppliesTo(module, fsharp));
        Assert.False(ItemTemplates.AppliesTo(module, csharp));
        Assert.False(ItemTemplates.AppliesTo(ItemTemplates.BuiltIn.Single(t => t.ShortName == "class"), fsharp));
        Assert.Equal("Compile", module.ItemType);
        Assert.Equal("module Acme.Shapes\n\n", module.Content!(new ItemTemplateContext("Shapes", "Acme", false)));
        Assert.Equal(".fsi", ItemTemplates.BuiltIn.Single(t => t.ShortName == "fssignature").Extension);
        Assert.Null(ItemTemplates.BuiltIn.Single(t => t.ShortName == "fsscript").ItemType); // scripts aren't compiled
    }

    [Fact]
    public async Task The_tools_install_with_dotnet_tool_install_and_fsautocomplete_starts_with_fantomas_on_its_path()
    {
        var calls = new List<string>();
        var cache = Path.Combine(_root, "cache");
        var dotnet = Path.Combine(_root, "sdk", "dotnet");
        using var tools = new FSharpTools(cache, dotnet, (program, arguments, directory, _) =>
        {
            calls.Add($"{program} {string.Join(' ', arguments)} @{directory}");
            var toolPath = arguments[^1];
            var tool = arguments[2];
            var version = arguments[4];
            var dll = Path.Combine(toolPath, ".store", tool, version, tool, version, "tools", "net10.0", "any", tool + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            File.WriteAllText(dll, string.Empty);
            return Task.FromResult((0, $"Tool '{tool}' was successfully installed."));
        });
        Assert.False(tools.IsInstalled);
        Assert.Null(tools.Launch());

        await tools.EnsureAsync(null, TestContext.Current.CancellationToken);
        Assert.True(tools.IsInstalled);
        Assert.Equal(
        [
            $"{dotnet} tool install fsautocomplete --version {FSharpTools.FsAutoCompleteVersion} --tool-path {tools.FsAutoCompleteFolder} @{tools.Folder}",
            $"{dotnet} tool install fantomas --version {FSharpTools.FantomasVersion} --tool-path {tools.FantomasFolder} @{tools.Folder}",
        ], calls);

        var launch = tools.Launch()!;
        Assert.Equal(dotnet, launch.FileName);
        Assert.EndsWith("fsautocomplete.dll", Assert.Single(launch.Arguments), StringComparison.Ordinal);
        Assert.EndsWith(Path.PathSeparator + tools.FantomasFolder, launch.Environment!["PATH"], StringComparison.Ordinal); // a local or global Fantomas comes first
        Assert.Equal(Path.Combine(_root, "sdk"), launch.Environment["DOTNET_ROOT"]);
        Assert.False(launch.Environment.ContainsKey("DOTNET_ROLL_FORWARD")); // no SDK newer than its net10.0 build
        Assert.Equal(10, FSharpTools.ToolMajor(Assert.Single(launch.Arguments)));

        // With a newer .NET's SDK (M30b: .NET 11's release candidate), FsAutoComplete rolls forward to its runtime.
        Directory.CreateDirectory(Path.Combine(_root, "sdk", "sdk", "11.0.100-rc.2.26504.105"));
        var rolled = tools.Launch()!.Environment!;
        Assert.Equal(("LatestMajor", "1"), (rolled["DOTNET_ROLL_FORWARD"], rolled["DOTNET_ROLL_FORWARD_TO_PRERELEASE"]));

        // Installed once.
        await tools.EnsureAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task A_failed_install_says_why_and_leaves_nothing_half_installed()
    {
        using var tools = new FSharpTools(Path.Combine(_root, "cache"), "dotnet", (_, arguments, _, _) =>
        {
            Directory.CreateDirectory(arguments[^1]);
            return Task.FromResult((1, "error NU1101: Unable to find package fsautocomplete."));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.EnsureAsync(null, TestContext.Current.CancellationToken));
        Assert.Contains("NU1101", error.Message, StringComparison.Ordinal);
        Assert.False(tools.IsInstalled);
        Assert.Null(tools.Launch());
    }
}

/// <summary>Deferred work (M3a): a package's transitive packages under it in the Dependencies node, from the assets file.</summary>
public sealed class TransitivePackageTests
{
    private static ExplorerNode Dependencies(ProjectInfo info) =>
        SolutionTree.Project(new SolutionProjectInfo(info.Path, info.Name, null, null), info, trusted: true).Children.Single(n => n.Kind == ExplorerNodeKind.Dependencies);

    [Fact]
    public void A_package_shows_the_packages_it_depends_on_nested_without_cycles()
    {
        var project = Path.Combine(Path.GetTempPath(), "P", "P.csproj");
        var info = new ProjectInfo(project, "P", ProjectStates.Loaded, null, ["net10.0"], "Library", [],
            [new ReferenceInfo(ReferenceKinds.Package, "Top", "1.0.0", null, "net10.0", true)], [], Restored: true)
        {
            Packages =
            [
                new PackageDependencyInfo("net10.0", "Top", "1.0.0", ["Middle", "Leaf"]),
                new PackageDependencyInfo("net10.0", "Middle", "2.0.0", ["Leaf", "Top"]), // a cycle back to Top
                new PackageDependencyInfo("net10.0", "Leaf", "3.0.0", []),
            ],
        };

        var top = Dependencies(info).Children.Single(g => g.Name == "Packages").Children.Single();
        Assert.Equal(("Top", "(1.0.0)"), (top.Name, top.Detail));
        Assert.Equal([("Leaf", "(3.0.0)"), ("Middle", "(2.0.0)")], top.Children.Select(c => (c.Name, c.Detail)));
        var middle = top.Children.Single(c => c.Name == "Middle");
        Assert.Equal(["Leaf"], middle.Children.Select(c => c.Name)); // not Top again
        Assert.Equal(top.Children.Count + 1 + 1, AllIds(top).Distinct().Count()); // unique ids: Top, Leaf, Middle, Middle's Leaf
    }

    [Fact]
    public void A_large_graph_is_cut_at_the_limit()
    {
        var project = Path.Combine(Path.GetTempPath(), "P", "P.csproj");
        // A chain of packages each depending on every later one: far more paths than the limit.
        var names = Enumerable.Range(0, 30).Select(i => $"Pkg{i:00}").ToList();
        var info = new ProjectInfo(project, "P", ProjectStates.Loaded, null, ["net10.0"], "Library", [],
            [new ReferenceInfo(ReferenceKinds.Package, names[0], "1.0.0", null, "net10.0", true)], [], Restored: true)
        {
            Packages = [.. names.Select((n, i) => new PackageDependencyInfo("net10.0", n, "1.0.0", [.. names.Skip(i + 1)]))],
        };

        var top = Dependencies(info).Children.Single(g => g.Name == "Packages").Children.Single();
        Assert.InRange(AllIds(top).Count() - 1, 1, SolutionTree.TransitiveLimit);
    }

    private static IEnumerable<string> AllIds(ExplorerNode node) => node.Children.SelectMany(AllIds).Prepend(node.Id);
}
