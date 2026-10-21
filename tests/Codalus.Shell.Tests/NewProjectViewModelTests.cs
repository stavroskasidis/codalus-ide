using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Tests;

namespace Codalus.Shell.Tests;

/// <summary>
/// New Project's three pages (ADR 0082) on the recorded SDK 10.0 and 11.0 templates, with a stand-in CLI that writes what
/// <c>dotnet new</c> would: search and filters, recent templates, the pages and their keys, names and locations, the
/// options per framework, the host options, Create, packs, Add New Project and New Solution.
/// </summary>
public sealed class NewProjectViewModelTests : IDisposable
{
    private readonly TemplateTestData _data = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-new-project-vm-" + Guid.NewGuid().ToString("N"));
    private readonly RecordedCli _cli = RecordedCli.Sdk11();
    private readonly List<string> _trusted = [];
    private readonly List<string> _output = [];
    private int _backToStart;

    public NewProjectViewModelTests()
    {
        Directory.CreateDirectory(_root);
        History = new NewProjectHistory(Path.Combine(_root, "state", "new-project.json"));
        History.AddRecentLocation(Location);
        _cli.Fallback = StandInDotnet;
    }

    private string Location => Path.Combine(_root, "repos");

    private NewProjectHistory History { get; }

    public void Dispose()
    {
        _data.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><c>dotnet new sln</c> and <c>dotnet new &lt;template&gt;</c> as files: the solution, or a project with a Program file.</summary>
    private static CliResult StandInDotnet(IReadOnlyList<string> args)
    {
        if (args is not ["new", var template, ..])
        {
            return new CliResult(1, "unexpected: " + string.Join(' ', args));
        }

        var list = args.ToList();
        var name = list[list.IndexOf("--name") + 1];
        var output = list[list.IndexOf("--output") + 1];
        Directory.CreateDirectory(output);
        if (template == "sln")
        {
            File.WriteAllText(Path.Combine(output, name + ".slnx"), "<Solution />");
        }
        else
        {
            var vb = list.Contains("VB");
            File.WriteAllText(Path.Combine(output, name + (vb ? ".vbproj" : ".csproj")), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(output, vb ? "Program.vb" : "Program.cs"), string.Empty);
        }

        return new CliResult(0, $"The template was created successfully. ({string.Join(' ', args)})");
    }

    private NewProjectViewModel Model(NewProjectContext? context = null)
    {
        var services = new NewProjectServices
        {
            Catalog = new ProjectTemplateCatalog(_cli, new FixedCacheLocator(_data.Cache11), () => new DateOnly(2026, 10, 21)),
            Creator = new NewProjectService(_cli, new PathRules(IsWindows: false, LongPathsEnabled: true)),
            History = History,
            Cli = _cli,
            IsTrusted = path => _trusted.Any(t => path.StartsWith(t, StringComparison.Ordinal)),
            Trust = _trusted.Add,
            Rules = new PathRules(IsWindows: false, LongPathsEnabled: true),
            ShowOutput = _output.Add,
            BackToStartWindow = () => _backToStart++,
        };
        return new NewProjectViewModel(services, context ?? new NewProjectContext { IsWindows = false })
        {
            SearchDelay = TimeSpan.Zero,
            ValidationDelay = TimeSpan.Zero,
        };
    }

    private static async Task SelectAsync(NewProjectViewModel model, string id)
    {
        model.SelectedTemplate = model.Templates.Single(r => r.Id == id);
        await model.WaitForTemplateAsync();
    }

    private static async Task SettleAsync(NewProjectViewModel model)
    {
        await model.Validation;
        await ShellFixture.PumpUntil(() => !model.IsValidating);
    }

    [AvaloniaFact]
    public async Task Search_and_filters_narrow_the_list_and_clear_all_resets()
    {
        var model = Model();
        await model.LoadAsync();
        Assert.False(model.IsLoading);
        Assert.Equal("Create a new project", model.PageTitle);
        Assert.Equal("New Project", model.WindowTitle);
        var all = model.Templates.Count;
        Assert.Equal(13, all); // 8 templates, 2 .NET Framework, 2 packs, Blank Solution
        Assert.Equal(["All languages", "C#", "F#", "Visual Basic"], model.Languages.Select(l => l.Label));
        Assert.False(model.HasFilter);

        // A match in the name first, then in the tags: xUnit's Web tag comes after the web templates.
        model.Search = "web";
        Assert.Equal(["ASP.NET Core Web API", "Blazor Web App"], model.Templates.Take(2).Select(r => r.Name));
        Assert.Equal([new HighlightRun("ASP.NET Core ", false), new HighlightRun("Web", true), new HighlightRun(" API", false)], model.Templates[0].NameRuns);
        Assert.Contains(model.Templates, r => r.Name == "xUnit Test Project");
        Assert.True(model.HasFilter);

        model.Search = string.Empty;
        model.SelectedLanguage = model.Languages.Single(l => l.Value == "VB");
        Assert.Equal(["Console App", "Blank Solution"], model.Templates.Where(r => r.Group.Kind is ProjectTemplateKind.DotnetNew or ProjectTemplateKind.BlankSolution).Select(r => r.Name));
        model.SelectedPlatform = model.Platforms.Single(p => p.Value == "Windows");
        model.SelectedProjectType = model.ProjectTypes.Single(p => p.Value == "Desktop");
        Assert.DoesNotContain(model.Templates, r => r.Group.Kind == ProjectTemplateKind.DotnetNew); // WPF is in C# only here
        Assert.Equal(("VB", "Windows", "Desktop"), (History.LanguageFilter, History.PlatformFilter, History.ProjectTypeFilter));

        model.Search = "nothing like it";
        Assert.True(model.IsEmpty);
        model.ClearAllCommand.Execute(null);
        Assert.Equal(all, model.Templates.Count);
        Assert.False(model.HasFilter);
        Assert.Equal((string?)null, History.LanguageFilter);

        // Off Windows, Windows-only templates say so.
        Assert.True(model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.WPF|C#").ShowWindowsOnly);
        Assert.Contains("Windows only", model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.WPF|C#").AccessibleName, StringComparison.Ordinal);
        Assert.False(model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Console|C#").ShowWindowsOnly);
    }

    [AvaloniaFact]
    public async Task Recent_templates_pin_and_remove()
    {
        History.AddRecentTemplate("dotnet:Microsoft.Test.xUnit|C#");
        History.AddRecentTemplate("dotnet:Microsoft.Common.Console|C#");
        History.AddRecentTemplate("pack:Avalonia.Templates"); // a pack isn't a recent template
        var model = Model();
        await model.LoadAsync();
        Assert.Equal(["Console App", "xUnit Test Project"], model.RecentTemplates.Select(r => r.Name));
        var xunit = model.RecentTemplates[1];
        Assert.Equal("Pin to List", xunit.PinTitle);
        xunit.PinCommand.Execute(null);
        Assert.Equal(["xUnit Test Project", "Console App"], model.RecentTemplates.Select(r => r.Name));
        Assert.True(model.RecentTemplates[0].IsPinned);
        Assert.Equal("Unpin from List", model.RecentTemplates[0].PinTitle);
        model.RecentTemplates[1].RemoveCommand.Execute(null);
        Assert.Equal(["xUnit Test Project"], model.RecentTemplates.Select(r => r.Name));
        Assert.True(History.RecentTemplates.Single(t => t.Id == "dotnet:Microsoft.Test.xUnit|C#").Pinned);
    }

    [AvaloniaFact]
    public async Task The_pages_follow_back_next_and_the_names()
    {
        var model = Model();
        await model.LoadAsync();
        Assert.False(model.CanGoBack); // not opened from the start window
        Assert.False(model.CanGoNext); // nothing selected
        await SelectAsync(model, "dotnet:Microsoft.Common.Console|C#");
        Assert.True(model.CanGoNext);
        Assert.True(model.IsNextVisible);

        await model.GoForwardAsync(); // Enter, Alt+Right
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Assert.Equal("Configure your new project", model.PageTitle);
        Assert.Equal(("ConsoleApp1", "ConsoleApp1", Location), (model.ProjectName, model.SolutionName, model.Location));
        Assert.False(model.ShowSolutionChoice); // nothing is open
        Assert.True(model.ShowTrust);
        await SettleAsync(model);
        Assert.True(model.CanGoNext);
        Assert.Equal($"Project will be created in \"{Path.Combine(Location, "ConsoleApp1", "ConsoleApp1")}{Path.DirectorySeparatorChar}\"", model.CreatedIn);

        // The solution name follows the project's until it's edited; the same directory disables it.
        model.ProjectName = "Hello";
        Assert.Equal("Hello", model.SolutionName);
        model.SolutionName = "Sol";
        model.ProjectName = "Hello2";
        Assert.Equal("Sol", model.SolutionName);
        model.SameDirectory = true;
        Assert.False(model.IsSolutionNameEnabled);
        Assert.Equal($"Project will be created in \"{Path.Combine(Location, "Hello2")}{Path.DirectorySeparatorChar}\"", model.CreatedIn);
        model.SameDirectory = false;

        // A reserved name stops Next; a fixed one lets it go on, once the disk check has run.
        model.ProjectName = "CON";
        Assert.Equal("\"CON\" is a device name Windows reserves. Choose another name.", model.NameError);
        Assert.False(model.CanGoNext);
        model.ProjectName = "Hello";
        await SettleAsync(model);
        Assert.Null(model.NameError);
        Assert.True(model.CanGoNext);

        // An existing folder that isn't empty.
        Directory.CreateDirectory(Path.Combine(Location, "Sol", "Hello"));
        File.WriteAllText(Path.Combine(Location, "Sol", "Hello", "x.txt"), "x");
        model.Location = Location + Path.DirectorySeparatorChar;
        await SettleAsync(model);
        Assert.NotNull(model.TargetError);
        Assert.False(model.CanGoNext);
        model.ProjectName = "Hello3";
        await SettleAsync(model);
        Assert.Null(model.TargetError);

        await model.GoForwardAsync();
        Assert.Equal(NewProjectPage.Additional, model.Page);
        Assert.Equal([".NET 11.0 (Preview)", ".NET 10.0 (Long Term Support)"], model.Frameworks.Select(f => f.Label));
        Assert.Equal("net10.0", model.SelectedFramework!.Moniker);
        Assert.Equal(["UseProgramMain", "NativeAot"], model.Options.Select(o => o.Name));
        Assert.Equal("Do not use _top-level statements", model.Options[0].Label);
        Assert.True(model.IsCreateVisible);
        Assert.False(model.ShowHttps || model.ShowAuth || model.ShowDocker || model.ShowAspire);

        model.GoBack(); // Alt+Left
        Assert.Equal(NewProjectPage.Configure, model.Page);
        model.GoBack();
        Assert.Equal(NewProjectPage.Choose, model.Page);
        Assert.False(model.CanGoBack);
    }

    [AvaloniaFact]
    public async Task Back_on_the_first_page_returns_to_the_start_window()
    {
        var model = Model(new NewProjectContext { FromStartWindow = true, IsWindows = false });
        NewProjectCompletion? finished = null;
        var done = false;
        model.Finished += (_, completion) => (finished, done) = (completion, true);
        await model.LoadAsync();
        Assert.True(model.CanGoBack);
        Assert.Equal("Back to the start window (Alt+Left)", model.BackToolTip);
        model.GoBack();
        Assert.True(done);
        Assert.Null(finished);
        Assert.Equal(1, _backToStart);
    }

    [AvaloniaFact]
    public async Task Additional_information_rebuilds_with_the_framework()
    {
        var model = Model();
        await model.LoadAsync();
        await SelectAsync(model, "dotnet:Microsoft.Test.xUnit|C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        await model.GoForwardAsync();
        Assert.Equal("net10.0", model.SelectedFramework!.Moniker);
        Assert.DoesNotContain(model.Options, o => o.Name is "XUnitVersion" or "TestRunner");

        model.SelectedFramework = model.Frameworks.Single(f => f.Moniker == "net11.0");
        var version = Assert.IsType<ChoiceOptionViewModel>(model.Options.Single(o => o.Name == "XUnitVersion"));
        var runner = model.Options.Single(o => o.Name == "TestRunner");
        Assert.Equal("v2", version.SymbolValue);
        Assert.False(runner.IsEnabled); // (XUnitVersion == v3)
        version.SymbolValue = "v3";
        Assert.True(runner.IsEnabled);

        // Back to 10.0: its member has neither option, and the values don't reach the CLI.
        model.SelectedFramework = model.Frameworks.Single(f => f.Moniker == "net10.0");
        Assert.DoesNotContain(model.Options, o => o.Name is "XUnitVersion" or "TestRunner");
        Assert.DoesNotContain("--test-runner", NewProjectPlan.Create(model.Request()).Arguments(new PathRules(false, true)));
    }

    [AvaloniaFact]
    public async Task Blazor_conditions_authentication_and_the_trusted_options()
    {
        var model = Model();
        await model.LoadAsync();
        await SelectAsync(model, "dotnet:Microsoft.Web.Blazor|C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        await model.GoForwardAsync();
        Assert.Equal(["InteractivityPlatform", "InteractivityLocation", "IncludeSampleContent", "UseProgramMain", "LocalhostTld"], model.Options.Select(o => o.Name));
        var platform = Assert.IsType<ChoiceOptionViewModel>(model.Options[0]);
        var location = model.Options[1];
        Assert.True(location.IsEnabled);
        platform.SymbolValue = "None";
        Assert.False(location.IsEnabled);
        Assert.True(Assert.IsType<BoolOptionViewModel>(model.Options[2]).IsChecked); // Include sample pages

        // The IDE's own options take the first free letters.
        Assert.Equal(("C_onfigure for HTTPS", "_Authentication type", "_Enable container support", "E_nlist in Aspire orchestration"), (model.HttpsLabel, model.AuthLabel, model.DockerLabel, model.AspireLabel));

        // Individual Accounts needs HTTPS.
        Assert.Equal(["None", "Individual Accounts"], model.AuthChoices.Select(a => a.Label));
        Assert.Equal("None", model.SelectedAuth!.Value);
        model.Https = false;
        Assert.False(model.Https);
        model.SelectedAuth = model.AuthChoices.Single(a => a.Value == "Individual");
        Assert.True(model.Https);
        Assert.False(model.IsHttpsEnabled);
        Assert.Equal("Individual Accounts needs HTTPS.", model.HttpsToolTip);

        // Container support and Aspire need a trusted location. Blazor's 10.0 template doesn't say it supports containers;
        // its 11.0 template does.
        Assert.False(model.ShowDocker);
        Assert.True(model.ShowAspire);
        model.SelectedFramework = model.Frameworks.Single(f => f.Moniker == "net11.0");
        Assert.True(model.ShowDocker);
        Assert.Equal("Individual", model.SelectedAuth!.Value); // kept across members
        Assert.False(model.AreTrustedOptionsEnabled);
        Assert.Equal("Needs a trusted location (previous page).", model.TrustedOptionsNote);
        model.Docker = true;
        Assert.False(model.Docker);
        model.TrustLocation = true;
        Assert.True(model.AreTrustedOptionsEnabled);
        Assert.True(model.Docker);
        Assert.Null(model.TrustedOptionsNote);
    }

    [AvaloniaFact]
    public async Task Create_writes_the_files_trusts_the_location_and_remembers()
    {
        var model = Model();
        NewProjectCompletion? finished = null;
        model.Finished += (_, completion) => finished = completion;
        await model.LoadAsync();
        await SelectAsync(model, "dotnet:Microsoft.Common.Console|C#");
        await model.GoForwardAsync();
        model.ProjectName = "Hello";
        model.SolutionName = "Sol";
        model.TrustLocation = true;
        await SettleAsync(model);
        await model.GoForwardAsync();
        model.Options[0].SymbolValue = "true"; // Do not use top-level statements
        Assert.True(model.CanCreate);
        await model.GoForwardAsync();

        Assert.NotNull(finished);
        Assert.Equal(Path.Combine(Location, "Sol", "Hello", "Hello.csproj"), finished.Result.ProjectPath);
        Assert.Equal(Path.Combine(Location, "Sol", "Sol.slnx"), finished.Result.SolutionPath);
        Assert.Equal([Path.Combine(Location, "Sol", "Hello", "Program.cs")], finished.Result.FilesToOpen);
        Assert.True(finished.Trusted);
        Assert.Equal([Location], _trusted);
        var command = Assert.Single(_cli.Calls, c => c is ["new", "console", ..]);
        Assert.Contains("--use-program-main", command);
        Assert.Contains("--no-restore", command);

        Assert.Equal("dotnet:Microsoft.Common.Console|C#", History.RecentTemplates[0].Id);
        Assert.Equal(Location, History.RecentLocations[0]);
        Assert.Equal("net10.0", History.Framework("dotnet:Microsoft.Common.Console|C#"));
        Assert.Equal("true", History.Value("shared:Microsoft", "UseProgramMain"));

        // The next New Project starts with what was remembered.
        var next = Model();
        await next.LoadAsync();
        Assert.Equal("Console App", next.RecentTemplates[0].Name);
        await SelectAsync(next, "dotnet:Microsoft.Common.Console|C#");
        await next.GoForwardAsync();
        await SettleAsync(next);
        await next.GoForwardAsync();
        Assert.True(Assert.IsType<BoolOptionViewModel>(next.Options[0]).IsChecked);
    }

    [AvaloniaFact]
    public async Task A_failed_create_stays_with_the_error_and_its_output()
    {
        _cli.Fallback = args => args is ["new", "sln", ..] ? StandInDotnet(args) : new CliResult(103, "Error: The template couldn't be created.\n");
        var model = Model();
        var finished = false;
        model.Finished += (_, _) => finished = true;
        await model.LoadAsync();
        await SelectAsync(model, "dotnet:Microsoft.Common.Console|C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        await model.GoForwardAsync();
        await model.GoForwardAsync();

        Assert.False(finished);
        Assert.False(model.IsCreating);
        Assert.Equal("Couldn't create the project: Error: The template couldn't be created.", model.CreateError);
        Assert.True(model.ShowOutputCommand.CanExecute(null));
        model.ShowOutputCommand.Execute(null);
        Assert.Contains("dotnet new console", Assert.Single(_output), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Location, "ConsoleApp1"))); // what it created is gone
    }

    [AvaloniaFact]
    public async Task A_missing_template_pack_installs_from_the_list()
    {
        var model = Model();
        await model.LoadAsync();
        await SelectAsync(model, "pack:Avalonia.Templates");
        Assert.True(model.IsInstallVisible);
        Assert.False(model.CanGoNext);
        Assert.Equal("Installs Avalonia.Templates from nuget.org with dotnet new install.", model.SelectedTemplate!.InstallDescription);
        await model.GoForwardAsync();
        Assert.Equal(["Avalonia.Templates"], _cli.Installed);
        Assert.Equal(NewProjectPage.Choose, model.Page);
    }

    [AvaloniaFact]
    public async Task Blank_solution_has_no_project_and_no_third_page()
    {
        var model = Model(new NewProjectContext { Mode = NewProjectMode.Solution, IsWindows = false });
        NewProjectCompletion? finished = null;
        model.Finished += (_, completion) => finished = completion;
        await model.LoadAsync(); // New Solution starts on Blank Solution's Configure page
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Assert.True(model.IsBlankSolution);
        Assert.False(model.ShowProjectName);
        Assert.False(model.ShowSameDirectory);
        Assert.False(model.HasAdditionalPage);
        Assert.Equal("Solution1", model.SolutionName);
        await SettleAsync(model);
        Assert.True(model.IsCreateVisible);
        await model.GoForwardAsync();
        Assert.Equal(Path.Combine(Location, "Solution1", "Solution1.slnx"), finished!.Result.SolutionPath);
        Assert.Null(finished.Result.ProjectPath);
    }

    [AvaloniaFact]
    public async Task Add_new_project_goes_into_the_solution_folder_captured_at_open()
    {
        var solution = Path.Combine(_root, "Existing");
        Directory.CreateDirectory(solution);
        var model = Model(new NewProjectContext
        {
            Mode = NewProjectMode.Add,
            Workspace = NewProjectWorkspace.Solution,
            WorkspacePath = Path.Combine(solution, "Existing.slnx"),
            WorkspaceDirectory = solution,
            WorkspaceName = "Existing",
            WorkspaceTrusted = true,
            SolutionFolderId = "/src/",
            SolutionFolderName = "src",
            IsWindows = false,
        });
        NewProjectCompletion? finished = null;
        model.Finished += (_, completion) => finished = completion;
        Assert.Equal("Add New Project", model.WindowTitle);
        await model.LoadAsync();
        Assert.Equal("Add a new project", model.PageTitle);
        await SelectAsync(model, "dotnet:Microsoft.Common.Library|C#");
        await model.GoForwardAsync();
        Assert.Equal(solution, model.Location);
        Assert.False(model.ShowSolutionChoice);
        Assert.False(model.ShowTrust);
        Assert.False(model.ShowSolutionName);
        Assert.Equal("Adds the project to the solution \"Existing\", in its folder \"src\".", model.AddsTo);
        await SettleAsync(model);
        await model.GoForwardAsync();
        Assert.True(model.AreTrustedOptionsEnabled); // the open solution's trust
        await model.GoForwardAsync();

        Assert.Equal("/src/", finished!.SolutionFolderId);
        Assert.Null(finished.Result.SolutionPath);
        Assert.Equal(Path.Combine(solution, "ClassLibrary1", "ClassLibrary1.csproj"), finished.Result.ProjectPath);
        Assert.Empty(_trusted);
        Assert.DoesNotContain(_cli.Calls, c => c is ["new", "sln", ..]);
    }

    [AvaloniaFact]
    public async Task With_a_solution_open_new_mode_offers_add_to_solution()
    {
        var solution = Path.Combine(_root, "Open");
        var model = Model(new NewProjectContext { Workspace = NewProjectWorkspace.Solution, WorkspaceDirectory = solution, WorkspaceName = "Open", IsWindows = false });
        await model.LoadAsync();
        await SelectAsync(model, "dotnet:Microsoft.Common.Console|C#");
        await model.GoForwardAsync();
        Assert.True(model.ShowSolutionChoice);
        Assert.Equal(["Create new solution", "Add to solution"], model.SolutionChoices.Select(c => c.Label));
        Assert.True(model.CreatesSolution);
        model.SelectedSolutionChoice = model.SolutionChoices[1];
        Assert.False(model.CreatesSolution);
        Assert.Equal(solution, model.Location); // the location follows until edited
        Assert.False(model.ShowTrust);
        Assert.Equal("Adds the project to the solution \"Open\".", model.AddsTo);

        var folder = Model(new NewProjectContext { Workspace = NewProjectWorkspace.Folder, WorkspaceDirectory = solution, WorkspaceName = "Open", IsWindows = false });
        Assert.Equal(["Create new solution", "Add to folder"], folder.SolutionChoices.Select(c => c.Label));
        Assert.Single(Model(new NewProjectContext { Workspace = NewProjectWorkspace.ProjectFile, IsWindows = false }).SolutionChoices);
    }

    [AvaloniaFact]
    public async Task A_template_argument_starts_on_it_and_cancel_finishes_with_nothing()
    {
        var model = Model(NewProjectViewModel.ParseArguments([System.Text.Json.JsonElement.Parse("""{ "template": "console", "language": "VB" }""")], NewProjectMode.Project) with { IsWindows = false });
        Assert.Equal(("console", "VB", false), (model.Context.Template, model.Context.Language, model.Context.FromStartWindow));
        var finished = 0;
        NewProjectCompletion? completion = null;
        model.Finished += (_, c) => (finished, completion) = (finished + 1, c);
        await model.LoadAsync();
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Assert.Equal("dotnet:Microsoft.Common.Console|VB", model.SelectedTemplate!.Id);

        model.CancelCommand.Execute(null); // Escape
        Assert.Equal(1, finished);
        Assert.Null(completion);
        model.Cancel();
        Assert.Equal(1, finished); // once
    }

    [Fact]
    public void Access_keys_keep_free_letters_and_give_the_ides_options_the_first_free_one()
    {
        var keys = new AccessKeyAllocator(['B', 'C', 'F']);
        Assert.Equal("_Interactive render mode", keys.Allocate("_Interactive render mode", assign: false));
        Assert.Equal("Interactivity location", keys.Allocate("_Interactivity location", assign: false)); // I is taken: no key
        Assert.Equal("Include sample pages", keys.Allocate("In_clude sample pages", assign: false)); // C is the window's
        Assert.Equal("C_onfigure for HTTPS", keys.Allocate("Configure for HTTPS", assign: true));
        Assert.Equal("Use a__b", keys.Allocate("Use a__b", assign: false));
        Assert.Equal('S', AccessKeyAllocator.KeyOf("Include _sample pages"));
        Assert.Null(AccessKeyAllocator.KeyOf("No key"));
        Assert.Equal("Include sample pages", AccessKeyAllocator.Strip("Include _sample pages"));
    }
}
