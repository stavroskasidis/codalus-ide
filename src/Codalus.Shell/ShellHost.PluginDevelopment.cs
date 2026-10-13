using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// Plugin development (M22b): when the startup project is a plugin (a <c>codalus-plugin.json</c> next to it), Run and
/// Debug Plugin in Development Instance build it and start a second IDE with the build output as a plugin folder and
/// user data of its own (<c>.codalus/plugin-dev</c>). Debugging gives the plugin a plugin host of its own, which
/// writes its process id to a file and waits; this IDE attaches the debugger to it, so breakpoints in the plugin,
/// <c>ActivateAsync</c> included, are hit.
/// </summary>
public sealed partial class ShellHost
{
    public const string RunPluginCommand = "plugins.runDevelopmentInstance";
    public const string DebugPluginCommand = "plugins.debugDevelopmentInstance";
    public const string PluginManifestFile = "codalus-plugin.json";

    private void InitializePluginDevelopment()
    {
        void Add(string id, string title, bool debug) =>
            Workbench.Commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Debug", Enablement = WhenClause.Parse("pluginProject && !buildRunning") },
                async (_, _) =>
                {
                    await Ui.RunAsync(() => StartPluginDevelopmentInstanceAsync(debug)).ConfigureAwait(false);
                    return null;
                });
        Add(RunPluginCommand, "Run Plugin in Development Instance", debug: false);
        Add(DebugPluginCommand, "Debug Plugin in Development Instance", debug: true);
        var debugMenu = MenuLocations.MenuBar("debug");
        Workbench.Menus.Add(
        [
            new MenuItemRule(debugMenu, DebugPluginCommand, ContributionSource.Shell) { Group = "1-start", Order = 50, When = WhenClause.Parse("pluginProject") },
            new MenuItemRule(debugMenu, RunPluginCommand, ContributionSource.Shell) { Group = "1-start", Order = 51, When = WhenClause.Parse("pluginProject") },
        ]);
        Runs.StartupChanged += (_, _) => Ui.Post(UpdatePluginProject);
        UpdatePluginProject();
    }

    /// <summary>The startup project's folder has a plugin manifest.</summary>
    private void UpdatePluginProject() =>
        Workbench.Context.Set("pluginProject", Runs.StartupProject is { } project && File.Exists(Path.Combine(Path.GetDirectoryName(project)!, PluginManifestFile)));

    /// <summary>The development instance started last, for tests and Stop.</summary>
    internal Process? PluginDevelopmentInstance { get; private set; }

    /// <summary>Builds the startup plugin project and starts a development instance with it; with <paramref name="debug"/>, attaches to its plugin host.</summary>
    public async Task StartPluginDevelopmentInstanceAsync(bool debug)
    {
        if (Runs.StartupProject is not { } project || !File.Exists(Path.Combine(Path.GetDirectoryName(project)!, PluginManifestFile)))
        {
            StatusBar.ShowMessage("The startup project isn't a plugin (no codalus-plugin.json next to it).", TimeSpan.FromSeconds(5));
            return;
        }

        if (!await BuildAsync("Build", project).ConfigureAwait(true))
        {
            return;
        }

        string? output;
        try
        {
            output = (await Solutions.GetRunInfoAsync(project, CancellationToken.None).ConfigureAwait(true)).TargetPath is { } target ? Path.GetDirectoryName(target) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output = null;
            Workbench.Log.AppendLine($"[plugins] The plugin's output folder couldn't be found: {ex.Message}");
        }

        if (output is null || !File.Exists(Path.Combine(output, PluginManifestFile)))
        {
            Workbench.Notifications.Error($"The build output of {Path.GetFileNameWithoutExtension(project)} has no {PluginManifestFile}: copy it to the output (<None Update=\"{PluginManifestFile}\" CopyToOutputDirectory=\"PreserveNewest\" />).");
            return;
        }

        var debugFile = debug ? Path.Combine(Path.GetTempPath(), $"codalus-plugin-debug-{Guid.NewGuid():N}.pid") : null;
        if (PluginDevelopmentStart(project, output, debugFile) is not { } start)
        {
            Workbench.Notifications.Error("The development instance can't start: the path of this IDE's executable isn't known.");
            return;
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error($"The development instance couldn't start: {ex.Message}");
            return;
        }

        if (process is null)
        {
            return;
        }

        PluginDevelopmentInstance = process;
        Workbench.Log.AppendLine($"[plugins] Development instance {process.Id} with {output}{(debug ? " (debugging its plugin host)" : string.Empty)}.");
        if (debugFile is not null)
        {
            await AttachToPluginHostAsync(process, debugFile).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// How the development instance starts: this executable, the plugin's output as a plugin folder, user data in the
    /// solution's <c>.codalus/plugin-dev</c> (so it never touches the user's own), and the debug file when debugging.
    /// </summary>
    internal ProcessStartInfo? PluginDevelopmentStart(string project, string output, string? debugFile)
    {
        if (Environment.ProcessPath is not { } program)
        {
            return null;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        // `dotnet Codalus.Shell.dll` (a development build run through the muxer) starts the same way.
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && Environment.GetCommandLineArgs() is [var entry, ..] && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(entry);
        }

        var root = Solutions.Solution?.Path is { } solution ? Path.GetDirectoryName(solution)! : Path.GetDirectoryName(project)!;
        start.ArgumentList.Add("--user-data-dir");
        start.ArgumentList.Add(Path.Combine(root, ".codalus", "plugin-dev"));
        start.ArgumentList.Add("--plugin-dir");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--dev");
        if (ManifestAsksForProposedApi(Path.Combine(output, PluginManifestFile)))
        {
            start.ArgumentList.Add("--enable-proposed-api");
        }

        if (debugFile is not null)
        {
            start.ArgumentList.Add("--debug-plugin-host");
            start.ArgumentList.Add(debugFile);
        }

        return start;
    }

    private static bool ManifestAsksForProposedApi(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("enableProposedApi", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Waits for the development instance's debugged plugin host to write its process id, then attaches to it.</summary>
    private async Task AttachToPluginHostAsync(Process instance, string debugFile)
    {
        using var cancel = new CancellationTokenSource();
        var task = Workbench.Progress.Begin("Waiting for the plugin in the development instance", "plugins", cancel.Cancel);
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline && !instance.HasExited && !cancel.IsCancellationRequested)
            {
                if (File.Exists(debugFile) && int.TryParse(await File.ReadAllTextAsync(debugFile, cancel.Token).ConfigureAwait(true), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    task.End();
                    await Debugger.AttachAsync(pid, CancellationToken.None, name: "Plugin host (development instance)").ConfigureAwait(true);
                    return;
                }

                await Task.Delay(250, cancel.Token).ConfigureAwait(true);
            }

            if (!cancel.IsCancellationRequested)
            {
                Workbench.Notifications.Warning(instance.HasExited
                    ? "The development instance closed before the plugin loaded."
                    : "The plugin's host didn't start in the development instance within 2 minutes: check View → Plugins there.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Workbench.Notifications.Error($"The debugger couldn't attach to the plugin host: {ex.Message}");
        }
        finally
        {
            task.End();
            try
            {
                File.Delete(debugFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
