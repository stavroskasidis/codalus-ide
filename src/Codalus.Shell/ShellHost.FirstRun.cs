using System.Text.Json;
using Avalonia.Controls;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Settings;

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
/// window welcomes the user and offers the appearance (the theme families, ADR 0077, and the editor's font size), the
/// keyboard scheme, settings from elsewhere (VS Code, a <c>.vssettings</c> file, an export, settings sync), M37's tools
/// check and the AI experience (ADR 0096), and ends on a summary. <see cref="SetupCompletedSetting"/>
/// records that it ran. It doesn't show for automation, a command line that opens something, a synced profile, or a
/// folder used before it existed (<see cref="SetupDecision"/>). Help → Set Up Codalus IDE (and the Welcome plugin's Get
/// Started command) run it again. AI stays off unless the user chooses an agent or a provider on the AI step and ends
/// the setup with the summary's button: only then <see cref="ApplySetupAiAsync"/> runs, in the main window.
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
        if (Window is not Window { IsVisible: true } || IsClosing)
        {
            return; // the IDE is closing
        }

        // The AI step's choice, only after the summary's button (ADR 0096): Connect, or the provider's key and model, in
        // the main window, where the Terminal a sign-in or an install needs is. The start window waits for it.
        if (model.WasDone && model.AiChanged)
        {
            await ApplySetupAiAsync(model.AiChoice).ConfigureAwait(true);
        }

        if (!firstStart || IsClosing)
        {
            return; // run again from Help
        }

        ContinueStartup();
        if (!model.VisitedTools && Options.SmokeTest is null && Options.Bench is null)
        {
            _ = CheckEnvironmentAsync(offer: true, requiredOnly: true);
        }
    }

    /// <summary>
    /// Applies what the setup's AI step held when the setup was done (ADR 0096), as the user would from Tools → AI:
    /// <list type="bullet">
    /// <item>No AI, with AI on: <c>ai.enabled</c> goes off in the user's settings. Connected agents, the provider and the
    /// other switches stay, so turning it on again brings them back.</item>
    /// <item>A coding agent: Connect opens for its template (More agents… for the list, or when none was chosen); Connect
    /// asks for its own consent and writes the agent and <c>ai.enabled</c> only after a passing test (ADR 0083). An agent
    /// that is connected already only turns AI on and answers in AI Chat.</item>
    /// <item>The built-in assistant: AI on, the built-in assistant answering in AI Chat, the two as-you-type switches as
    /// ticked, then the provider (asked for when none was chosen) with its key and model, as Choose AI Provider… does.</item>
    /// </list>
    /// </summary>
    internal async Task ApplySetupAiAsync(SetupAiChoice choice)
    {
        var settings = Workbench.Settings;
        var chosen = (choice.Agent ?? choice.Provider) is { } what ? $" ({what})" : string.Empty;
        Workbench.Log.AppendLine($"[setup] AI: {choice.Path}{chosen}.");
        switch (choice.Path)
        {
            case SetupAiPath.None:
                if (UserSetting(AiService.EnabledSetting))
                {
                    await settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                break;

            case SetupAiPath.Agent:
                if (choice.Agent is { } connected && ExternalAgents.Configured.ContainsKey(connected))
                {
                    await TurnOnAiAsync().ConfigureAwait(true);
                    await settings.SetAsync(AcpAgents.ChatAgentSetting, connected, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    await ConnectExternalAgentAsync(choice.Agent is null or SetupAiChoice.More ? null : choice.Agent).ConfigureAwait(true);
                }

                break;

            case SetupAiPath.Assistant:
                await TurnOnAiAsync().ConfigureAwait(true);
                if (settings.GetString(AcpAgents.ChatAgentSetting, string.Empty).Length > 0)
                {
                    await settings.SetAsync(AcpAgents.ChatAgentSetting, string.Empty, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                if (UserSetting(AiCompletions.EnabledSetting) != choice.InlineSuggestions)
                {
                    await settings.SetAsync(AiCompletions.EnabledSetting, choice.InlineSuggestions, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                if (UserSetting(AiNextEdit.EnabledSetting) != choice.NextEdit)
                {
                    await settings.SetAsync(AiNextEdit.EnabledSetting, choice.NextEdit, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                if (choice.Provider is { } provider && LanguageModels.Provider(provider) is not null)
                {
                    await UseAiProviderAsync(provider).ConfigureAwait(true);
                }
                else
                {
                    await ChooseAiProviderAsync().ConfigureAwait(true);
                }

                break;
        }

        bool UserSetting(string key) => settings.GetLayerValue(key, SettingScope.User) is { ValueKind: JsonValueKind.True };
    }
}
