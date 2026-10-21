using System.Text.Json;
using Avalonia.Controls;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// New Project and New Solution (ADR 0082): the three-page wizard in a window of its own, owned by the main window and
/// not modal. The wizard asks everything on its own pages and writes the files (phase 1); it closes before the main
/// window opens or changes anything (phase 2): the new solution opens with the trust chosen on page 2, the project is
/// added through the build host, a trusted solution restores, and the template's start file opens.
/// </summary>
public sealed partial class ShellHost
{
    public const string DefaultLocationSetting = "templates.defaultLocation";

    private NewProjectViewModel? _newProject;
    private Window? _newProjectWindow;
    private Task<string?>? _newProjectResult;

    /// <summary>New Project's templates: the catalog over <see cref="Templates"/>'s CLI. Tests set a stand-in.</summary>
    public ProjectTemplateCatalog ProjectTemplates { get; internal set; } = null!;

    /// <summary>New Project's creation through the CLI. Tests set a stand-in.</summary>
    public NewProjectService NewProjects { get; internal set; } = null!;

    /// <summary>What New Project remembers (<c>&lt;state&gt;/new-project.json</c>).</summary>
    public NewProjectHistory NewProjectHistory { get; internal set; } = null!;

    /// <summary>The CLI New Project installs template packs with (<see cref="Templates"/>). Tests set a stand-in.</summary>
    internal ITemplateCli NewProjectCli { get; set; } = null!;

    /// <summary>The open wizard's model (tests).</summary>
    public NewProjectViewModel? NewProjectModel => _newProject;

    /// <summary>Whether the wizard is open.</summary>
    public bool IsNewProjectOpen => _newProject is not null;

    /// <summary>The wizard's window for a model (the view: NewProjectWindow). Without one the model is driven directly (tests).</summary>
    internal Func<NewProjectViewModel, Window?>? NewProjectWindowFactory { get; set; }

    private OutputChannel TemplatesLog => Workbench.Output.GetOrCreate("codalus.templates", "Templates");

    private void InitializeNewProject()
    {
        NewProjectCli = Templates;
        ProjectTemplates = new ProjectTemplateCatalog(Templates);
        NewProjects = new NewProjectService(Templates);
        NewProjectHistory = new NewProjectHistory(Path.Combine(Paths.State, "new-project.json"));
    }

    /// <summary>
    /// Opens the wizard (or activates the open one) and returns what Create made when it's done: the project's path, or
    /// the solution's for Blank Solution; null when it's cancelled or closed.
    /// </summary>
    public Task<string?> OpenNewProjectWizardAsync(NewProjectContext open)
    {
        if (_newProject is not null && _newProjectResult is not null)
        {
            _newProjectWindow?.Activate();
            return _newProjectResult;
        }

        var context = Snapshot(open);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new NewProjectViewModel(NewProjectServices(), context);
        _newProject = model;
        _newProjectResult = completion.Task;

        // Add New Project goes into the solution that was open: it closes when that solution closes.
        void OnSolutionChanged(object? sender, EventArgs e) => Ui.Post(() =>
        {
            if (_newProject == model && !Solutions.IsOpen)
            {
                model.OnClosed();
            }
        });

        if (context.Mode == NewProjectMode.Add)
        {
            Solutions.SolutionChanged += OnSolutionChanged;
        }

        model.Finished += (_, finished) =>
        {
            Solutions.SolutionChanged -= OnSolutionChanged;
            _newProject = null;
            _newProjectResult = null;
            var window = _newProjectWindow;
            _newProjectWindow = null;
            window?.Close();
            if (finished is null)
            {
                completion.TrySetResult(null);
                return;
            }

            _ = FinishAsync();

            async Task FinishAsync()
            {
                try
                {
                    completion.TrySetResult(await FinishNewProjectAsync(finished).ConfigureAwait(true));
                }
                catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or InvalidOperationException or IOException)
                {
                    TemplatesLog.AppendLine($"[new project] {ex}");
                    ShowTemplatesError(Localizer.Format("msg.newProject.openFailed", "Created {0}, but couldn't open it: {1}", finished.Result.ProjectPath ?? finished.Result.SolutionPath, ex.Message));
                    completion.TrySetResult(finished.Result.ProjectPath ?? finished.Result.SolutionPath);
                }
            }
        };

        if (NewProjectWindowFactory?.Invoke(model) is { } window)
        {
            _newProjectWindow = window;
            window.Closed += (_, _) =>
            {
                if (_newProjectWindow == window)
                {
                    _newProjectWindow = null;
                    model.OnClosed();
                }
            };
            if (Window is Window owner && owner.IsVisible)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        _ = model.LoadAsync();
        return completion.Task;
    }

