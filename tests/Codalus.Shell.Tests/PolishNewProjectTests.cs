using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Tests;

namespace Codalus.Shell.Tests;

/// <summary>
/// New Project's window (0.6.0, ADR 0082; docs/design/controls/NewProject.dc.html) with the checks the other new views
/// get: each page in its states, at the window's minimum size (760 × 520), in all sixteen themes: no text cut off without
/// an ellipsis and a tooltip, every control named, no input squeezed, every text readable on what it is drawn on, and
/// every icon (the template kinds') in the palette. The .NET Framework rows and their framework labels are among the
/// states (they replace the quick picks PolishNetFrameworkTests photographed before 0.6.0).
/// </summary>
public partial class PolishTests
{
    /// <summary>The wizard's minimum size (NewProjectWindow.axaml).</summary>
    private const double NewProjectMinWidth = 760;

    private const double NewProjectMinHeight = 520;

    /// <summary><c>dotnet new</c> as files (sln and a project), or a failure for <c>fail</c>.</summary>
    private static CliResult StandInDotnetNew(IReadOnlyList<string> args)
    {
        if (args is not ["new", var template, ..])
        {
            return new CliResult(1, "unexpected: " + string.Join(' ', args));
        }

        var list = args.ToList();
        var name = list[list.IndexOf("--name") + 1];
        if (name == "Broken" && template != "sln")
        {
            return new CliResult(1, "Template \"Blazor Web App\" could not be created.\nFailed to create template.");
        }

        var output = list[list.IndexOf("--output") + 1];
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, name + (template == "sln" ? ".slnx" : ".csproj")), template == "sln" ? "<Solution />" : "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new CliResult(0, "The template was created successfully.");
    }

    [AvaloniaFact]
    public async Task The_new_project_window_in_every_state_cuts_no_text_names_every_control_and_stays_readable_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        var root = Directory.CreateDirectory(Path.Combine(f.UserData, "new-project")).FullName;
        var location = Directory.CreateDirectory(Path.Combine(root, "repos")).FullName;
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var checkedStates = new List<string>();

        (NewProjectViewModel Model, NewProjectWindow Window) Open(NewProjectContext context, bool windows = false)
        {
            var cli = RecordedCli.Sdk11();
            cli.Fallback = StandInDotnetNew;
            var rules = new PathRules(IsWindows: windows, LongPathsEnabled: false);
            var history = new NewProjectHistory(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"));
            history.AddRecentLocation(location);
            foreach (var recent in new[] { "dotnet:Microsoft.Common.Console|F#", "dotnet:Microsoft.Test.xUnit|C#", "dotnet:Microsoft.Common.Console|C#", "dotnet:Microsoft.Web.Blazor|C#" })
            {
                history.AddRecentTemplate(recent);
            }

            history.SetPinned("dotnet:Microsoft.Web.Blazor|C#", true);
            var model = new NewProjectViewModel(
                new NewProjectServices
                {
                    Catalog = new ProjectTemplateCatalog(cli, new FixedCacheLocator(data.Cache11), () => new DateOnly(2026, 10, 21)),
                    Creator = new NewProjectService(cli, rules),
                    History = history,
                    Cli = cli,
                    IsTrusted = _ => false,
                    Trust = _ => { },
                    Rules = rules,
                },
                context with { IsWindows = windows })
            {
                SearchDelay = TimeSpan.Zero,
                ValidationDelay = TimeSpan.Zero,
            };
            var window = new NewProjectWindow { DataContext = model, Width = NewProjectMinWidth, Height = NewProjectMinHeight };
            window.Show();
            return (model, window);
        }

        async Task SettleAsync(NewProjectViewModel model)
        {
            await model.Validation;
            await ShellFixture.PumpUntil(() => !model.IsValidating);
            ShellFixture.Pump();
        }

        void Check(string state, Window window)
        {
            checkedStates.Add(state);
            foreach (var problem in InspectWindowInEveryTheme(window, NewProjectMinWidth, NewProjectMinHeight))
            {
                problems.Add($"new-project ({state}): {problem}");
            }

            NewProjectSnapshot(state, window);
        }

        async Task SelectAsync(NewProjectViewModel model, Func<NewProjectTemplateRow, bool> which)
        {
            model.SelectedTemplate = model.Templates.First(which);
            await model.WaitForTemplateAsync();
            ShellFixture.Pump();
        }

        // Create a new project: the templates with recent ones, a search with the C# filter, no match, a pack to install,
        // and the .NET Framework rows off Windows with their Windows only chips.
        var (model, window) = Open(new NewProjectContext());
        await model.LoadAsync();
        await SelectAsync(model, r => r.Id == "dotnet:Microsoft.Common.Console|C#");
        Check("choose", window);
        model.Search = "web";
        model.SelectedLanguage = model.Languages.Single(l => l.Value == "C#");
        Check("choose (search with the C# filter)", window);
        model.Search = "nothing like it";
        Check("choose (no match)", window);
        model.ClearAllCommand.Execute(null);
        await SelectAsync(model, r => r.IsPack);
        Check("choose (a template pack to install)", window);
        model.Search = "framework";
        await SelectAsync(model, r => r.Group.Kind == ProjectTemplateKind.NetFramework);
        Assert.All(model.Templates.Where(r => r.Group.Kind == ProjectTemplateKind.NetFramework), r => Assert.True(r.ShowWindowsOnly));
        Check("choose (.NET Framework, windows only)", window);

        // .NET Framework's Additional information: the frameworks labeled as the classic IDE does.
        await model.GoForwardAsync();
        await SettleAsync(model);
        await model.GoForwardAsync();
        ShellFixture.Pump();
        Assert.Contains(model.Frameworks, c => c.Label.StartsWith(".NET Framework 4.8", StringComparison.Ordinal));
        Check("additional (.NET Framework)", window);
        model.Cancel();
        window.Close();

        // Configure your new project: a new solution, a reserved name, Blank Solution; then with a solution open, and
        // adding to it.
        (model, window) = Open(new NewProjectContext(), windows: true);
        await model.LoadAsync();
        await SelectAsync(model, r => r.Id == "dotnet:Microsoft.Web.Blazor|C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        Check("configure", window);
        model.ProjectName = "CON";
        await SettleAsync(model);
        Check("configure (a reserved name)", window);
        model.ProjectName = "Shop";
        await SettleAsync(model);

        // Additional information: Blazor with Individual Accounts (HTTPS locked) in an untrusted location; then Create
        // failing, with the CLI's message and Show Output.
        await model.GoForwardAsync();
        ShellFixture.Pump();
        model.SelectedAuth = model.AuthChoices.Single(a => a.Value == "Individual");
        ShellFixture.Pump();
        Check("additional (blazor, individual accounts, untrusted)", window);
        model.GoBack();
        model.ProjectName = "Broken";
        await SettleAsync(model);
        await model.GoForwardAsync();
        await model.GoForwardAsync();
        await ShellFixture.PumpUntil(() => model.HasCreateError);
        Check("additional (create failed)", window);

        model.GoBack();
        model.GoBack();
        await SelectAsync(model, r => r.Group.Kind == ProjectTemplateKind.BlankSolution);
        await model.GoForwardAsync();
        await SettleAsync(model);
        Check("configure (blank solution)", window);
        model.Cancel();
        window.Close();

        (model, window) = Open(new NewProjectContext { Workspace = NewProjectWorkspace.Solution, WorkspaceName = "Wcf", WorkspaceDirectory = location, WorkspacePath = Path.Combine(location, "Wcf.slnx") });
        await model.LoadAsync();
        await SelectAsync(model, r => r.Id == "dotnet:Microsoft.Test.xUnit|C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        Check("configure (a solution open)", window);

        // xUnit for .NET 11.0: its own options (xUnit.net version, Test runner).
        await model.GoForwardAsync();
        model.SelectedFramework = model.Frameworks.Single(c => c.Moniker == "net11.0");
        ShellFixture.Pump();
        Check("additional (xunit, .NET 11.0)", window);
        model.Cancel();
        window.Close();

        (model, window) = Open(new NewProjectContext { Mode = NewProjectMode.Add, Workspace = NewProjectWorkspace.Solution, WorkspaceName = "Wcf", WorkspaceDirectory = location, WorkspacePath = Path.Combine(location, "Wcf.slnx"), WorkspaceTrusted = true, SolutionFolderId = "src", SolutionFolderName = "src" });
        await model.LoadAsync();
        Assert.Equal("Add New Project", window.Title);
        Check("choose (add a new project)", window);
        await SelectAsync(model, r => r.Group.Name == "Class Library" && r.Group.Language == "C#");
        await model.GoForwardAsync();
        await SettleAsync(model);
        Check("configure (adding to a solution folder)", window);

        // A template with nothing but a framework says so.
        await model.GoForwardAsync();
        ShellFixture.Pump();
        Check("additional (class library)", window);
        model.Cancel();
        window.Close();

        Assert.True(problems.Count == 0, $"{checkedStates.Count} states checked; problems:\n" + string.Join('\n', problems));
    }

    /// <summary>With CODALUS_UI_SNAPSHOTS set, the window as checked, in Classic Light and Classic Dark, for review.</summary>
    private static void NewProjectSnapshot(string state, Window window)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is not { Length: > 0 } snapshots)
        {
            return;
        }

        Directory.CreateDirectory(snapshots);
        var old = (window.Width, window.Height, window.RequestedThemeVariant);
        (window.Width, window.Height) = (1000, 680);
        foreach (var (name, variant) in s_themes.Where(t => t.Variant == ThemeVariant.Light || t.Variant == ThemeVariant.Dark || t.Variant == ThemeService.HarborHighContrast))
        {
            window.RequestedThemeVariant = variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            var file = "new-project-" + string.Concat($"{state}-{name}".Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
            frame!.Save(Path.Combine(snapshots, file + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        (window.Width, window.Height, window.RequestedThemeVariant) = old;
        ShellFixture.Pump();
    }
}
