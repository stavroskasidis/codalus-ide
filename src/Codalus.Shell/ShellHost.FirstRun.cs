using System.Text.Json;
using Avalonia.Controls;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell;

/// <summary>Whether the first-run setup shows when the IDE starts (M46), and why not.</summary>
public enum SetupDecision
{
    /// <summary>The setup shows, before the start window.</summary>
    Show,

    /// <summary><c>--skip-setup</c> or <c>CODALUS_SKIP_SETUP=1</c>: automation asked for no setup.</summary>
    SkippedBySwitch,

    /// <summary>The smoke test, a benchmark (<c>--bench</c>, <c>--exit-on</c>) or a screenshot run.</summary>
    Automation,

    /// <summary>The command line opens something: a solution, folder or file, a remote, a diff, a merge or <c>--wait</c>.</summary>
    OpensSomething,

    /// <summary><c>workbench.setupCompleted</c>: the setup ran (Done or Skip setup) for this user data folder.</summary>
    Completed,

    /// <summary>Settings sync is on: the profile arrives from it, so there is nothing to set up.</summary>
    SyncedProfile,

    /// <summary>The user data folder was used before the setup existed: it has settings or a saved window.</summary>
    UsedBefore,
}

/// <summary>
/// The first-run setup (M46): the first time the IDE starts for a user data folder, a window of its own before the start
/// window offers the appearance (the theme families, ADR 0077), the keyboard scheme, settings from elsewhere (VS Code,
/// a <c>.vssettings</c> file, an export, settings sync) and M37's tools check. <see cref="SetupCompletedSetting"/>
/// records that it ran. It doesn't show for automation, a command line that opens something, a synced profile, or a
/// folder used before it existed (<see cref="SetupDecision"/>). Help → Set Up Codalus IDE (and the Welcome plugin's Get
/// Started command) run it again. AI stays off and isn't asked about (ADR 0062).
/// </summary>
public sealed partial class ShellHost
{
    public const string SetupCompletedSetting = "workbench.setupCompleted";

    /// <summary><c>CODALUS_SKIP_SETUP=1</c>: as <c>--skip-setup</c>, for automation that can't add an argument.</summary>
    public const string SkipSetupVariable = "CODALUS_SKIP_SETUP";

    /// <summary>Whether the user data folder had been used when this shell started (read before anything writes to it).</summary>
    private readonly bool _usedBefore;

    private SetupWindow? _setupWindow;

    /// <summary>Whether the setup window is open.</summary>
    public bool IsSetupOpen => _setupWindow is not null;

    /// <summary>The open setup's model (tests, the smoke test).</summary>
    public SetupViewModel? SetupModel => _setupWindow?.DataContext as SetupViewModel;

    /// <summary>The open setup window (tests capture its frames).</summary>
    internal SetupWindow? SetupWindowView => _setupWindow;

    /// <summary>Whether the setup shows at this start, and why not.</summary>
    public SetupDecision FirstRunDecision => Decide(
        Options,
        Workbench.Settings.GetBoolean(SetupCompletedSetting),
        syncOn: SyncTarget.Length > 0,
        _usedBefore,
        Environment.GetEnvironmentVariable(SkipSetupVariable));

    /// <summary>The decision for a start (unit-tested): the switch, automation, the command line, then the folder's state.</summary>
    public static SetupDecision Decide(ShellOptions options, bool completed, bool syncOn, bool usedBefore, string? skipVariable)
    {
        if (options.SkipSetup || skipVariable == "1")
        {
            return SetupDecision.SkippedBySwitch;
        }

        if (options.SmokeTest is not null || options.Bench is not null || options.ExitOn is not null || options.Screenshot is not null)
        {
            return SetupDecision.Automation;
        }

        if (options.Path is not null || options.Remote is not null || options.Diff is not null || options.Merge is not null
            || options.WaitFile is not null || options.OpenFile is not null)
        {
            return SetupDecision.OpensSomething;
        }

        return completed ? SetupDecision.Completed
            : syncOn ? SetupDecision.SyncedProfile
            : usedBefore ? SetupDecision.UsedBefore
            : SetupDecision.Show;
    }

    /// <summary>
    /// Whether a user data folder was used before (an install from before M46, or one whose settings came another way): its
    /// <c>settings.json</c> holds a setting, or a window layout was saved in <c>state.json</c>. Such a folder isn't shown
    /// the setup, and nothing is written for it.
    /// </summary>
    internal static bool WasUsed(AppPaths paths)
    {
        if (File.Exists(Path.Combine(paths.State, "state.json")))
        {
            return true;
        }

        try
        {
            if (!File.Exists(paths.SettingsFile))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return true; // a file that can't be read is someone's: don't take it for a new folder
        }
    }

    /// <summary>Help → Set Up Codalus IDE and Get Started: the setup again, over the running IDE.</summary>
    public void ShowSetup() => ShowSetup(firstStart: false);

    /// <summary>
    /// Opens the setup. <paramref name="firstStart"/>: at the first start (before the start window), so closing it goes on
    /// to the start window, and the tools a skipped Tools step didn't show are offered as M37 does.
    /// </summary>
    internal void ShowSetup(bool firstStart)
    {
        if (_setupWindow is not null)
        {
            _setupWindow.Activate();
            return;
        }

        var model = new SetupViewModel(this, firstStart);
        var window = _setupWindow = new SetupWindow { DataContext = model };
        model.Finished += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            _setupWindow = null;
            _ = OnSetupClosedAsync(model, firstStart);
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

    private async Task OnSetupClosedAsync(SetupViewModel model, bool firstStart)
    {
        // Closed with its close button, Escape or with the IDE: as Skip setup (it ran; the current step isn't saved).
        await model.FinishAsync(done: false).ConfigureAwait(true);
        Workbench.Log.AppendLine($"[setup] The first-run setup {(model.WasDone ? "is done" : "was skipped")}.");
        if (!firstStart || Window is not Window { IsVisible: true })
        {
            return; // run again from Help, or the IDE is closing
        }

        ContinueStartup();
        if (!model.VisitedTools && Options.SmokeTest is null && Options.Bench is null)
        {
            _ = CheckEnvironmentAsync(offer: true);
        }
    }
}