    /// <summary>The workspace as the wizard opens: what's open, its trust, and (for Add New Project) the selected solution folder.</summary>
    private NewProjectContext Snapshot(NewProjectContext open)
    {
        var solution = Solutions.Solution;
        var workspace = solution switch
        {
            null => NewProjectWorkspace.None,
            { IsFolder: true } => NewProjectWorkspace.Folder,
            { Format: "project" } => NewProjectWorkspace.ProjectFile,
            _ => NewProjectWorkspace.Solution,
        };
        var folderId = open.Mode == NewProjectMode.Add && workspace == NewProjectWorkspace.Solution ? Explorer.Selected?.Node.SolutionFolderId : null;
        return open with
        {
            Workspace = workspace,
            WorkspacePath = solution?.Path,
            WorkspaceDirectory = solution?.Directory,
            WorkspaceName = solution?.Name,
            WorkspaceTrusted = solution?.Trusted ?? false,
            SolutionFolderId = folderId,
            SolutionFolderName = folderId is null ? null : solution?.Folders.FirstOrDefault(f => f.Id == folderId)?.Name,
            DefaultLocationSetting = Workbench.Settings.GetString(DefaultLocationSetting, string.Empty),
        };
    }

    private NewProjectServices NewProjectServices() => new()
    {
        Catalog = ProjectTemplates,
        Creator = NewProjects,
        History = NewProjectHistory,
        Cli = NewProjectCli,
        IsTrusted = Trust.IsTrusted,
        Trust = Trust.Trust,
        InstallMore = () => Run("tools.templates"),
        ShowOutput = log =>
        {
            TemplatesLog.AppendLine(log);
            Workbench.Output.Show(TemplatesLog);
        },
        BackToStartWindow = () => Ui.Post(ShowStartWindow),
    };

    /// <summary>
    /// Phase 2, in the main window once the wizard has closed: open the new solution with the trust chosen (no prompt)
    /// and add the project through the build host, or add it to the open solution or folder (in the solution folder
    /// selected when the wizard opened); restore a trusted solution; add container support; open the start file.
    /// </summary>
    internal async Task<string?> FinishNewProjectAsync(NewProjectCompletion completion)
    {
        var (request, plan, result) = (completion.Request, completion.Plan, completion.Result);
        TemplatesLog.AppendLine(result.Log);
        if (plan.CreatesSolution && result.SolutionPath is { } solution)
        {
            if (!await OpenSolutionAsync(solution, completion.Trusted).ConfigureAwait(true))
            {
                ShowCreatedNotOpened(solution);
                return result.ProjectPath ?? solution;
            }

            foreach (var project in new[] { result.ProjectPath }.Concat(result.ExtraProjects).OfType<string>())
            {
                Explorer.RevealWhenAvailable("p:" + Path.GetFullPath(project));
                await Solutions.AddProjectAsync(project, null, CancellationToken.None).ConfigureAwait(true);
            }
        }
        else if (result.ProjectPath is { } added)
        {
            foreach (var project in new[] { added }.Concat(result.ExtraProjects))
            {
                await AddProjectFileAsync(project, completion.SolutionFolderId).ConfigureAwait(true);
            }
        }

        if (result.ProjectPath is { } created)
        {
            if (Solutions.Solution is { Trusted: true } && Workbench.Settings.GetBoolean(RestoreOnOpenSetting, true))
            {
                _ = Solutions.RestoreAsync(null, CancellationToken.None);
            }

            if (request.HostOptions.Docker && completion.Trusted && request.Member?.HostOptions.Docker == true)
            {
                await AddDockerSupportAsync(created).ConfigureAwait(true);
            }

            foreach (var file in result.FilesToOpen)
            {
                await OpenFileAsync(file).ConfigureAwait(true);
            }
        }

        return result.ProjectPath ?? result.SolutionPath;
    }

    /// <summary>The files are there but the solution didn't open: say so, with Open.</summary>
    private void ShowCreatedNotOpened(string solution)
    {
        var open = Localizer.Get("msg.newProject.open", "Open");
        var notification = Workbench.Notifications.Info(Localizer.Format("msg.newProject.createdNotOpened", "Created {0}. It wasn't opened.", solution), open);
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == open)
                {
                    Ui.Post(() => _ = OpenSolutionAsync(solution));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void ShowTemplatesError(string message)
    {
        var show = Localizer.Get("msg.newProject.showOutput", "Show Output");
        var notification = Workbench.Notifications.Error(message, show);
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == show)
                {
                    Ui.Post(() => Workbench.Output.Show(TemplatesLog));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>A command's result: a path as a JSON string, or null.</summary>
    internal static JsonElement? PathResult(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(path);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
