using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>AI assistance (M28a, ADR 0062): the AI Chat window, its commands, menus and key binding.</summary>
internal static partial class BuiltIns
{
    public const string ToolsAiMenu = "menuBar/tools/ai";
    public const string EditorAiMenu = "editor/context/ai";

    public const string AiKeybindings =
        """
        [
          { "key": "primary+alt+i", "command": "ai.chat.show" }
        ]
        """;

    public static IEnumerable<ToolWindowDescriptor> AiToolWindows()
    {
        yield return new ToolWindowDescriptor(AiChatViewModel.ToolWindowId, "AI Chat", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "sparkle" };
    }

    public static void RegisterAiCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "AI", Enablement = WhenClause.Parse(enablement) },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });
        Task Sync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        Add("ai.chat.show", "AI Chat", () => Sync(shell.ShowAiChat));
        Add("ai.enable", "Turn On AI Assistance", shell.TurnOnAiAsync, "!aiEnabled");
        Add("ai.chooseProvider", "Choose AI Provider…", shell.ChooseAiProviderAsync, "aiEnabled");
        Add("ai.configureProvider", "Configure AI Provider…", shell.ConfigureAiProviderAsync, "aiEnabled");
        Add("ai.toggleForWorkspace", "Turn AI Off or On for This Workspace", shell.TurnOffAiForWorkspaceAsync, "solutionOpen");
        Add("ai.showLog", "Show AI Request Log", () => Sync(shell.ShowAiLog));
        Add("ai.askAboutSelection", "Ask AI About the Selection", () => Sync(() => shell.AskAi(string.Empty)), "aiEnabled && editorHasSelection");
        Add("ai.explainSelection", "Explain the Selection", () => Sync(() => shell.AskAiNow("Explain what this code does.")), "aiEnabled && editorHasSelection");
        Add("ai.fixDiagnostic", "Fix with AI", shell.FixDiagnosticWithAiAsync, "aiEnabled && activeDocumentKind == textFile");
        Add("ai.generateTests", "Generate Tests with AI", shell.GenerateTestsWithAiAsync, "aiEnabled && activeDocumentKind == textFile");
        Add("ai.generateCommitMessage", "Write the Commit Message with AI", shell.GenerateCommitMessageAsync, "aiEnabled && gitRepository");
        Add("ai.explainTest", "Ask AI Why the Test Fails", shell.ExplainTestWithAiAsync, "aiEnabled && testExplorerSelection");

        // External agents from templates (D43, ADR 0083): offered while AI is off too (Connect can turn it on).
        Add("ai.agents.connect", "Connect to an Agent…", () => shell.ConnectExternalAgentAsync());
        foreach (var template in Workbench.Ai.Acp.AcpAgentTemplates.All)
        {
            var id = template.Id;
            commands.Register(
                new CommandDescriptor("ai.agents.connect." + id, ConnectTitle(id, template.Name), ContributionSource.Shell) { Category = "AI" },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(() => shell.ConnectExternalAgentAsync(id)).ConfigureAwait(false);
                    return null;
                });
        }

        Add("ai.agents.manage", "Manage Agents…", shell.ManageExternalAgentsAsync, "externalAgentsConfigured");
        Add("ai.agents.check", "Check an Agent's Connection…", () => shell.CheckExternalAgentAsync(), "externalAgentsConfigured");
        Add("ai.agents.disconnect", "Disconnect an Agent…", () => shell.DisconnectExternalAgentAsync(), "externalAgentsConfigured");
    }

    /// <summary>A template's Connect command title, written out for the catalogue (each is a key of its own).</summary>
    private static string ConnectTitle(string id, string name) => id switch
    {
        "claude" => "Connect to Claude…",
        "codex" => "Connect to Codex…",
        "gemini" => "Connect to Gemini CLI…",
        "copilot" => "Connect to GitHub Copilot…",
        "pi" => "Connect to pi…",
        "opencode" => "Connect to OpenCode…",
        "goose" => "Connect to goose…",
        _ => "Connect to " + name + "…",
    };

    public static void RegisterAiMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        var order = 0;
        MenuItemRule Item(string location, string command, string group, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = ++order, When = WhenClause.Parse(when), Title = title };
        var tools = MenuLocations.MenuBar("tools");
        var view = MenuLocations.MenuBar("view");
        menus.Add(
        [
            Item(view, "ai.chat.show", "view") with { Order = 86 },
            MenuItemRule.ForSubmenu(tools, ToolsAiMenu, "AI", shell) with { Group = "1-data", Order = 50 },
            Item(ToolsAiMenu, "ai.chat.show", "1-chat"),
            Item(ToolsAiMenu, "ai.generateCommitMessage", "1-chat", "aiEnabled"),
            Item(ToolsAiMenu, "ai.explainTest", "1-chat", "aiEnabled"),
            Item(ToolsAiMenu, "ai.enable", "2-setup", "!aiEnabled"),
            Item(ToolsAiMenu, "ai.chooseProvider", "2-setup", "aiEnabled"),
            Item(ToolsAiMenu, "ai.configureProvider", "2-setup", "aiEnabled"),
            Item(ToolsAiMenu, "ai.agents.connect.claude", "2-setup"),
            Item(ToolsAiMenu, "ai.agents.connect", "2-setup"),
            Item(ToolsAiMenu, "ai.agents.manage", "2-setup", "externalAgentsConfigured"),
            Item(ToolsAiMenu, "ai.toggleForWorkspace", "3-privacy"),
            Item(ToolsAiMenu, "ai.showLog", "3-privacy"),
            MenuItemRule.ForSubmenu(MenuLocations.EditorContext, EditorAiMenu, "AI", shell) with { Group = "5-send", Order = 100, When = WhenClause.Parse("aiEnabled") },
            Item(EditorAiMenu, "ai.explainSelection", "1-ask", "editorHasSelection"),
            Item(EditorAiMenu, "ai.askAboutSelection", "1-ask", "editorHasSelection"),
            Item(EditorAiMenu, "ai.fixDiagnostic", "2-act"),
            Item(EditorAiMenu, "ai.generateTests", "2-act"),
            Item(EditorAiMenu, "ai.chat.show", "3-chat"),
        ]);
    }
}
