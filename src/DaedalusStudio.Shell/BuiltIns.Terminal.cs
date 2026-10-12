using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>The integrated terminal's tool window, commands, menus and key bindings (ADR 0014).</summary>
internal static partial class BuiltIns
{
    public const string TerminalKeybindings = """
        [
          { "key": "ctrl+`", "command": "workbench.view.terminal" },
          { "key": "ctrl+shift+`", "command": "terminal.new" },
          { "win": "ctrl+shift+5", "linux": "ctrl+shift+5", "mac": "cmd+\\", "command": "terminal.split", "when": "focusedView == terminal" },
          { "key": "primary+f", "command": "terminal.find", "when": "focusedView == terminal" },
          { "key": "alt+right", "command": "terminal.focusNext", "when": "focusedView == terminal" },
          { "key": "alt+left", "command": "terminal.focusPrevious", "when": "focusedView == terminal" }
        ]
        """;

    public static ToolWindowDescriptor TerminalToolWindow() =>
        new(TerminalViewModel.ToolWindowId, "Terminal", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "output" };

    public static void RegisterTerminalCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Action<string?> handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = "Terminal",
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        // Replaces the tool window's own workbench.view.terminal (registered by SyncViewCommands before this runs):
        // Ctrl+` toggles the window instead of only showing it.
        Add("workbench.view.terminal", "Terminal", _ => shell.ToggleTerminal());
        Add("terminal.new", "New Terminal", profile => shell.ShowTerminal(newTerminal: true, profile));
        Add("terminal.split", "Split Terminal", _ => shell.TerminalWindow.Split(), "focusedView == terminal");
        Add("terminal.kill", "Kill Terminal", _ => shell.TerminalWindow.Kill(shell.TerminalWindow.Selected));
        Add("terminal.clear", "Clear Terminal", _ => shell.TerminalWindow.ClearCommand.Execute(null));
        Add("terminal.find", "Find in Terminal", _ => shell.TerminalWindow.FindVisible = true, "focusedView == terminal");
        Add("terminal.focusNext", "Focus Next Terminal", _ => Cycle(shell, 1), "focusedView == terminal");
        Add("terminal.focusPrevious", "Focus Previous Terminal", _ => Cycle(shell, -1), "focusedView == terminal");
        Add("terminal.openInTerminal", "Open in Terminal", path => shell.OpenInTerminal(path), palette: false);
        Add("terminal.runSelectedText", "Run Selected Text in Terminal", _ => shell.RunSelectionInTerminal(), "activeDocumentKind == textFile");
    }

    private static void Cycle(ShellHost shell, int delta)
    {
        var tabs = shell.TerminalWindow.Tabs;
        if (tabs.Count > 1 && shell.TerminalWindow.Selected is { } current)
        {
            shell.TerminalWindow.Selected = tabs[(tabs.IndexOf(current) + delta + tabs.Count) % tabs.Count];
        }
    }

    public static void RegisterTerminalMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(SolutionExplorerView.ContextMenuLocation, "terminal.openInTerminal", "5-path", 3, "explorerItemKind == folder || explorerItemKind == project || explorerItemKind == solution"),
        ]);
    }
}
