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
    /// <summary>Whether a save while netcoredbg debugs offers the Codalus debugger (M55); "Don't Show Again" turns it off.</summary>
    public const string DebugEditsOfferSetting = "debug.codalusOffer";

    /// <summary>The offer is made once a session at most.</summary>
    private bool _debugEditsOffered;

    /// <summary>The debug session's Hot Reload (M55): it takes edits while a Codalus debugger runs the program.</summary>
    public DebugHotReload DebugEdits { get; private set; } = null!;

    private string? _debugEditsStatus;

    /// <summary>When the last edits applied (a Blazor Web App's status says it beside the browser's).</summary>
    private string? _debugEditsApplied;
    private bool _debugEditsLanguageSaid;

    private void InitializeDebugHotReload()
    {
        DebugEdits = new DebugHotReload(Debugger, () => Languages.RunningClient(CSharpLanguageId));
        DebugEdits.ActiveChanged += (_, _) => Ui.Post(() =>
        {
            if (!DebugEdits.IsActive)
            {
                _debugEditsStatus = null;
                _debugEditsApplied = null;
                _debugEditsOffered = false;
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
                _debugEditsApplied = time;
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
            case HotReloadOutcome.Rude when WatchedServerDebugSession is { IsActive: true }:
                // A Blazor Web App under dotnet watch: dotnet watch restarts it for the edit, and debugging follows (ADR 0040).
                _debugEditsStatus = Localizer.Get("status.debugHotReload.watchRestarts", "Hot Reload: dotnet watch restarts the app");
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
                Workbench.Notifications.Error(outcome.Message?.Contains("CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED", StringComparison.Ordinal) == true
                    // A process attached to, which loaded its code before the debugger came without the variable.
                    ? Localizer.Get("msg.debugHotReload.notEnabled", "The code changes can't be applied to this program: it wasn't started to take them. Start it with DOTNET_MODIFIABLE_ASSEMBLIES=debug before attaching, or start it from Codalus IDE.")
                    : Localizer.Format("msg.debugHotReload.failed", "The code changes couldn't be applied to the program: {0}", outcome.Message ?? string.Empty));
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
            OfferCodalusDebugger(path);
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

    /// <summary>
    /// A C#, Visual Basic or Razor edit saved while netcoredbg debugs a .NET program: once, an offer to debug with the
    /// Codalus debugger (a preview), which would apply it. Use It sets <c>debug.adapter</c> (the next start takes it).
    /// </summary>
    private void OfferCodalusDebugger(string path)
    {
        if (_debugEditsOffered || !Debugger.IsActive || Debugger.UsesCodalusAdapter || Debugger.CodalusAdapters.Count > 0
            || Path.GetExtension(path).ToLowerInvariant() is not (".cs" or ".vb" or ".razor" or ".cshtml")
            || !Workbench.Settings.GetBoolean(DebugEditsOfferSetting, true)
            || Workbench.Settings.GetString(DebugAdapterSetting, DebugService.NetcoredbgAdapter) != DebugService.NetcoredbgAdapter)
        {
            return;
        }

        _debugEditsOffered = true;
        var use = Localizer.Get("action.debugHotReload.useCodalus", "Use It");
        var never = Localizer.Get("action.debugHotReload.dontShowAgain", "Don't Show Again");
        _ = Workbench.Notifications.Info(
            Localizer.Get("msg.debugHotReload.offer", "This edit doesn't reach the program: netcoredbg can't apply edits while it debugs. The Codalus debugger (a preview) applies them as you save, continue or press Alt+F10. Debug with it from the next start?"),
            use, never).Result.ContinueWith(t =>
        {
            if (t.Result == use)
            {
                _ = Workbench.Settings.SetAsync(DebugAdapterSetting, DebugService.CodalusAdapter, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
            }
            else if (t.Result == never)
            {
                _ = Workbench.Settings.SetAsync(DebugEditsOfferSetting, false, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
            }
        }, TaskScheduler.Default);
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
