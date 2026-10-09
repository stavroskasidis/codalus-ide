using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Interactive;

namespace DaedalusStudio.Shell;

/// <summary>C# Interactive (ADR 0015): the REPL host connection, the tool window and sending code to it.</summary>
public sealed partial class ShellHost
{
    public InteractiveService Interactive { get; private set; } = null!;

    public InteractiveViewModel InteractiveWindow { get; private set; } = null!;

    private void InitializeInteractive()
    {
        Interactive = new InteractiveService(InteractiveHostLaunch.Resolve, TerminalDirectory());
        InteractiveWindow = new InteractiveViewModel(Interactive, EditorFont) { Colors = EditorTheme };
        Theme.Changed += (_, _) => Ui.Post(() => InteractiveWindow.Colors = EditorTheme);
    }

    /// <summary>Shows the C# Interactive window and focuses its input.</summary>
    public InteractiveViewModel ShowInteractive()
    {
        Layout.ShowToolWindow(InteractiveViewModel.ToolWindowId);
        return InteractiveWindow;
    }

    /// <summary>Runs the editor's selection (or the current line) in C# Interactive (Send to Interactive).</summary>
    public async Task SendToInteractiveAsync()
    {
        if (ActiveEditor?.Controller?.Editor is not { } editor)
        {
            return;
        }

        var text = editor.SelectionLength > 0
            ? editor.SelectedText
            : editor.Document.GetText(editor.Document.GetLineByOffset(editor.CaretOffset));
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!Interactive.IsAvailable)
        {
            StatusBar.ShowMessage("C# Interactive isn't available (daedalus-replhost was not found).", TimeSpan.FromSeconds(5));
            return;
        }

        await ShowInteractive().ExecuteAsync(text).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the project, then resets C# Interactive with its output and references (Initialize Interactive with
    /// Project). The project's compile references come from its last design-time build.
    /// </summary>
    public async Task InitializeInteractiveWithProjectAsync(string? argument = null)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage("Select a project to initialize C# Interactive with.", TimeSpan.FromSeconds(4));
            return;
        }

        var window = ShowInteractive();
        if (!await BuildAsync(DaedalusStudio.Projects.Protocol.BuildTargets.Build, project).ConfigureAwait(true))
        {
            window.Transcript.AppendLine($"{Path.GetFileNameWithoutExtension(project)} didn't build; C# Interactive was not initialized.");
            return;
        }

        if (Languages.Project(project) is not { } data)
        {
            window.Transcript.AppendLine($"{Path.GetFileNameWithoutExtension(project)} has no compiler information yet (its design-time build hasn't finished).");
            return;
        }

        var configuration = InteractiveProjects.Configuration(project, data.OutputPath, data.Arguments);
        Layout.ShowToolWindow(InteractiveViewModel.ToolWindowId);
        await window.ResetAsync(configuration, $"Initializing C# Interactive with {Path.GetFileNameWithoutExtension(project)} ({configuration.References.Length} references).").ConfigureAwait(true);
    }
}
