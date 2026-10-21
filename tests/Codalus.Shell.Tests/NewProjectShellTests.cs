using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Tests;

namespace Codalus.Shell.Tests;

/// <summary>
/// New Project in the shell (ADR 0082): its commands, key, menus and setting through the built-in registries with the
/// ids the Templates plugin had; the command waits for the wizard and returns what Create made; the wizard's result
/// opens in the main window without a prompt.
/// </summary>
public sealed class NewProjectShellTests
{
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
