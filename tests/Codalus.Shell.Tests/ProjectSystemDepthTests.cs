using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.ExternalTools;

namespace Codalus.Shell.Tests;

/// <summary>
/// Project-system depth (M26a, ADR 0033) in the headless shell, against the real build host: the Properties window,
/// Show All Files with Include and Exclude, Add New Item, Export Template and the template manager, moves between
/// projects, folder mode with its tasks and launch configurations, and External Tools.
/// </summary>
public sealed class ProjectSystemDepthTests
{
    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>A shell whose <c>dotnet new</c> uses a settings folder of its own (no change to the user's templates).</summary>
    private static ShellFixture Create()
    {
        var userData = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODALUS_TEMPLATE_HIVE", Path.Combine(userData, "template-hive"));
        var f = ShellFixture.Create(userData);
        f.Window.Width = 1400;
        f.Window.Height = 900;
        return f;
    }

    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = Create();
        var slnx = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(slnx))!);
        await f.Shell.OpenSolutionAsync(slnx);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        return (f, Path.GetDirectoryName(slnx)!);
    }

    private static Dictionary<string, PropertyRowViewModel> Rows(ShellFixture f) =>
        f.Shell.PropertiesWindow.Categories.SelectMany(c => c.Rows).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());

    [AvaloniaFact]
    public async Task The_properties_window_edits_a_files_build_action_and_metadata()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        var appsettings = Path.Combine(folder, "src", "App", "appsettings.json");
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PropertiesWindowView>().Any());

        f.Shell.Explorer.Reveal("f:" + appsettings);
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Title == "appsettings.json File Properties" && !f.Shell.PropertiesWindow.IsLoading && Rows(f).ContainsKey("Build Action"), seconds: 60);
        var rows = Rows(f);
        Assert.Equal("None", rows["Build Action"].SelectedOption?.Value);
        Assert.Equal("Copy if newer", rows["Copy to Output Directory"].SelectedOption?.Label);
        Assert.True(rows["Copy to Output Directory"].IsSet);
        Assert.Equal(appsettings, rows["Full Path"].Value);
        f.Shell.PropertiesWindow.SelectedRow = rows["Build Action"];
        Assert.Contains("compiled", f.Shell.PropertiesWindow.Description, StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "m26a-properties");

        // Copy always: the attribute changes in place.
        rows["Copy to Output Directory"].SelectedOption = PropertiesWindowViewModel.CopyToOutputOptions.Single(o => o.Value == "Always");
        await ShellFixture.PumpUntil(() => Read(app).Contains("<None Update=\"appsettings.json\" CopyToOutputDirectory=\"Always\" />", StringComparison.Ordinal), seconds: 60);

        // Content: a Remove for the None glob and a Content item that keeps the metadata.
        await ShellFixture.PumpUntil(() => Rows(f).TryGetValue("Build Action", out var row) && row.Options.Any(o => o.Value == "Content") && !f.Shell.PropertiesWindow.IsLoading, seconds: 60);
        Rows(f)["Build Action"].SelectedOption = Rows(f)["Build Action"].Options.Single(o => o.Value == "Content");
        await ShellFixture.PumpUntil(() => Read(app).Contains("<None Remove=\"appsettings.json\" />", StringComparison.Ordinal) && Read(app).Contains("<Content Include=\"appsettings.json\"", StringComparison.Ordinal), seconds: 60);
        await ShellFixture.PumpUntil(() => Rows(f).TryGetValue("Build Action", out var row) && row.Value == "Content", seconds: 60);

        // A metadata of its own, then the Custom Tool.
        f.Shell.PropertiesWindow.AddMetadataCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "Pack";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Message.Contains("Pack", StringComparison.Ordinal));
        f.Shell.Dialogs.InputText = "true";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => Read(app).Contains("<Pack>true</Pack>", StringComparison.Ordinal), seconds: 60);
        await ShellFixture.PumpUntil(() => Rows(f).ContainsKey("Pack") && !f.Shell.PropertiesWindow.IsLoading, seconds: 60);
        Rows(f)["Custom Tool"].Text = "MyGenerator";
        await ShellFixture.PumpUntil(() => Read(app).Contains("<Generator>MyGenerator</Generator>", StringComparison.Ordinal), seconds: 60);

        // A project and the solution show summaries with links.
        f.Shell.Explorer.Reveal("p:" + app);
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Title == "App Project Properties" && Rows(f).ContainsKey("Output Type"), seconds: 60);
        Assert.Equal("Exe", Rows(f)["Output Type"].Value);
        Assert.Contains(f.Shell.PropertiesWindow.Links, l => l.Title.StartsWith("Open Project Properties", StringComparison.Ordinal));
        f.Shell.Explorer.Reveal("solution");
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Title == "Solution Properties" && Rows(f).ContainsKey("Active Configuration"));
        Assert.Equal("Debug|Any CPU", Rows(f)["Active Configuration"].Value);
    }

    [AvaloniaFact]
    public async Task Show_all_files_includes_and_excludes_files()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var explorer = f.Shell.Explorer;
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        var appDir = Path.GetDirectoryName(app)!;
        var ignored = Path.Combine(appDir, "Excluded", "Ignored.cs");
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId);

        explorer.ShowAllFiles = true;
        await ShellFixture.PumpUntil(() => explorer.Root is { } root && Find(root, "f:" + ignored) is { IsExcluded: true }, seconds: 60);
        explorer.Reveal("f:" + ignored);
        Assert.True(explorer.Selected!.IsExcluded);
        Assert.True(explorer.Selected.IsDimmed);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("explorerItemExcluded"));
        NavigationDepthTests.Screenshot(f, "m26a-show-all-files");

        f.Shell.Run("explorer.includeInProject");
        await ShellFixture.PumpUntil(() => Read(app).Contains("<Compile Include=\"Excluded\\Ignored.cs\" />", StringComparison.Ordinal), seconds: 60);
        await ShellFixture.PumpUntil(() => Find(explorer.Root!, "f:" + ignored) is { IsExcluded: false }, seconds: 60);

        var program = Path.Combine(appDir, "Program.cs");
        explorer.Reveal("f:" + program);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("explorerItemInProject"));
        await f.Shell.Workbench.Commands.ExecuteAsync("explorer.excludeFromProject", TestContext.Current.CancellationToken);
        Assert.Contains("<Compile Remove=\"Program.cs\" />", Read(app), StringComparison.Ordinal);

        // Once the command has run, the tree and the selection's context keys say so after the posted refresh, not a file
        // watcher's debounce later: the command sweep's palette run of it found the item gone in between (0.4.0's release).
        ShellFixture.Pump();
        Assert.True(Find(explorer.Root!, "f:" + program) is { IsExcluded: true }, "the tree shows Program.cs excluded once the command has run");
        Assert.Equal("Program.cs", explorer.Selected?.Name);
        Assert.Equal((true, false), (f.Shell.Workbench.Context.GetValue("explorerItemExcluded"), f.Shell.Workbench.Context.GetValue("explorerItemInProject")));
        Assert.True(f.Shell.Workbench.Commands.IsEnabled("explorer.includeInProject"));
        Assert.False(f.Shell.Workbench.Commands.IsEnabled("explorer.excludeFromProject"));

        // Without Show All Files, the excluded file is gone from the tree.
        explorer.ShowAllFiles = false;
        await ShellFixture.PumpUntil(() => Find(explorer.Root!, "f:" + program) is null);

        // A file moved into another project takes its Update item along (CopyToOutputDirectory).
        var core = Path.Combine(folder, "src", "Core", "Core.csproj");
        await explorer.DropAsync([Path.Combine(appDir, "appsettings.json")], Find(explorer.Root!, "p:" + core)!, copy: false);
        await ShellFixture.PumpUntil(() => Read(core).Contains("<None Update=\"appsettings.json\">", StringComparison.Ordinal), seconds: 60);
        Assert.DoesNotContain("appsettings.json\"", Read(app), StringComparison.Ordinal);
    }

    private static Workbench.Projects.ExplorerNode? Find(Workbench.Projects.ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [AvaloniaFact]
    public async Task Add_new_item_creates_built_in_and_dotnet_new_items()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var appDir = Path.Combine(folder, "src", "App");
        var forms = Path.Combine(appDir, "Forms");

        var add = f.Shell.OpenAddNewItem(Path.Combine(appDir, "App.csproj"), forms)!;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AddNewItemView>().Any());
        await ShellFixture.PumpUntil(() => !add.IsLoading && add.Templates.Any(t => t.Template.Source == Workbench.Projects.ItemTemplateSource.DotnetNew), seconds: 90);
        Assert.Contains(add.Templates, t => t.Template.ShortName == "editorconfig");
        Assert.DoesNotContain(add.Templates, t => t.Template.ShortName == "razorcomponent"); // a Web template in a console app
        add.ShowAll = true;
        Assert.Contains(add.Templates, t => t.Template.ShortName == "razorcomponent");
        add.ShowAll = false;

        add.Search = "class";
        add.Selected = add.Templates.First(t => t.Template.ShortName == "class");
        Assert.Equal("Class1.cs", add.Name);
        add.Name = "Invoice";
        Assert.Contains("namespace App.Forms;", add.Preview, StringComparison.Ordinal);
        Assert.Contains("internal class Invoice", add.Preview, StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "m26a-add-new-item");
        var created = await add.AddAsync();
        Assert.Equal(Path.Combine(forms, "Invoice.cs"), created);
        Assert.Equal("namespace App.Forms;\n\ninternal class Invoice\n{\n}\n", File.ReadAllText(created!));
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Codalus.Shell.Editor.CodeEditorDocument { Title: "Invoice.cs" });
        Assert.DoesNotContain(f.Shell.Layout.Documents, d => d is SolutionManagementDocument { ViewModel: AddNewItemViewModel });

        // A dotnet new template, run by the build host in the project's folder.
        add = f.Shell.OpenAddNewItem(Path.Combine(appDir, "App.csproj"))!;
        await ShellFixture.PumpUntil(() => !add.IsLoading && add.Templates.Any(t => t.Template.ShortName == "editorconfig"), seconds: 90);
        add.Selected = add.Templates.First(t => t.Template.ShortName == "editorconfig");
        add.Name = ".editorconfig";
        Assert.Contains("dotnet new editorconfig", add.Preview, StringComparison.Ordinal);
        created = await add.AddAsync();
        Assert.True(created is not null, add.Error);
        Assert.True(File.Exists(Path.Combine(appDir, ".editorconfig")));

        // A resources file gets its generator.
        add = f.Shell.OpenAddNewItem(Path.Combine(appDir, "App.csproj"))!;
        add.Selected = add.Templates.First(t => t.Template.ShortName == "resx");
        add.Name = "Messages";
        created = await add.AddAsync();
        Assert.Equal(Path.Combine(appDir, "Messages.resx"), created);
        await ShellFixture.PumpUntil(() => Read(Path.Combine(appDir, "App.csproj")).Contains("<Generator>ResXFileCodeGenerator</Generator>", StringComparison.Ordinal), seconds: 60);
        Assert.Contains("<LastGenOutput>Messages.Designer.cs</LastGenOutput>", Read(Path.Combine(appDir, "App.csproj")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Export_template_installs_it_and_the_template_manager_lists_it()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var core = Path.Combine(folder, "src", "Core", "Core.csproj");
        // Revealed again until it's selected: the explorer may still be filling in its tree.
        await ShellFixture.PumpUntil(() =>
        {
            if (f.Shell.Explorer.Selected?.Node.Path != core)
            {
                f.Shell.Explorer.Reveal("p:" + core);
            }

            return f.Shell.Explorer.Selected?.Node.Path == core;
        });

        var export = f.Shell.OpenExportTemplate()!;
        Assert.Equal(("project", "Core", "core"), (export.Kind, export.Name, export.ShortName));
        export.Name = "Acme Core";
        export.ShortName = "acme-core";
        export.Description = "A library.";
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ExportTemplateView>().Any());
        NavigationDepthTests.Screenshot(f, "m26a-export-template");
        await export.ExportAsync();
        Assert.True(export.Succeeded, export.Status + "\n" + export.Output);
        Assert.True(File.Exists(Path.Combine(f.Shell.TemplatesFolder, "acme-core", ".template.config", "template.json")));
        Assert.True(File.Exists(Path.Combine(f.Shell.TemplatesFolder, "acme-core", "Core.csproj")));

        var manager = f.Shell.OpenTemplateManager();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TemplateManagerView>().Any());
        await ShellFixture.PumpUntil(() => !manager.IsBusy && manager.Packages.Any(p => p.Id.Contains("acme-core", StringComparison.OrdinalIgnoreCase)), seconds: 60);
        manager.Selected = manager.Packages.First(p => p.Id.Contains("acme-core", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("acme-core", manager.Selected.TemplatesText, StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "m26a-templates");

        var uninstalling = manager.UninstallAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await uninstalling;
        Assert.DoesNotContain(manager.Packages, p => p.Id.Contains("acme-core", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task A_folder_opens_loads_its_projects_and_runs_its_tasks_and_launch_configurations()
    {
        await using var f = Create();
        var slnx = f.CopyFixture();
        var folder = Path.Combine(Path.GetDirectoryName(slnx)!, "FolderOnly");
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderWorkspaceAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is { IsFolder: true } && f.Shell.Explorer.IsFolderView && f.Shell.Explorer.Rows.Any(r => r.Name == "README.md"), seconds: 60);
        var explorer = f.Shell.Explorer;
        Assert.Equal(["Folder 'FolderOnly'", ".codalus", "scripts", "src", "README.md"], explorer.Rows.Select(r => r.Name));
        Assert.Equal("FolderOnly", f.Shell.Toolbar.SolutionName);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("folderMode"));
        Assert.Equal(folder, f.Shell.Recent.Items[0]);
        var tool = Path.Combine(folder, "src", "Tool", "Tool.csproj");
        explorer.Reveal("f:" + tool);
        Assert.Equal("(not loaded)", explorer.Selected!.Detail);
        NavigationDepthTests.Screenshot(f, "m26a-folder-view");

        // Load All Projects: the solution view of the folder's projects.
        f.Shell.Run("folder.loadAllProjects");
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Projects.Values.All(p => p.State == ProjectStates.Loaded), seconds: 120);
        await ShellFixture.PumpUntil(() => !explorer.IsFolderView && explorer.Rows.Any(r => r.Name == "Tool") && explorer.Rows.Any(r => r.Name == "Lib"));
        NavigationDepthTests.Screenshot(f, "m26a-folder-solution");
        explorer.SetFolderView(true);
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Name == "src"));
        explorer.SetFolderView(false);

        // A task, with its output in Output → Tasks.
        Assert.Equal(0, await f.Shell.RunTaskAsync("sdk version"));
        Assert.Matches(@"(?m)^\d+\.\d+\.\d+", f.Shell.Workbench.Output.GetOrCreate("codalus.tasks").GetText());

        // The launch configurations are the startup list; Ctrl+F5 runs the selected one with its arguments.
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.StartupProjects.Count == 2);
        Assert.Equal(["Tool", "Tool (program)"], f.Shell.Toolbar.StartupProjects.Select(p => p.Name));
        f.Shell.Run("run.startWithoutDebugging");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Output.GetOrCreate("codalus.run.Tool").GetText().Contains("FROM LAUNCH.JSON!", StringComparison.Ordinal), seconds: 240);

        // The program configuration runs its pre-launch task (a build), then the .dll.
        f.Shell.SelectLaunchConfiguration("Tool (program)");
        f.Shell.Run("run.startWithoutDebugging");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Output.GetOrCreate("codalus.run.Tool (program)").GetText().Contains("PROGRAM!", StringComparison.Ordinal), seconds: 240);
        Assert.Contains("build tool", f.Shell.Workbench.Output.GetOrCreate("codalus.tasks").GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task External_tools_become_commands_and_run_with_macros()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var tools = f.Shell.OpenExternalTools();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ExternalToolsView>().Any());
        tools.Add();
        tools.Selected!.Title = "SDK Version";
        tools.Selected.Command = "dotnet";
        tools.Selected.Arguments = "--version";
        tools.Selected.InitialDirectory = "$(SolutionDir)";
        await tools.SaveAsync();
        Assert.False(tools.IsModified);
        NavigationDepthTests.Screenshot(f, "m26a-external-tools");

        var tool = Assert.Single(ExternalToolStore.Load(f.Shell.ExternalToolsFile));
        Assert.NotNull(f.Shell.Workbench.Commands.Find(tool.CommandId));
        Assert.Contains(f.Shell.Workbench.Menus.GetItems("menuBar/tools"), i => i.Command == tool.CommandId && i.Title == "SDK Version");
        f.Shell.Run(tool.CommandId);
        var channel = f.Shell.Workbench.Output.GetOrCreate("codalus.externalTools");
        await ShellFixture.PumpUntil(() => channel.GetText().Contains("exited with code 0", StringComparison.Ordinal), seconds: 60);
        Assert.Matches(@"(?m)^\d+\.\d+\.\d+", channel.GetText());

        // Macros, and the arguments asked first.
        await f.Shell.SaveExternalToolsAsync([tool with { Arguments = "--list-sdks \"$(ProjectFileName)\"", PromptForArguments = true }]);
        f.Shell.Explorer.Reveal("p:" + Path.Combine(folder, "src", "App", "App.csproj"));
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId);
        f.Shell.Run(tool.CommandId);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("--list-sdks \"App.csproj\"", f.Shell.Dialogs.InputText);
        f.Shell.Dialogs.Cancel();
    }
}
