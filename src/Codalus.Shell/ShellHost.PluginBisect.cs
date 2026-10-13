using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// Bisect Plugins (M22c, ADR 0060): finds the plugin behind a problem by turning half of the suspects off for the
/// workspace (the user's settings when no folder is open), restarting, and asking whether the problem is still there.
/// </summary>
public sealed partial class ShellHost
{
    public const string BisectPluginsCommand = "plugins.bisect";

    private string BisectStateFile => Path.Combine(Paths.State, "plugin-bisect.json");

    private SettingScope BisectScope => Solutions.Solution is null ? SettingScope.User : SettingScope.Workspace;

    private void InitializePluginBisect()
    {
        Workbench.Commands.Register(
            new CommandDescriptor(BisectPluginsCommand, "Bisect Plugins…", ContributionSource.Shell) { Category = "Plugins" },
            async (_, _) =>
            {
                await Ui.RunAsync(StartPluginBisectAsync).ConfigureAwait(false);
                return null;
            });
    }

    /// <summary>Starts a bisect over the enabled third-party plugins, after explaining it.</summary>
    public async Task StartPluginBisectAsync()
    {
        if (PluginBisect.Load(BisectStateFile) is { } running)
        {
            await AskPluginBisectAsync(running).ConfigureAwait(true);
            return;
        }

        var enabled = Plugins.Plugins.Where(p => !p.IsBuiltIn && p.IsUsable && !Plugins.IsTurnedOff(p.Id)).Select(p => p.Id).ToList();
        if (enabled.Count == 0)
        {
            Workbench.Notifications.Info("There are no plugins of your own to bisect: only built-in plugins are loaded.");
            return;
        }

        if (!await Dialogs.ConfirmAsync("Bisect plugins?", $"Codalus IDE restarts a few times, each time with half of the remaining {enabled.Count} plugins turned off, and asks whether the problem is still there. Built-in plugins stay on. Your plugin settings come back at the end.", "Start").ConfigureAwait(true))
        {
            return;
        }

        var step = PluginBisect.Start(enabled, [.. Plugins.TurnedOff(BisectScope)])!;
        await ApplyPluginBisectStepAsync(step).ConfigureAwait(true);
    }

    /// <summary>At startup with a bisect running: asks the round's question.</summary>
    private async Task ResumePluginBisectAsync()
    {
        if (PluginBisect.Load(BisectStateFile) is { } state)
        {
            await AskPluginBisectAsync(state).ConfigureAwait(true);
        }
    }

    private async Task AskPluginBisectAsync(PluginBisectState state)
    {
        var off = state.TurnedOff;
        var answer = await Workbench.Notifications.Info(
            $"Plugin bisect, round {state.Step + 1}: {off.Length} of {state.Suspects.Length} suspects are turned off ({string.Join(", ", off)}). Is the problem still there?",
            "Still There", "Gone", "Stop").Result.ConfigureAwait(true);
        await AnswerPluginBisectAsync(answer).ConfigureAwait(true);
    }

    /// <summary>The user's answer: <c>Still There</c>, <c>Gone</c> or <c>Stop</c> (null: asked again at the next start).</summary>
    public async Task AnswerPluginBisectAsync(string? answer)
    {
        if (PluginBisect.Load(BisectStateFile) is not { } state || answer is null)
        {
            return;
        }

        var step = answer switch
        {
            "Still There" => PluginBisect.Answer(state, stillThere: true),
            "Gone" => PluginBisect.Answer(state, stillThere: false),
            _ => PluginBisect.Stop(state),
        };
        await ApplyPluginBisectStepAsync(step).ConfigureAwait(true);
    }

    private async Task ApplyPluginBisectStepAsync(PluginBisectStep step)
    {
        PluginBisect.Save(BisectStateFile, step.State);
        await Workbench.Settings.SetAsync(PluginManager.DisabledSetting, JsonSerializer.SerializeToElement(step.Disabled, Docking.ShellJsonContext.Default.StringArray), BisectScope, CancellationToken.None).ConfigureAwait(true);
        if (step.State is not null)
        {
            Restart();
            return;
        }

        if (step.Culprit is { } culprit)
        {
            var name = Plugins.Plugins.FirstOrDefault(p => p.Id == culprit)?.DisplayName ?? culprit;
            var choice = await Workbench.Notifications.Warning($"The problem comes from {name} ({culprit}). Your plugin settings are back as they were; a restart applies them.", "Turn It Off", "Restart Now").Result.ConfigureAwait(true);
            if (choice == "Turn It Off")
            {
                await SetPluginTurnedOffAsync(culprit, off: true).ConfigureAwait(true);
            }
            else if (choice == "Restart Now")
            {
                Restart();
            }
        }
        else if (await Workbench.Notifications.Info("The plugin bisect stopped. Your plugin settings are back as they were; a restart applies them.", "Restart Now").Result.ConfigureAwait(true) == "Restart Now")
        {
            Restart();
        }
    }
}
