using Avalonia.Media;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Terminal;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.Terminal;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>The integrated terminal (ADR 0014): sessions, the tool window, settings and running programs in it.</summary>
public sealed partial class ShellHost
{
    public const string TerminalDefaultProfileSetting = "terminal.defaultProfile";
    public const string TerminalFontFamilySetting = "terminal.fontFamily";
    public const string TerminalFontSizeSetting = "terminal.fontSize";
    public const string TerminalScrollbackSetting = "terminal.scrollback";
    public const string TerminalCopyOnSelectSetting = "terminal.copyOnSelect";
    public const string TerminalShellIntegrationSetting = "terminal.shellIntegration";
    public const string TerminalCommandsToSkipShellSetting = "terminal.commandsToSkipShell";
    public const string RunConsoleSetting = "run.console";

    /// <summary>Commands that keep working while a terminal has focus; every other key goes to the shell.</summary>
    public static readonly string[] DefaultCommandsToSkipShell =
    [
        "workbench.showCommandPalette", "workbench.action.featureSearch", "workbench.focusNextPane", "workbench.focusPreviousPane", "workbench.quit",
        "workbench.view.terminal", "terminal.new", "terminal.split", "terminal.find", "terminal.focusNext", "terminal.focusPrevious",
        "build.buildSolution", "build.cancel", "debug.start", "debug.stop", "run.startWithoutDebugging", "run.stop",
        "workbench.openSettings", "workbench.view.solutionExplorer", "workbench.view.output",
    ];

    private HashSet<string> _commandsToSkipShell = new(DefaultCommandsToSkipShell, StringComparer.Ordinal);

    public TerminalService Terminals { get; private set; } = null!;

    public TerminalViewModel TerminalWindow { get; private set; } = null!;

    private TerminalAppearance TerminalLook { get; } = new();

    /// <summary>Detects the shells (for the default-profile setting) before settings are read.</summary>
    private void InitializeTerminalProfiles()
    {
        Terminals = new TerminalService(PlatformInfo.Current, Path.Combine(AppContext.BaseDirectory, "terminal"));
        Workbench.Settings.Register(TerminalSettings(Terminals.Profiles));
    }

