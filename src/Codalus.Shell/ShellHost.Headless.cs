using System.ComponentModel;
using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell;

/// <summary>
/// The headless IDE of <c>codalus mcp --folder</c> (M60b, ADR 0099; <see cref="HeadlessIde"/> runs it): the same shell
/// with no one in front of it. Every dialog and picker is cancelled as it opens (the call that opened it is told so, as
/// an agent's command is); actions run only when the user's <c>ai.agent.ideActions</c> is <c>allow</c>, since an
/// approval that would ask is refused with the reason; a plugin's tool, which asks every time, is refused. It takes no
/// requests from other <c>codalus</c> processes, opens no MCP pipe of its own and starts none of the window's startup
/// work (updates, settings sync, the start window, the first-run setup).
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Whether this shell is the headless IDE of <c>codalus mcp --folder</c>.</summary>
    public bool IsHeadless => Options.Headless;

    /// <summary>The titles of the dialogs the headless IDE cancelled, last first (tests and the log).</summary>
    internal IReadOnlyList<string> CancelledHeadlessDialogs => _cancelledHeadlessDialogs;

    private readonly List<string> _cancelledHeadlessDialogs = [];

    private void InitializeHeadless()
    {
        if (!IsHeadless)
        {
            return;
        }

        Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogViewModel.IsOpen) && Dialogs.Request is { } request)
            {
                Ui.Post(() =>
                {
                    if (ReferenceEquals(Dialogs.Request, request))
                    {
                        _cancelledHeadlessDialogs.Insert(0, request.Title);
                        Workbench.Log.AppendLine($"[headless] Cancelled a dialog nobody can answer: {request.Title}");
                        Dialogs.Cancel();
                    }
                });
            }
        };
        Palette.Opened += (_, _) => Ui.Post(() =>
        {
            if (Palette.IsOpen)
            {
                Workbench.Log.AppendLine("[headless] Cancelled a picker nobody can answer.");
                Palette.Cancel();
            }
        });
    }

    /// <summary>
    /// Why the headless IDE refuses <paramref name="definition"/> without asking, or null when it runs it: a read always
    /// runs; an action only under <c>allow</c> in the user's settings; a plugin's tool never (it asks every time).
    /// </summary>
    private string? HeadlessRefusal(IdeToolDefinition definition)
    {
        if (!IsHeadless || definition.Kind == IdeToolKind.Read)
        {
            return null;
        }

        if (definition.Kind == IdeToolKind.Plugin)
        {
            return $"Codalus IDE runs without a window here (codalus mcp --folder), and {definition.Name} is a plugin's tool, which asks the user every time: with nobody to ask, it is refused. Don't try again; run the agent beside a Codalus IDE window to use it.";
        }

        var userValue = Workbench.Settings.GetLayerValue(IdeTools.ActionsSetting, Codalus.Workbench.Settings.SettingScope.User);
        var value = userValue is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
        return IdeTools.ParseApproval(value) == IdeActionApproval.Allow
            ? null
            : $"Codalus IDE runs without a window here (codalus mcp --folder), so nobody can approve {definition.Name}: actions run only when the user's setting {IdeTools.ActionsSetting} is \"allow\", and it is \"{value}\". Don't try again; ask the user to set it to allow, or to run the agent beside a Codalus IDE window.";
    }
}
