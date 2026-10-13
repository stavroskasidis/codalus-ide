using System.Diagnostics;
using System.Text.Json;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// Restarting the IDE (M23b): File → Restart closes it as usual (asking about unsaved files) and starts it again on the
/// same solution and user-data folder. Turning plugins off and on (<see cref="PluginManager.DisabledSetting"/>) applies
/// at the next start, so the Plugins window offers it.
/// </summary>
public sealed partial class ShellHost
{
    private bool _restartRequested;

    /// <summary>Closes the window; once the IDE has exited, it starts again.</summary>
    public void Restart()
    {
        _restartRequested = true;
        Window?.RequestClose();
    }

    /// <summary>Called last on exit (after the single-instance lock is released): starts the IDE again when <see cref="Restart"/> asked for it.</summary>
    private void RestartIfRequested(string? solution)
    {
        if (!_restartRequested || _restartForUpdate || Environment.ProcessPath is not { } program)
        {
            return;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        if (solution is not null)
        {
            start.ArgumentList.Add(solution);
        }

        if (Options.UserDataDir is { } userData)
        {
            start.ArgumentList.Add("--user-data-dir");
            start.ArgumentList.Add(userData);
        }

        foreach (var pluginDir in Options.PluginDirs)
        {
            start.ArgumentList.Add("--plugin-dir");
            start.ArgumentList.Add(pluginDir);
        }

        if (Options.EnableProposedApi)
        {
            start.ArgumentList.Add("--enable-proposed-api");
        }

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Log.AppendLine($"[restart] Couldn't start {program}: {ex.Message}");
        }
    }

    /// <summary>Turns a plugin off or back on (<see cref="PluginManager.DisabledSetting"/>); it applies at the next start.</summary>
    public async Task SetPluginTurnedOffAsync(string pluginId, bool off)
    {
        var ids = Workbench.Settings.GetValue(PluginManager.DisabledSetting) is { ValueKind: JsonValueKind.Array } current
            ? current.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
            : [];
        ids.RemoveAll(i => string.Equals(i, pluginId, StringComparison.OrdinalIgnoreCase));
        if (off)
        {
            ids.Add(pluginId);
        }

        await Workbench.Settings.SetAsync(PluginManager.DisabledSetting, JsonSerializer.SerializeToElement(ids.ToArray(), Docking.ShellJsonContext.Default.StringArray), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        PluginList.Refresh();
        if (await Workbench.Notifications.Info($"The plugin {pluginId} is turned {(off ? "off" : "on")} from the next start.", "Restart Now").Result.ConfigureAwait(true) == "Restart Now")
        {
            Restart();
        }
    }
}
