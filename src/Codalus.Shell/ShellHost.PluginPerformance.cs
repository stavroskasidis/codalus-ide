using System.Globalization;
using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell;

/// <summary>
/// Plugin performance attribution (M22d): the Plugins window shows each plugin's activation time, its commands' calls
/// and its host's memory; a plugin that is slow to activate in three sessions gets a notification offering to turn it
/// off, as the classic IDE does for extensions that slow it down.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The sessions a plugin has to be slow in before the notification (each counted once).</summary>
    public const int SlowSessionsBeforeNotice = 3;

    private static readonly string s_session = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
    private readonly HashSet<string> _slowNoticed = new(StringComparer.Ordinal);

    private string SlowPluginsFile => Path.Combine(Paths.State, "slow-plugins.json");

    private void InitializePluginPerformance() =>
        Plugins.SlowPlugin += (_, plugin) => Ui.Post(() => _ = OnSlowPluginAsync(plugin));

    /// <summary>Counts the sessions a plugin was slow in, and offers to turn it off at the third.</summary>
    internal async Task OnSlowPluginAsync(PluginDescription plugin)
    {
        if (plugin.IsBuiltIn)
        {
            return;
        }

        Dictionary<string, string[]> slow;
        try
        {
            slow = File.Exists(SlowPluginsFile)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(SlowPluginsFile).ConfigureAwait(true), Docking.ShellJsonContext.Default.DictionaryStringStringArray) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            slow = [];
        }

        var sessions = slow.GetValueOrDefault(plugin.Id) ?? [];
        if (!sessions.Contains(s_session))
        {
            sessions = [.. sessions.Append(s_session).TakeLast(10)];
            slow[plugin.Id] = sessions;
            try
            {
                Directory.CreateDirectory(Paths.State);
                await File.WriteAllTextAsync(SlowPluginsFile, JsonSerializer.Serialize(slow, Docking.ShellJsonContext.Default.DictionaryStringStringArray)).ConfigureAwait(true);
            }
            catch (IOException)
            {
            }
        }

        if (sessions.Length < SlowSessionsBeforeNotice || !_slowNoticed.Add(plugin.Id))
        {
            return;
        }

        var seconds = plugin.ActivationTime?.TotalSeconds ?? 0;
        if (await Workbench.Notifications.Warning(
                $"{plugin.DisplayName} took {seconds:0.0} s to start, and it has been slow in {sessions.Length} sessions. Plugins share their host, so a slow one delays the others.",
                "Turn It Off", "Show Plugins").Result.ConfigureAwait(true) is { } choice)
        {
            if (choice == "Turn It Off")
            {
                await SetPluginTurnedOffAsync(plugin.Id, off: true).ConfigureAwait(true);
            }
            else
            {
                Layout.ShowToolWindow(BuiltIns.PluginsWindow, focus: true);
            }
        }
    }
}
