using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Tests;

namespace Codalus.Shell.Tests;

/// <summary>
/// New Project in the shell (ADR 0082): its commands, key, menus and setting through the built-in registries with the
/// ids the Templates plugin had; the command waits for the wizard and returns what Create made; the wizard's result
/// opens in the main window without a prompt.
/// </summary>
public sealed class NewProjectShellTests
{
    /// <summary>
    /// <c>dotnet new</c> as files the build host can load: an empty solution, and a project (a web project for
    /// <c>webapi</c>) for the <c>--framework</c> asked, with a Program file.
    /// </summary>
    private static CliResult StandInDotnet(IReadOnlyList<string> args)
    {
        if (args is not ["new", var template, ..])
        {
            return new CliResult(1, "unexpected: " + string.Join(' ', args));
        }

        var list = args.ToList();
        string? Value(string option) => list.IndexOf(option) is >= 0 and var at && at + 1 < list.Count ? list[at + 1] : null;
        var name = Value("--name")!;
        var output = Value("--output")!;
        Directory.CreateDirectory(output);
        if (template == "sln")
        {
            File.WriteAllText(Path.Combine(output, name + ".slnx"), "<Solution />");
        }
        else
        {
            var sdk = template == "webapi" ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk";
            File.WriteAllText(Path.Combine(output, name + ".csproj"), $"<Project Sdk=\"{sdk}\">\n  <PropertyGroup>\n    <TargetFramework>{Value("--framework") ?? "net10.0"}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(output, "Program.cs"), "System.Console.WriteLine();\n");
        }

        return new CliResult(0, "The template was created successfully.");
    }

    /// <summary>The shell's New Project on the recorded templates and the stand-in CLI, its model driven without a window.</summary>
    private static void UseRecordedTemplates(ShellFixture f, TemplateTestData data)
    {
        var cli = RecordedCli.Sdk11();
        cli.Fallback = StandInDotnet;
        f.Shell.ProjectTemplates = new ProjectTemplateCatalog(cli, new FixedCacheLocator(data.Cache11), () => new DateOnly(2026, 10, 21));
        f.Shell.NewProjectCli = cli;
        f.Shell.NewProjects = new NewProjectService(cli, new PathRules(IsWindows: false, LongPathsEnabled: true));
        f.Shell.NewProjectWindowFactory = null;
        f.Shell.NewProjectIsWindows = false;
    }

    private static async Task<NewProjectViewModel> OpenedAsync(ShellFixture f)
    {
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 });
        var model = f.Shell.NewProjectModel!;
        model.SearchDelay = TimeSpan.Zero;
        model.ValidationDelay = TimeSpan.Zero;
        return model;
    }

    private static async Task RestoreOnOpenOffAsync(ShellFixture f)
    {
        using var off = JsonDocument.Parse("false");
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, off.RootElement.Clone(), SettingScope.User, TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task The_commands_menus_key_and_setting_are_the_shells()
    {
        await using var f = ShellFixture.Create();
        var workbench = f.Shell.Workbench;
        foreach (var id in new[] { "templates.newProject", "templates.newSolution", "solution.addNewProject" })
        {
            Assert.Equal(ContributionSource.Shell, workbench.Commands.Find(id)!.Source);
        }

        Assert.Equal("New Project…", workbench.Commands.Find("templates.newProject")!.Title);
        Assert.Contains(BuiltIns.ParseDefaultKeybindings(PlatformKind.Linux), r => r.Command == "templates.newProject" && r.Keys?.ToString() == "ctrl+shift+n");
        Assert.Contains(BuiltIns.ParseDefaultKeybindings(PlatformKind.MacOS), r => r.Command == "templates.newProject" && r.Keys?.ToString() == "shift+meta+n");
        Assert.Contains(workbench.Menus.Rules, r => r.Location == BuiltIns.FileNewMenu && r.Command == "templates.newProject");
        Assert.Contains(workbench.Menus.Rules, r => r.Location == BuiltIns.FileNewMenu && r.Command == "templates.newSolution");
        var add = Assert.Single(workbench.Menus.Rules, r => r.Location == Views.SolutionExplorerView.AddMenuLocation && r.Command == "solution.addNewProject");
        Assert.Equal("New Project…", add.Title);
        Assert.Contains(workbench.Menus.Rules, r => r.Location == MenuLocations.MenuBar("project") && r.Command == "solution.addNewProject");

        var setting = workbench.Settings.Find(ShellHost.DefaultLocationSetting)!;
        Assert.Equal((ContributionSource.Shell, Workbench.Settings.SettingScope.User), (setting.Source, setting.Scope));
        Assert.DoesNotContain(f.Shell.Plugins.Plugins, p => p.Id == "codalus.templates");

        // Enablement: New Project anywhere but in a remote window; Add New Project with a solution (not a lone project file).
        Assert.True(workbench.Commands.IsEnabled("templates.newProject"));
        Assert.False(workbench.Commands.IsEnabled("solution.addNewProject"));
        workbench.Context.Set("solutionOpen", true);
        Assert.True(workbench.Commands.IsEnabled("solution.addNewProject"));
        workbench.Context.Set("solutionIsProjectFile", true);
        Assert.False(workbench.Commands.IsEnabled("solution.addNewProject"));
        workbench.Context.Set("solutionIsProjectFile", false);
        workbench.Context.Set("remoteConnected", true);
        Assert.False(workbench.Commands.IsEnabled("templates.newProject"));
        Assert.False(workbench.Commands.IsEnabled("templates.newSolution"));
        Assert.False(workbench.Commands.IsEnabled("solution.addNewProject"));
        workbench.Context.Set("remoteConnected", false);
        workbench.Context.Set("solutionOpen", false);
    }

    [AvaloniaFact]
    public async Task The_command_waits_for_the_wizard_and_cancel_returns_null()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        var cli = RecordedCli.Sdk11();
        f.Shell.ProjectTemplates = new ProjectTemplateCatalog(cli, new FixedCacheLocator(data.Cache11));
        f.Shell.NewProjectCli = cli;

        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 });
        var model = f.Shell.NewProjectModel!;
        Assert.False(command.IsCompleted); // not modal, and nothing blocked: the command waits for the wizard

        // Running it again activates the open wizard.
        var again = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => true);
        Assert.Same(model, f.Shell.NewProjectModel);

        model.Cancel();
        Assert.Null(await command);
        Assert.Null(await again);
        Assert.False(f.Shell.IsNewProjectOpen);

        // The start window's Create a new project runs the command with fromStartWindow: Back returns there.
        f.Shell.ShowStartWindow();
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.CreateProjectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false } && !f.Shell.IsStartWindowOpen);
        Assert.True(f.Shell.NewProjectModel!.Context.FromStartWindow);
        f.Shell.NewProjectModel.GoBack();
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen && !f.Shell.IsNewProjectOpen);

        // Another kind of New Project replaces the open wizard (which returns nothing); the same kind comes back to it.
        var project = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Context.Mode: NewProjectMode.Project });
        var solution = f.Shell.Workbench.Commands.ExecuteAsync("templates.newSolution", TestContext.Current.CancellationToken);
        Assert.Null(await project.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Context.Mode: NewProjectMode.Solution, Page: NewProjectPage.Configure });
        var blank = f.Shell.NewProjectModel!;
        var solutionAgain = f.Shell.Workbench.Commands.ExecuteAsync("templates.newSolution", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => true);
        Assert.Same(blank, f.Shell.NewProjectModel);
        Assert.False(solution.IsCompleted);
        blank.Cancel();
        Assert.Null(await solution);
        Assert.Null(await solutionAgain);
    }

    [AvaloniaFact]
    public async Task Enable_container_support_adds_the_dockerfile_once_the_project_has_loaded()
    {
        // The build host answers AddProject before it has evaluated the project; container support needs the evaluated
        // project (its framework, that it's a web project). Restore on open is off: nothing reaches a package feed.
        await using var f = ShellFixture.Create();
        await RestoreOnOpenOffAsync(f);
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var location = Directory.CreateDirectory(Path.Combine(f.UserData, "repos")).FullName;
        Codalus.Tests.FixtureSdk.Pin(location);
        f.Shell.NewProjectHistory.AddRecentLocation(location);

        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var model = await OpenedAsync(f);
        model.SelectedTemplate = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Web.WebApi|C#");
        await model.WaitForTemplateAsync();
        await model.GoForwardAsync();
        model.ProjectName = "Api";
        model.TrustLocation = true;
        await ShellFixture.PumpUntil(() => model.CanGoNext);
        await model.GoForwardAsync();
        model.SelectedFramework = model.Frameworks.Single(c => c.Moniker == "net11.0");
        Assert.True(model.ShowDocker);
        model.Docker = true;
        Assert.True(model.Docker);
        await ShellFixture.PumpUntil(() => model.CanCreate);
        _ = model.GoForwardAsync();

        var folder = Path.Combine(location, "Api", "Api");
        var result = await command.WaitAsync(TimeSpan.FromMinutes(4), TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(folder, "Api.csproj"), result!.Value.GetString());
        var notes = string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message));
        Assert.True(File.Exists(Path.Combine(folder, "Dockerfile")), "no Dockerfile; notifications: " + notes);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:11.0", File.ReadAllText(Path.Combine(folder, "Dockerfile")), StringComparison.Ordinal); // a web project's image
        Assert.Contains("Container (Dockerfile)", File.ReadAllText(Path.Combine(folder, "Properties", "launchSettings.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(location, "Api", ".dockerignore")));
        Assert.DoesNotContain("Select a project to add Docker support to.", notes, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Add_new_project_goes_into_the_selected_solution_folder_and_closes_with_the_solution()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var solution = f.CopyFixture();
        Assert.True(await f.Shell.OpenSolutionAsync(solution, trusted: false)); // the structure is enough: no MSBuild
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Any(r => r.Node is { Kind: ExplorerNodeKind.SolutionFolder, Name: "tests" }), seconds: 60);

        // Solution Explorer's tests folder selected: Add → New Project goes there, as the Explorer names the folder.
        var tests = f.Shell.Explorer.Rows.First(r => r.Node is { Kind: ExplorerNodeKind.SolutionFolder, Name: "tests" });
        f.Shell.Explorer.Selected = tests;
        var folderId = tests.Node.SolutionFolderId;
        Assert.NotNull(folderId);
        var command = f.Shell.Workbench.Commands.ExecuteAsync("solution.addNewProject", TestContext.Current.CancellationToken);
        var model = await OpenedAsync(f);
        Assert.Equal((NewProjectMode.Add, folderId), (model.Context.Mode, model.Context.SolutionFolderId));
        f.Shell.Explorer.Selected = null; // what counts is the folder selected when it opened
        model.SelectedTemplate = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Library|C#");
        await model.WaitForTemplateAsync();
        await model.GoForwardAsync();
        Assert.Equal("Adds the project to the solution \"ProjectSystem\", in its folder \"tests\".", model.AddsTo);
        model.ProjectName = "Extra";
        await ShellFixture.PumpUntil(() => model.CanGoNext || model.CanCreate);
        await model.GoForwardAsync();
        await ShellFixture.PumpUntil(() => model.CanCreate);
        _ = model.GoForwardAsync();

        var project = Path.Combine(Path.GetDirectoryName(solution)!, "Extra", "Extra.csproj");
        var result = await command.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(project, result!.Value.GetString());
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution!.Projects.Any(p => Path.GetFullPath(p.Path) == project), seconds: 60);
        Assert.Equal(folderId, f.Shell.Solutions.Solution!.Projects.Single(p => Path.GetFullPath(p.Path) == project).Parent);
        var slnx = XDocument.Load(solution);
        var inTests = slnx.Root!.Elements("Folder").Single(e => (string?)e.Attribute("Name") == "/tests/").Elements("Project").Select(e => ((string)e.Attribute("Path")!).Replace('\\', '/'));
        Assert.Contains("Extra/Extra.csproj", inTests);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Any(r => r.Node.Path is { } path && Path.GetFullPath(path) == project), seconds: 30);

        // Add New Project goes into the solution that was open: closing it closes the wizard, which returns nothing.
        var again = f.Shell.Workbench.Commands.ExecuteAsync("solution.addNewProject", TestContext.Current.CancellationToken);
        await OpenedAsync(f);
        await f.Shell.CloseSolutionAsync();
        Assert.Null(await again.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(f.Shell.IsNewProjectOpen);
    }

    [AvaloniaFact]
    public async Task Another_solution_opened_behind_the_wizard_isnt_added_to()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var first = f.CopyFixture();
        var other = Path.Combine(Directory.CreateDirectory(Path.Combine(f.UserData, "Other")).FullName, "Other.slnx");
        File.WriteAllText(other, "<Solution />");
        Assert.True(await f.Shell.OpenSolutionAsync(first, trusted: false));

        // New Project with ProjectSystem open offers Add to solution; opening Other behind it takes that offer back.
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var model = await OpenedAsync(f);
        model.SelectedTemplate = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Library|C#");
        await model.WaitForTemplateAsync();
        await model.GoForwardAsync();
        model.SelectedSolutionChoice = model.SolutionChoices.Single(c => c.Mode == SolutionMode.AddToSolution);
        Assert.False(model.CreatesSolution);
        Assert.True(await f.Shell.OpenSolutionAsync(other, trusted: false));
        await ShellFixture.PumpUntil(() => model.CreatesSolution);
        Assert.Equal(["Create new solution"], model.SolutionChoices.Select(c => c.Label));
        model.Cancel();
        Assert.Null(await command);

        // A Create that was adding to ProjectSystem and finishes with Other open: made, not added to Other.
        var library = (await f.Shell.ProjectTemplates.LoadAsync(TestContext.Current.CancellationToken)).Single(r => r.Id == "dotnet:Microsoft.Common.Library|C#");
        var made = Path.Combine(Path.GetDirectoryName(first)!, "Late", "Late.csproj");
        StandInDotnet(["new", "classlib", "--name", "Late", "--output", Path.GetDirectoryName(made)!]);
        var request = new NewProjectRequest { Group = library, ProjectName = "Late", Location = Path.GetDirectoryName(first)!, SolutionMode = SolutionMode.AddToSolution };
        var completion = new NewProjectCompletion(request, NewProjectPlan.Create(request), new NewProjectResult(null, made, [], [], "log"), false, null) { WorkspacePath = RealPath.Resolve(first) };
        Assert.Equal(made, await f.Shell.FinishNewProjectAsync(completion));
        await ShellFixture.PumpUntil(() => true);
        Assert.DoesNotContain(f.Shell.Solutions.Solution!.Projects, p => Path.GetFileName(p.Path) == "Late.csproj");
        Assert.Contains(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("but didn't add it: ProjectSystem.slnx isn't open any more.", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Create_into_an_untrusted_location_opens_it_without_asking()
    {
        // The real SDK, in a template hive of its own (the user's templates untouched), with --no-restore.
        var userData = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(TemplateService.HiveVariable, Path.Combine(userData, "template-hive"));
        await using var f = ShellFixture.Create(userData);
        var location = Path.Combine(userData, "repos");
        var dialogs = false;
        f.Shell.Dialogs.PropertyChanged += (_, e) => dialogs |= e.PropertyName == nameof(DialogViewModel.IsOpen) && f.Shell.Dialogs.IsOpen;

        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 }, seconds: 120);
        var model = f.Shell.NewProjectModel!;
        model.ValidationDelay = TimeSpan.Zero;
        model.SelectedTemplate = model.Templates.First(r => r.Group.ShortName == "console" && r.Group.Language == "C#");
        await model.GoForwardAsync();
        model.Location = location;
        model.ProjectName = "Hello";
        model.SolutionName = "HelloSolution";
        Assert.False(model.TrustLocation); // unchecked by default
        await ShellFixture.PumpUntil(() => model.CanGoNext, seconds: 60);
        await model.GoForwardAsync();
        await ShellFixture.PumpUntil(() => model.CanCreate);
        _ = model.GoForwardAsync();

        var project = Path.Combine(location, "HelloSolution", "Hello", "Hello.csproj");
        var result = await command.WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal(project, result!.Value.GetString());
        Assert.False(f.Shell.IsNewProjectOpen); // closed before the solution opened
        Assert.False(dialogs); // no trust prompt
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is { Projects.Length: 1 } solution && solution.Path.StartsWith(Path.Combine(location, "HelloSolution", "HelloSolution.sln"), StringComparison.Ordinal), seconds: 120);
        Assert.False(f.Shell.Solutions.Solution!.Trusted);
        Assert.Equal(project, Path.GetFullPath(f.Shell.Solutions.Solution.Projects[0].Path));
        Assert.False(f.Shell.Trust.IsTrusted(location));
        Assert.Equal("dotnet:Microsoft.Common.Console|C#", f.Shell.NewProjectHistory.RecentTemplates[0].Id);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(location, "HelloSolution", "Hello", "Program.cs"), seconds: 60);
    }
}
