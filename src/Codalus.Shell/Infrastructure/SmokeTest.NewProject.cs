using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// New Project in the shipped app (0.6.0, ADR 0082): File → New Project opens the three-page wizard in a window of its
/// own, owned by the main window, with a few recent templates. The search and the C# filter find Console App (the
/// website's picture, <c>new-project.png</c>); Configure names it, puts it in a new folder of the run's work folder and
/// trusts that folder on the page (<c>new-project-configure.png</c>); Additional information labels the frameworks with
/// their support and starts on the computed default (<c>new-project-additional.png</c>); Create writes it with the real
/// SDK, closes the window, and the main window opens the trusted solution with the project and Program.cs, asking
/// nothing. On Windows, Solution Explorer's Add → New Project then adds a WPF Application to it, which evaluates: a
/// Windows-only template through the wizard in the shipped app.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task NewProjectStepAsync()
    {
        var location = Path.Combine(Work, "new-project");
        var history = shell.NewProjectHistory;
        var dialogs = false;
        void OnDialog(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => dialogs |= e.PropertyName == nameof(DialogViewModel.IsOpen) && shell.Dialogs.IsOpen;
        shell.Dialogs.PropertyChanged += OnDialog;
        try
        {
            // The recent templates of someone who has made a few projects (the website's picture), Blazor pinned.
            foreach (var recent in new[] { "dotnet:Microsoft.Common.Console|F#", "dotnet:Microsoft.Web.WebApi|C#", "dotnet:Microsoft.Common.Library|C#", "dotnet:Microsoft.Test.xUnit|C#", "dotnet:Microsoft.Web.Blazor|C#" })
            {
                history.AddRecentTemplate(recent);
            }

            history.SetPinned("dotnet:Microsoft.Web.Blazor|C#", true);
            shell.Run("templates.newProject");
            await WaitAsync(() => shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 } && shell.NewProjectWindowView is { IsVisible: true }, "New Project opens with the templates (dotnet new list)", 120).ConfigureAwait(true);
            var model = shell.NewProjectModel!;
            var view = (NewProjectWindow)shell.NewProjectWindowView!;
            Check(ReferenceEquals(view.Owner, window), "the wizard is a window of its own, owned by the main window");
            view.Width = 1280; // the website's pictures' size, as the setup step's
            view.Height = 800;

            // Create a new project: the search and the C# filter.
            model.Search = "console";
            if (model.Languages.FirstOrDefault(l => l.Value == "C#") is { } csharp)
            {
                model.SelectedLanguage = csharp;
            }

            await WaitAsync(() => model.Templates.Any(r => r.Group is { ShortName: "console", Language: "C#" }), "the search and the C# filter find Console App").ConfigureAwait(true);
            var console = model.Templates.First(r => r.Group is { ShortName: "console", Language: "C#" });
            model.SelectedTemplate = console;
            await model.WaitForTemplateAsync().ConfigureAwait(true);
            await RenderedAsync(view).ConfigureAwait(true);
            Screenshot("new-project.png", view);

            // Configure your new project: a new folder of the work folder, trusted on the page.
            await model.GoForwardAsync().ConfigureAwait(true);
            Check(model.Page == NewProjectPage.Configure, $"Next goes to Configure your new project (on {model.Page})");
            model.ProjectName = "SmokeConsole";
            model.Location = location;
            Check(!model.TrustLocation, "Trust this location starts unchecked for a new folder");
            model.TrustLocation = true;
            await WaitAsync(() => model.CanGoNext, () => $"the names and the location check out ({model.NameError ?? model.LocationError ?? model.TargetError})", 60).ConfigureAwait(true);
            await RenderedAsync(view).ConfigureAwait(true);
            Screenshot("new-project-configure.png", view);

            // Additional information: the frameworks with their support, the computed default chosen.
            await model.GoForwardAsync().ConfigureAwait(true);
            Check(model.Page == NewProjectPage.Additional, $"Next goes to Additional information (on {model.Page})");
            Check(model.Frameworks.Any(f => f.Label.Contains("(Long Term Support)", StringComparison.Ordinal) || f.Label.Contains("(Standard Term Support)", StringComparison.Ordinal)),
                $"the frameworks say their support ({string.Join(", ", model.Frameworks.Select(f => f.Label))})");
            var group = console.Group;
            var expected = group.DefaultFramework(history.Framework(group.Id));
            Check(model.SelectedFramework?.Moniker == expected, $"the framework starts on {expected} (got {model.SelectedFramework?.Moniker})");
            await RenderedAsync(view).ConfigureAwait(true);
            Screenshot("new-project-additional.png", view);

            // Create: the wizard writes the files and closes; the main window opens the trusted solution, asking nothing.
            var project = Path.Combine(location, "SmokeConsole", "SmokeConsole", "SmokeConsole.csproj");
            await model.GoForwardAsync().ConfigureAwait(true);
            Check(model.CreateError is null, $"Create succeeds ({model.CreateError})");
            await WaitAsync(() => !shell.IsNewProjectOpen, "Create closes the wizard", 180).ConfigureAwait(true);
            Check(File.Exists(project), $"the project is written ({project})");
            await WaitAsync(() => shell.Solutions.Solution is { Trusted: true, Projects.Length: 1 } && shell.Solutions.Projects.Count == 1, "the trusted solution opens with the project evaluated", 240).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor?.Model.Path == Path.Combine(location, "SmokeConsole", "SmokeConsole", "Program.cs"), "Program.cs opens", 60).ConfigureAwait(true);
            Check(shell.Trust.IsTrusted(location), "the location is trusted");
            Check(!dialogs, "no dialog appeared in the main window (trust was asked on the wizard's page)");
            // Pinned templates stay first; the template just used leads the others.
            var recentLocations = history.RecentLocations;
            Check(history.RecentTemplates.FirstOrDefault(r => !r.Pinned)?.Id == group.Id && recentLocations.Count > 0 && recentLocations[0] == location,
                $"the template and the location are the first recent ones ({string.Join(", ", history.RecentTemplates.Select(r => r.Id))}; {string.Join(", ", recentLocations)})");

            // Windows: Add → New Project in the open solution, a Windows-only template.
            if (OperatingSystem.IsWindows())
            {
                await AddWpfProjectAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            shell.Dialogs.PropertyChanged -= OnDialog;
            shell.NewProjectModel?.Cancel();
            if (shell.Solutions.IsOpen)
            {
                await shell.CloseSolutionAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>Solution Explorer's Add → New Project: WPF Application joins the open solution and evaluates.</summary>
    private async Task AddWpfProjectAsync()
    {
        shell.Run("solution.addNewProject");
        await WaitAsync(() => shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0, IsAddMode: true }, "Add New Project opens", 120).ConfigureAwait(true);
        var model = shell.NewProjectModel!;
        Check(shell.NewProjectWindowView?.Title == "Add New Project", $"its title is Add New Project (got {shell.NewProjectWindowView?.Title})");
        var wpf = model.Templates.FirstOrDefault(r => r.Group is { ShortName: "wpf", Language: "C#" });
        Check(wpf is not null, "the WPF Application template is listed");
        model.SelectedTemplate = wpf;
        await model.WaitForTemplateAsync().ConfigureAwait(true);
        await model.GoForwardAsync().ConfigureAwait(true);
        model.ProjectName = "SmokeWpf";
        await WaitAsync(() => model.CanGoNext || model.CanCreate, () => $"Configure checks out ({model.NameError ?? model.LocationError ?? model.TargetError})", 60).ConfigureAwait(true);
        Check(!model.ShowSolutionChoice && !model.ShowTrust, "adding asks neither for a solution nor for trust");
        while (model.CanGoNext)
        {
            await model.GoForwardAsync().ConfigureAwait(true);
        }

        await model.GoForwardAsync().ConfigureAwait(true);
        await WaitAsync(() => !shell.IsNewProjectOpen, () => $"Create closes Add New Project ({model.CreateError})", 180).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Solution is { Projects.Length: 2 } && shell.Solutions.Projects.Count == 2, "WPF Application joins the solution and evaluates", 240).ConfigureAwait(true);
    }
}
