using Daedalus.Platform;
using Daedalus.Workbench.Commands;
using Daedalus.Workbench.Context;
using Daedalus.Workbench.Keybindings;
using Daedalus.Workbench.Menus;
using Daedalus.Workbench.Notifications;
using Daedalus.Workbench.Output;
using Daedalus.Workbench.Settings;
using Daedalus.Workbench.StatusBar;
using Daedalus.Workbench.ToolWindows;

namespace Daedalus.Workbench;

/// <summary>
/// The UI-agnostic workbench: every core service, wired together. The shell builds its views on top of it;
/// tests use it headless.
/// </summary>
public sealed class WorkbenchServices : IDisposable
{
    public WorkbenchServices(AppPaths paths, PlatformKind platform, string? workspaceSettingsFile = null, bool watchFiles = true)
    {
        Paths = paths;
        Platform = platform;
        Context = new ContextKeyService();
        Context.Set("isWindows", platform == PlatformKind.Windows);
        Context.Set("isMac", platform == PlatformKind.MacOS);
        Context.Set("isLinux", platform == PlatformKind.Linux);
        Commands = new CommandRegistry(Context);
        Keybindings = new KeybindingService(Context, platform);
        Menus = new MenuRegistry(Commands, Keybindings, Context);
        Settings = new SettingsService(paths.SettingsFile, workspaceSettingsFile, watchFiles);
        Notifications = new NotificationService();
        Output = new OutputService();
        StatusBar = new StatusBarService();
        ToolWindows = new ToolWindowRegistry();
        if (watchFiles)
        {
            _keybindingsWatcher = new FileWatcher(paths.KeybindingsFile);
            _keybindingsWatcher.Changed += (_, _) => LoadUserKeybindings();
        }
    }

    private readonly FileWatcher? _keybindingsWatcher;

    public AppPaths Paths { get; }

    public PlatformKind Platform { get; }

    public ContextKeyService Context { get; }

    public CommandRegistry Commands { get; }

    public KeybindingService Keybindings { get; }

    public MenuRegistry Menus { get; }

    public SettingsService Settings { get; }

    public NotificationService Notifications { get; }

    public OutputService Output { get; }

    public StatusBarService StatusBar { get; }

    public ToolWindowRegistry ToolWindows { get; }

    /// <summary>The shell's own log channel.</summary>
    public OutputChannel Log => Output.GetOrCreate("daedalus.log", "Daedalus Studio");

    /// <summary>(Re)reads the user's keybindings.json; problems go to the log.</summary>
    public void LoadUserKeybindings()
    {
        try
        {
            var text = File.Exists(Paths.KeybindingsFile) ? File.ReadAllText(Paths.KeybindingsFile) : "[]";
            var diagnostics = new List<string>();
            Keybindings.SetUserRules(KeybindingService.ParseRules(text, KeybindingSource.User, Platform, null, diagnostics));
            foreach (var diagnostic in diagnostics)
            {
                Log.AppendLine($"[keybindings.json] {diagnostic}");
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Log.AppendLine($"[keybindings.json] {ex.Message}");
        }
    }

    /// <summary>Writes keybindings.json (from <see cref="KeybindingService.RebindForUser"/>) atomically.</summary>
    public async Task SaveUserKeybindingsAsync(string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.KeybindingsFile)!);
        var temp = Paths.KeybindingsFile + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, Paths.KeybindingsFile, overwrite: true);
    }

    public void Dispose()
    {
        _keybindingsWatcher?.Dispose();
        Settings.Dispose();
    }
}