    private void InitializeTerminal()
    {
        TerminalWindow = new TerminalViewModel(
            Terminals,
            TerminalDirectory,
            () => Workbench.Settings.GetString(TerminalDefaultProfileSetting) is { Length: > 0 } id ? id : null,
            TerminalLook,
            OpenTerminalLink);
        ApplyTerminalSettings();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Any(k => k.StartsWith("terminal.", StringComparison.Ordinal) || k.StartsWith("editor.font", StringComparison.Ordinal)))
            {
                Ui.Post(ApplyTerminalSettings);
            }
        };
        Theme.Changed += (_, _) => Ui.Post(ApplyTerminalSettings);
        Solutions.SolutionChanged += (_, _) => Terminals.DotnetRoot = Solutions.SdkPath is { } sdk ? Directory.GetParent(Path.TrimEndingDirectorySeparator(sdk))?.Parent?.FullName : null;
    }

    /// <summary>The solution folder, else the user's home.</summary>
    private string TerminalDirectory() =>
        Solutions.Solution?.Directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void ApplyTerminalSettings()
    {
        var settings = Workbench.Settings;
        var family = settings.GetString(TerminalFontFamilySetting);
        TerminalLook.FontFamily = string.IsNullOrWhiteSpace(family) ? EditorFont.Family : new FontFamily(family);
        var size = settings.GetDouble(TerminalFontSizeSetting, 0);
        TerminalLook.FontSize = size >= 6 ? Math.Min(size, 72) : EditorFont.Size;
        TerminalLook.CopyOnSelect = settings.GetBoolean(TerminalCopyOnSelectSetting, false);
        TerminalLook.Palette = TerminalTheme.For(Theme.ActiveTheme ?? "dark");
        Terminals.Scrollback = Math.Clamp(settings.GetInt32(TerminalScrollbackSetting, DaedalusStudio.Terminal.VirtualTerminal.DefaultScrollback), 0, 1_000_000);
        Terminals.ShellIntegration = settings.GetBoolean(TerminalShellIntegrationSetting, true);
        var skip = settings.GetStrings(TerminalCommandsToSkipShellSetting);
        _commandsToSkipShell = new HashSet<string>(skip.Count > 0 ? skip : DefaultCommandsToSkipShell, StringComparer.Ordinal);
    }

    /// <summary>Whether a key bound to <paramref name="command"/> stays with the IDE while a terminal has focus.</summary>
    public bool KeepsCommandFromTerminal(string command) => _commandsToSkipShell.Contains(command);

    private void OpenTerminalLink(TerminalLinkActivation link)
    {
        if (link.Kind == DaedalusStudio.Terminal.TerminalLinkKind.Url)
        {
            if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                ShellIntegration.OpenUrl(uri);
            }

            return;
        }

        var at = link.Line > 0 ? new DaedalusStudio.LanguageServices.Protocol.Position(link.Line - 1, Math.Max(0, link.Column - 1)) : null;
        _ = OpenFileAsync(link.Target, preview: false, at);
    }

    /// <summary>Shows the Terminal window, with a terminal (a new one when none is open or when asked).</summary>
    public TerminalSession? ShowTerminal(bool newTerminal = false, string? profile = null, string? directory = null)
    {
        Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
        if (newTerminal || TerminalWindow.Tabs.Count == 0)
        {
            return TerminalWindow.NewTerminal(profile, directory);
        }

        return TerminalWindow.Selected?.Primary;
    }

    /// <summary>Toggles the Terminal window (Ctrl+`), opening a terminal the first time.</summary>
    public void ToggleTerminal()
    {
        if (Layout.IsVisible(TerminalViewModel.ToolWindowId) && Equals(Workbench.Context.GetValue("focusedView"), TerminalViewModel.ToolWindowId))
        {
            Layout.CloseToolWindow(TerminalViewModel.ToolWindowId);
            return;
        }

        ShowTerminal();
    }

    /// <summary>Opens a terminal in the selected Solution Explorer folder or project (Open in Terminal).</summary>
    public void OpenInTerminal(string? path)
    {
        path ??= Explorer.Selected?.Node.Path;
        var directory = path is null ? TerminalDirectory() : Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        ShowTerminal(newTerminal: true, directory: directory);
    }

    /// <summary>Sends the editor's selection (or the current line) to the active terminal (Run Selected Text).</summary>
    public void RunSelectionInTerminal()
    {
        if (ActiveEditor?.Controller is not { } controller)
        {
            return;
        }

        var text = controller.Editor.SelectedText;
        if (text.Length == 0)
        {
            var line = controller.Editor.Document.GetLineByOffset(controller.Editor.CaretOffset);
            text = controller.Editor.Document.GetText(line.Offset, line.Length);
        }

        if (ShowTerminal() is { } session)
        {
            session.Write(text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r') + (text.EndsWith('\n') ? string.Empty : "\r"));
        }
    }

    /// <summary>
    /// Runs a project's planned launch in the integrated terminal (setting <c>run.console</c>), so console apps can read
    /// input. The tab shows the program; it stays open after the program exits.
    /// </summary>
    public async Task<bool> RunInTerminalAsync(string project)
    {
        var (plan, profile, error) = await Runs.PlanAsync(project, CancellationToken.None).ConfigureAwait(true);
        if (error is not null)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.terminal.ignoringTheLaunchSettings", "Ignoring the launch settings: {0}", error));
        }

        try
        {
            var name = Path.GetFileNameWithoutExtension(project) + (profile is null ? string.Empty : $" ({profile.Name})");
            var session = Terminals.Run(name, plan.FileName, plan.SplitArguments(), plan.WorkingDirectory, plan.Environment);
            Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
            TerminalWindow.Show(session);
            if (plan.BrowserUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => ShellIntegration.OpenUrl(uri), TaskScheduler.Default);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.terminal.couldNotRunInTheTerminal", "Could not run {0} in the terminal: {1}", Path.GetFileName(project), ex.Message));
            return false;
        }
    }

    public static IReadOnlyList<SettingDescriptor> TerminalSettings(IReadOnlyList<TerminalProfile> profiles)
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(TerminalDefaultProfileSetting, "\"\"", SettingType.String, shell) with
            {
                Title = "Default Profile",
                Category = "Terminal",
                Description = "The shell new terminals start. Empty: the first one found.",
                Enum = ["", .. profiles.Select(p => p.Id)],
                EnumDescriptions = ["Automatic", .. profiles.Select(p => p.Name)],
                Order = 1,
            },
            SettingDescriptor.Create(TerminalFontFamilySetting, "\"\"", SettingType.String, shell) with
            {
                Title = "Font Family",
                Category = "Terminal",
                Description = "Fonts for the terminal, comma-separated; empty uses the editor's font.",
                Order = 2,
            },
            SettingDescriptor.Create(TerminalFontSizeSetting, "0", SettingType.Number, shell) with
            {
                Title = "Font Size",
                Category = "Terminal",
                Description = "Font size in points; 0 uses the editor's size.",
                Minimum = 0,
                Maximum = 72,
                Order = 3,
            },
            SettingDescriptor.Create(TerminalScrollbackSetting, "10000", SettingType.Integer, shell) with
            {
                Title = "Scrollback",
                Category = "Terminal",
                Description = "Lines kept above the screen in each terminal (new terminals).",
                Minimum = 0,
                Maximum = 1_000_000,
                Order = 4,
            },
            SettingDescriptor.Create(TerminalCopyOnSelectSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Copy on Selection",
                Category = "Terminal",
                Description = "Copy selected text to the clipboard as soon as the selection ends.",
                Order = 5,
            },
            SettingDescriptor.Create(TerminalShellIntegrationSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Shell Integration",
                Category = "Terminal",
                Description = "Start bash, zsh and PowerShell with a script that reports the current directory and command marks (new terminals).",
                Order = 6,
            },
            SettingDescriptor.Create(TerminalCommandsToSkipShellSetting, "[]", SettingType.StringArray, shell) with
            {
                Title = "Commands Kept from the Shell",
                Category = "Terminal",
                Description = "Command ids whose key bindings keep working while a terminal has focus; every other key goes to the shell. Empty: the built-in list (palette, F6, build, run, debug, terminal commands).",
                Order = 7,
            },
            SettingDescriptor.Create(RunConsoleSetting, $"\"{DefaultRunConsole}\"", SettingType.String, shell) with
            {
                Title = "Console for Run Without Debugging",
                Category = "Build and Run",
                Description = "Where console and web apps run: their output in the Output window, the integrated terminal (they can read input), or a console window of their own (Windows, the default there).",
                Enum = ["outputWindow", "integratedTerminal", "externalConsole"],
                EnumDescriptions = ["Output window", "Integrated terminal", "Console window (Windows)"],
                Order = 3,
            },
        ];
    }
}
