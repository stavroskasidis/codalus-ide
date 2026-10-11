using System.Reflection;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.ExternalTools;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M26a's workbench pieces (ADR 0033): item templates, the template CLI's parsing and Export Template, folder mode's tasks and launch configurations, External Tools, and the Solution Explorer's Show All Files and folder view.</summary>
public sealed class ProjectSystemDepthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-m26a-" + Guid.NewGuid().ToString("N"));

    private static string RepoRoot => typeof(ProjectSystemDepthTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusRepoRoot").Value!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ item templates

    [Fact]
    public void Templates_are_offered_by_language_and_project_kind()
    {
        var web = new ItemTemplate("Razor Page", "page", "Web", "", "Page1", "") { Source = ItemTemplateSource.DotnetNew, Languages = ["C#"], Tags = ["Web", "ASP.NET"] };
        var config = new ItemTemplate("EditorConfig file", "editorconfig", "Configuration", "", "x", "") { Source = ItemTemplateSource.DotnetNew, Tags = ["Config"] };
        var nunit = new ItemTemplate("NUnit Test Item", "nunit-test", "Test", "", "x", "") { Source = ItemTemplateSource.DotnetNew, Languages = ["C#", "F#", "VB"], Tags = ["Test", "NUnit"] };
        var library = new ItemTemplateProject("C#");
        var site = new ItemTemplateProject("C#", IsWeb: true);
        var fsharpTests = new ItemTemplateProject("F#", IsTest: true);

        Assert.False(ItemTemplates.AppliesTo(web, library));
        Assert.True(ItemTemplates.AppliesTo(web, site));
        Assert.True(ItemTemplates.AppliesTo(config, library));
        Assert.True(ItemTemplates.AppliesTo(nunit, fsharpTests));
        Assert.False(ItemTemplates.AppliesTo(nunit, library));
        Assert.False(ItemTemplates.AppliesTo(ItemTemplates.BuiltIn.Single(t => t.ShortName == "class"), fsharpTests));
        Assert.Equal("F#", ItemTemplateProject.LanguageOf("a/b.fsproj"));

        // A built-in template wins over dotnet new's with the same short name.
        var merged = ItemTemplates.Merge([new ItemTemplate("Class", "class", "Code", "", "Class1", "") { Source = ItemTemplateSource.DotnetNew }, config]);
        Assert.Equal(ItemTemplateSource.BuiltIn, merged.Single(t => t.ShortName == "class").Source);
        Assert.Contains(merged, t => t.ShortName == "editorconfig");
    }

    [Fact]
    public void Built_in_templates_use_the_folders_namespace()
    {
        var project = Path.Combine(_root, "App");
        Assert.Equal("Acme.App.Data.My_Models", ItemTemplates.NamespaceFor("Acme.App", project, Path.Combine(project, "Data", "My Models")));
        Assert.Equal("Acme.App", ItemTemplates.NamespaceFor("Acme.App", project, project));
        Assert.Equal("_2D", ItemTemplates.Identifier("2D"));

        var @class = ItemTemplates.BuiltIn.Single(t => t.ShortName == "class");
        Assert.Equal("Customer.cs", @class.FileName("Customer"));
        Assert.Equal("Customer.cs", @class.FileName("Customer.cs"));
        Assert.Equal("namespace Acme;\n\ninternal class Customer\n{\n}\n", @class.Content!(new ItemTemplateContext("Customer", "Acme")));
        Assert.Equal("namespace Acme\n{\n    internal class Customer\n    {\n    }\n}\n", @class.Content!(new ItemTemplateContext("Customer", "Acme", FileScopedNamespace: false)));
        Assert.True(ItemTemplates.SupportsFileScopedNamespaces(["net8.0", "net10.0"]));
        Assert.False(ItemTemplates.SupportsFileScopedNamespaces(["net10.0", "netstandard2.0"]));

        var resx = ItemTemplates.BuiltIn.Single(t => t.ShortName == "resx");
        Assert.Equal(("EmbeddedResource", "ResXFileCodeGenerator"), (resx.ItemType, resx.Metadata!["Generator"]));
        Assert.Contains("<root>", resx.Content!(new ItemTemplateContext("Strings", "Acme")), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the template CLI

    private const string ListOutput = """
        Warning:
        The following templates use the same identity 'Acme.Dashboard':
          • 'Acme Dashboard' from 'Acme.Templates@1.0.0'
        The template from 'Acme Dashboard' will be used.
        These templates matched your input: --type='item'

        Template Name                    Short Name                  Language    Tags
        -------------------------------  --------------------------  ----------  --------------------
        API Controller                   apicontroller               [C#]        Web/ASP.NET
        Class                            class                       [C#],VB     Common
        dotnet gitignore file            gitignore,.gitignore                    Config
        MSTest Test Class                mstest-class                [C#],F#,VB  Test/MSTest

        """;

    [Fact]
    public void The_template_list_is_parsed_by_its_header_rule()
    {
        var templates = TemplateService.ParseList(ListOutput);
        Assert.Equal(["apicontroller", "class", "gitignore", "mstest-class"], templates.Select(t => t.ShortName));
        var controller = templates[0];
        Assert.Equal(("API Controller", "Web", ItemTemplateSource.DotnetNew), (controller.Name, controller.Category, controller.Source));
        Assert.Equal(["C#"], controller.Languages);
        Assert.Equal(["Web", "ASP.NET"], controller.Tags);
        Assert.Equal("ApiController1", controller.DefaultName);
        Assert.Empty(templates.Single(t => t.ShortName == "gitignore").Languages);
        Assert.Equal("Configuration", templates.Single(t => t.ShortName == "gitignore").Category);
        Assert.Equal(["C#", "F#", "VB"], templates.Single(t => t.ShortName == "mstest-class").Languages);
        Assert.Empty(TemplateService.ParseList("No templates found."));
    }

    [Fact]
    public void Installed_packages_and_updates_are_parsed()
    {
        const string packages = """
            Currently installed items:
               NUnit3.DotNetNew.Template
                  Version: 1.8.1
                  Details:
                     Author: akharlov
                     Reserved: ✘
                     NuGetSource: https://api.nuget.org/v3/index.json
                  Templates:
                     NUnit 3 Test Item (nunit-test) C#
                     NUnit 3 Test Project (nunit) C#
                  Uninstall Command:
                     dotnet new uninstall NUnit3.DotNetNew.Template

               C:\templates\mine
                  Templates:
                     Mine (mine) C#
                  Uninstall Command:
                     dotnet new uninstall C:\templates\mine
            """;
        var parsed = TemplateService.ParsePackages(packages);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(("NUnit3.DotNetNew.Template", "1.8.1", "akharlov", "https://api.nuget.org/v3/index.json"), (parsed[0].Id, parsed[0].Version, parsed[0].Author, parsed[0].Source));
        Assert.Equal(["NUnit 3 Test Item (nunit-test) C#", "NUnit 3 Test Project (nunit) C#"], parsed[0].Templates);
        Assert.Equal((@"C:\templates\mine", null), (parsed[1].Id, parsed[1].Version));
        Assert.Empty(TemplateService.ParsePackages("Currently installed items:\n(No Items)\n"));

        const string updates = """
            An update for template packages is available:
            Package                    Current  Latest
            -------------------------  -------  ------
            NUnit3.DotNetNew.Template  1.8.1    1.9.0

            To update the package use:
            """;
        Assert.Equal([new TemplatePackageUpdate("NUnit3.DotNetNew.Template", "1.8.1", "1.9.0")], TemplateService.ParseUpdates(updates));
        Assert.Empty(TemplateService.ParseUpdates("All template packages are up-to-date."));
    }

    [Fact]
    public async Task An_exported_item_template_installs_and_creates_items_in_its_own_hive()
    {
        var source = Path.Combine(_root, "src", "Invoice.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "namespace Acme.Billing;\n\npublic sealed class Invoice\n{\n    public decimal Total { get; init; }\n}\n");
        var hive = Path.Combine(_root, "hive");
        var templates = new TemplateService(hive, _root);
        var destination = Path.Combine(_root, "templates", "invoice");

        var (succeeded, output) = await templates.ExportAsync(new TemplateExportRequest("item", source, [source], "Invoice", "acme-invoice", "An invoice.", "Acme", "C#", destination), TestContext.Current.CancellationToken);
        Assert.True(succeeded, output);
        var json = File.ReadAllText(Path.Combine(destination, ".template.config", "template.json"));
        Assert.Contains("\"sourceName\": \"Invoice\"", json, StringComparison.Ordinal);
        Assert.Contains("\"replaces\": \"Acme.Billing\"", json, StringComparison.Ordinal);
        Assert.Contains(await templates.PackagesAsync(TestContext.Current.CancellationToken), p => p.Id.Equals(destination, StringComparison.OrdinalIgnoreCase) || p.Templates.Any(t => t.Contains("acme-invoice", StringComparison.Ordinal)));
        Assert.Contains(await templates.ItemTemplatesAsync(TestContext.Current.CancellationToken), t => t.ShortName == "acme-invoice");

        (succeeded, output) = await templates.UninstallAsync(destination, TestContext.Current.CancellationToken);
        Assert.True(succeeded, output);
        Assert.DoesNotContain(await templates.ItemTemplatesAsync(TestContext.Current.CancellationToken), t => t.ShortName == "acme-invoice");
    }

    [Fact]
    public void An_exported_project_leaves_out_build_outputs_and_ide_state()
    {
        var project = Path.Combine(_root, "Tool", "Tool.csproj");
        foreach (var file in new[] { project, Path.Combine(_root, "Tool", "Program.cs"), Path.Combine(_root, "Tool", "bin", "x.dll"), Path.Combine(_root, "Tool", "obj", "y.json"), Path.Combine(_root, "Tool", "Tool.csproj.user"), Path.Combine(_root, "Tool", ".vs", "z") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
        }

        var destination = Path.Combine(_root, "out");
        var template = TemplateService.WriteTemplate(new TemplateExportRequest("project", project, [], "Tool", "tool", "", "me", "C#", destination));
        Assert.Equal("Tool", template["sourceName"]!.GetValue<string>());
        Assert.True(template["preferNameDirectory"]!.GetValue<bool>());
        Assert.Equal("project", template["tags"]!["type"]!.GetValue<string>());
        Assert.Equal(["Program.cs", "Tool.csproj"], Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).Order());
    }

    // ------------------------------------------------------------------ folder mode

    [Fact]
    public async Task Folder_tasks_and_launch_configurations_are_read_expanded_and_run()
    {
        var folder = Path.Combine(RepoRoot, "samples", "ProjectSystem", "FolderOnly");
        var (tasks, error) = FolderConfiguration.ReadTasks(folder);
        Assert.Null(error);
        Assert.Equal(["build tool", "sdk version"], tasks.Select(t => t.Label));
        var build = tasks[0];
        Assert.Equal(("process", "dotnet", "build", true), (build.Type, build.Command, build.Group, build.IsDefault));

        var (launch, launchError) = FolderConfiguration.ReadLaunch(folder);
        Assert.Null(launchError);
        Assert.Equal([("Tool", "project"), ("Tool (program)", "program")], launch.Select(c => (c.Name, c.Type)));
        Assert.Equal(["from", "launch.json"], launch[0].Args);
        Assert.Equal("build tool", launch[1].PreLaunchTask);

        var variables = new FolderVariables(folder, Path.Combine(folder, "src", "Tool", "Program.cs"), Line: 3, SelectedText: "Shout");
        Assert.Equal(Path.Combine(folder, "src", "Tool", "Tool.csproj").Replace('\\', '/'), FolderConfiguration.Expand(launch[0].Project!, variables).Replace('\\', '/'));
        Assert.Equal("Program|.cs|3|Shout|FolderOnly|${unknown}", FolderConfiguration.Expand("${fileBasenameNoExtension}|${fileExtname}|${lineNumber}|${selectedText}|${workspaceFolderBasename}|${unknown}", variables));
        Environment.SetEnvironmentVariable("DAEDALUS_M26A_TEST", "on");
        Assert.Equal("on", FolderConfiguration.Expand("${env:DAEDALUS_M26A_TEST}", variables));

        var output = new OutputService();
        var channel = output.GetOrCreate("tasks", "Tasks");
        var exitCode = await FolderConfiguration.RunTaskAsync(tasks[1], variables, channel, TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.Contains("exited with code 0", channel.GetText(), StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\d+\.\d+\.\d+", channel.GetText());

        var shell = new FolderTask("echo", "shell", "echo", ["hello world"], null, new Dictionary<string, string>(), null, false);
        Assert.Equal(0, await FolderConfiguration.RunTaskAsync(shell, variables, channel, TestContext.Current.CancellationToken));
        Assert.Contains("hello world", channel.GetText(), StringComparison.Ordinal);

        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(Path.Combine(broken, ".daedalus"));
        File.WriteAllText(FolderConfiguration.TasksPath(broken), "{ \"tasks\": [ ");
        Assert.NotNull(FolderConfiguration.ReadTasks(broken).Error);
    }

    // ------------------------------------------------------------------ External Tools

    [Fact]
    public async Task External_tools_round_trip_expand_macros_and_run()
    {
        var file = Path.Combine(_root, "config", ExternalToolStore.FileName);
        var first = new ExternalTool(ExternalTool.NewId("Show SDK", []), "Show SDK", "dotnet", "--version", "$(SolutionDir)", UseOutputWindow: true, PromptForArguments: true);
        var second = new ExternalTool(ExternalTool.NewId("Show SDK", [first.Id]), "Show SDK", "dotnet", "--info");
        Assert.Equal(("showsdk", "showsdk2", "externalTools.run.showsdk"), (first.Id, second.Id, first.CommandId));
        ExternalToolStore.Save(file, [first, second]);
        Assert.Equal([first, second], ExternalToolStore.Load(file));
        Assert.Empty(ExternalToolStore.Load(Path.Combine(_root, "missing.json")));

        var values = new Dictionary<string, string> { ["ItemPath"] = @"C:\a b\c.cs", ["CurLine"] = "12" };
        Assert.Equal("\"C:\\a b\\c.cs\" 12 $(Unknown)", ExternalToolStore.Expand("\"$(ItemPath)\" $(CurLine) $(Unknown)", n => values.GetValueOrDefault(n)));
        Assert.Equal(["ItemPath", "CurLine"], ExternalToolStore.MacrosIn("$(ItemPath) $(CurLine) $(ItemPath)"));
        Assert.Contains(ExternalToolStore.Macros, m => m.Name == "TargetPath");

        var channel = new OutputService().GetOrCreate("tools", "External Tools");
        var exitCode = await ExternalToolRunner.RunAsync("dotnet", "--version", _root, channel, TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.Contains("dotnet exited with code 0.", channel.GetText().Replace("dotnet.exe", "dotnet", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Solution Explorer

    [Fact]
    public void Show_all_files_adds_what_is_on_disk_as_excluded()
    {
        var dir = Path.Combine(_root, "P");
        var project = Path.Combine(dir, "P.csproj");
        string F(string relative) => Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        var info = new ProjectInfo(project, "P", ProjectStates.Loaded, null, ["net10.0"], "Library",
        [
            new("Compile", F("A.cs"), null, null),
            new("Compile", F("Models/M.cs"), null, null),
        ], [], [], Restored: true);
        var disk = new List<DirectoryEntry>
        {
            new(project, false),
            new(F("A.cs"), false),
            new(F("Models"), true),
            new(F("Models/M.cs"), false),
            new(F("Models/Old.cs"), false),
            new(F("bin"), true),
            new(F("bin/Debug"), true),
            new(F("notes.txt"), false),
        };

        var contents = SolutionTree.Contents(project, info, disk);
        Assert.Equal(["bin", "Models", "A.cs", "notes.txt"], contents.Select(n => n.Name));
        Assert.Equal([true, false, false, true], contents.Select(n => n.IsExcluded));
        var models = contents.Single(n => n.Name == "Models");
        Assert.Equal([("M.cs", false), ("Old.cs", true)], models.Children.Select(n => (n.Name, n.IsExcluded)));
        Assert.True(contents.Single(n => n.Name == "bin").Children.Single().IsExcluded);
        Assert.DoesNotContain(contents, n => n.Name == "P.csproj");
        Assert.Equal(["Models", "A.cs"], SolutionTree.Contents(project, info).Select(n => n.Name));
    }

    [Fact]
    public void The_folder_view_shows_the_disk_with_projects_marked()
    {
        var folder = Path.Combine(_root, "repo");
        string F(string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        var solution = new SolutionInfo(folder, SolutionFormats.Folder, [], [new SolutionProjectInfo(F("src/Tool/Tool.csproj"), "Tool", null, null)], [new("Debug", "Any CPU")], new("Debug", "Any CPU"), true);
        var entries = new List<DirectoryEntry>
        {
            new(F("src"), true),
            new(F("src/Tool"), true),
            new(F("src/Tool/Tool.csproj"), false),
            new(F("src/Tool/Program.cs"), false),
            new(F("src/Lib"), true),
            new(F("src/Lib/Lib.csproj"), false),
            new(F("README.md"), false),
        };

        var root = SolutionTree.Build(solution, new Dictionary<string, ProjectInfo>(), options: new ExplorerOptions { FolderEntries = entries });
        Assert.Equal(("Folder 'repo'", "(1 project loaded)"), (root.Name, root.Detail));
        Assert.Equal(["src", "README.md"], root.Children.Select(n => n.Name));
        var src = root.Children[0];
        var lib = src.Children.Single(n => n.Name == "Lib").Children.Single();
        Assert.Equal(("project", "(not loaded)", null), (lib.Icon, lib.Detail, lib.ProjectPath));
        var tool = src.Children.Single(n => n.Name == "Tool");
        Assert.Equal(F("src/Tool/Tool.csproj"), tool.Children.Single(n => n.Name == "Program.cs").ProjectPath);
        Assert.Null(tool.Children.Single(n => n.Name == "Tool.csproj").Detail);

        // The solution view of a folder: its loaded projects.
        var solutionView = SolutionTree.Build(solution, new Dictionary<string, ProjectInfo>());
        Assert.Equal(["Tool"], solutionView.Children.Select(n => n.Name));
        Assert.Equal("Folder 'repo'", solutionView.Name);
        Assert.Equal(folder, solution.Directory);
    }
}
