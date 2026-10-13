using Codalus.Platform;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Output;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Workbench;

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
        Progress = new ProgressService();
        Output = new OutputService();
        StatusBar = new StatusBarService();
        ToolWindows = new ToolWindowRegistry();
        LanguageDefinitions = new Languages.LanguageRegistry();
        Snippets = new Snippets.SnippetRegistry();
        LanguageServers = new Languages.LanguageServiceRegistry();
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

    /// <summary>Long operations shown in the status bar, cancellable when their owner allows it (API 1.0).</summary>
    public ProgressService Progress { get; }

    public OutputService Output { get; }

    public StatusBarService StatusBar { get; }

    public ToolWindowRegistry ToolWindows { get; }

    /// <summary>Languages and TextMate grammars (ADR 0016).</summary>
    public Languages.LanguageRegistry LanguageDefinitions { get; }

    /// <summary>Code snippets: built in, from plugins (<c>contributes.snippets</c>) and the user's (M12b).</summary>
    public Snippets.SnippetRegistry Snippets { get; }

    /// <summary>Language servers (C# and the ones plugins contribute).</summary>
    public Languages.LanguageServiceRegistry LanguageServers { get; }

    /// <summary>The shell's own log channel.</summary>
    public OutputChannel Log => Output.GetOrCreate("codalus.log", "Codalus IDE");

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
