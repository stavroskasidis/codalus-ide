using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;
using Codalus.Workbench.StatusBar;

namespace Codalus.Shell;

/// <summary>
/// Hot Reload while debugging (M55, ADR 0086): under the Codalus debugger, edits apply to the program being debugged.
/// Apply Code Changes (<c>Alt+F10</c>, the toolbar's Hot Reload button) applies them at any time; Continue, the steps
/// and Run to Cursor apply them first while stopped; a save applies them while the program runs, with
/// <c>debug.hotReload</c> on. A rude edit asks: Restart, or go on with the old code; its errors are in the Error List.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The debug session's Hot Reload (M55): it takes edits while a Codalus debugger runs the program.</summary>
    public DebugHotReload DebugEdits { get; private set; } = null!;

    private string? _debugEditsStatus;
    private bool _debugEditsLanguageSaid;

    private void InitializeDebugHotReload()
    {
        DebugEdits = new DebugHotReload(Debugger, () => Languages.RunningClient(CSharpLanguageId));
        DebugEdits.ActiveChanged += (_, _) => Ui.Post(() =>
        {
            if (!DebugEdits.IsActive)
            {
                _debugEditsStatus = null;
                _debugEditsLanguageSaid = false;
                ErrorList.SetHotReload([]);
            }
            else
            {
                _debugEditsStatus = Localizer.Get("status.debugHotReload.ready", "Hot Reload: edits apply while debugging");
            }

            UpdateHotReloadStatus();
        });
        Debugger.BeforeResume = async _ =>
        {
            if (!DebugEdits.IsActive)
            {
                return true;
            }

            // A block: an expression lambda's value would choose RunAsync<T>, which doesn't wait for the task.
            var go = true;
            await Ui.RunAsync(async () =>
            {
                go = await ApplyDebugEditsAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
            return go;
        };
    }

    /// <summary>
    /// Applies the edits to the program being debugged and says how it went: in the status bar, the Error List, and for a
    /// rude edit a question. False when the user chose to restart (the program doesn't go on).
    /// </summary>
    private async Task<bool> ApplyDebugEditsAsync()
    {
        _debugEditsStatus = Localizer.Get("status.debugHotReload.applying", "Hot Reload: applying…");
        UpdateHotReloadStatus();
        var outcome = await DebugEdits.ApplyAsync(CancellationToken.None).ConfigureAwait(true);
        var time = DateTime.Now.ToString("T", CultureInfo.CurrentCulture);
        switch (outcome.Kind)
        {
            case HotReloadOutcome.Applied:
                ErrorList.SetHotReload([]);
                _debugEditsStatus = Localizer.Format("status.debugHotReload.applied", "Hot Reload: applied at {0}", time);
                DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.applied", "Hot Reload: the code changes were applied ({0}).", time));
                break;
            case HotReloadOutcome.None:
                ErrorList.SetHotReload([]);
                _debugEditsStatus = Localizer.Get("status.debugHotReload.ready", "Hot Reload: edits apply while debugging");
                break;
            case HotReloadOutcome.Errors:
                ErrorList.SetHotReload(Rows(outcome.Diagnostics));
                _debugEditsStatus = Localizer.Get("status.debugHotReload.errors", "Hot Reload: not applied (compile errors)");
                DebugLog.AppendLine(Localizer.Get("msg.debugHotReload.errors", "Hot Reload: the code changes weren't applied: they don't compile (see the Error List)."));
                break;
            case HotReloadOutcome.Rude:
                ErrorList.SetHotReload(Rows(outcome.Diagnostics));
                _debugEditsStatus = Localizer.Get("status.debugHotReload.rude", "Hot Reload: restart needed");
                UpdateHotReloadStatus();
                var restart = Localizer.Get("action.debugHotReload.restart", "Restart");
                var keepGoing = Localizer.Get("action.debugHotReload.continue", "Continue with the Old Code");
                var first = outcome.Diagnostics is [var rude, ..] ? rude.Message : string.Empty;
                var choice = await Workbench.Notifications.Warning(
                    Localizer.Format("msg.debugHotReload.rude", "An edit can't be applied while the program runs: {0} Restart to run it, or go on with the code it started with.", first),
                    restart, keepGoing).Result.ConfigureAwait(true);
                if (choice == restart)
                {
                    _ = RestartDebuggingAsync();
                    return false;
                }

                break;
            case HotReloadOutcome.Failed:
                _debugEditsStatus = Localizer.Get("status.debugHotReload.failed", "Hot Reload: not applied");
                Workbench.Notifications.Error(Localizer.Format("msg.debugHotReload.failed", "The code changes couldn't be applied to the program: {0}", outcome.Message ?? string.Empty));
                break;
        }

        UpdateHotReloadStatus();
        return true;
    }

    /// <summary>A save while the program runs applies the edits (with <c>debug.hotReload</c> on).</summary>
    private void OnSavedForDebugEdits(string path)
    {
        if (!DebugEdits.IsActive)
        {
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".fs" or ".fsx" or ".fsi" or ".xaml" or ".axaml")
        {
            if (!_debugEditsLanguageSaid)
            {
                _debugEditsLanguageSaid = true;
                DebugLog.AppendLine(Localizer.Get("msg.debugHotReload.language", "Hot Reload while debugging applies C#, Visual Basic and Razor edits; F# and XAML edits run after a restart."));
            }

            return;
        }

        if (Debugger.State == DebugState.Running && Workbench.Settings.GetBoolean(DebugHotReloadSetting, true))
        {
            _ = ApplyDebugEditsAsync();
        }
    }

    private static List<ErrorListRow> Rows(IReadOnlyList<HotReloadDiagnostic> diagnostics) =>
    [
        .. diagnostics.Select(d =>
        {
            var path = d.Location is { } location ? DocumentUris.ToPath(location.Uri) ?? string.Empty : string.Empty;
            var start = d.Location?.Range.Start ?? new Position(0, 0);
            return new ErrorListRow("error", d.Id, d.Message, string.Empty, Path.GetFileName(path), start.Line + 1, start.Character + 1, path, start);
        }),
    ];
}
