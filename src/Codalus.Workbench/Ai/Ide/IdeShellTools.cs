namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: The IDE itself: its commands (under each command's own enablement, never the ones agents don't get), its settings, a picture of a window, and the integrated terminal.</summary>
public static class IdeShellTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        IdeTools.Read("list_commands", "The commands of Codalus IDE that agents can run with run_command (menus, the command palette, plugins'), with their titles, key bindings and whether each is enabled now. The commands agents never get (exit, settings sync, plugins, sign-ins, updates, trust, AI settings, external pages) aren't listed.", """{"filter":{"type":"string","description":"Only the commands whose id or title contains this text (case-insensitive)."}}"""),
        IdeTools.Action("run_command", "Run a command of Codalus IDE by its id (list_commands lists them), as its menu item or the command palette does, under the command's own enablement: a disabled command is refused, saying why. A dialog or picker it opens is cancelled, since an agent can't answer it; the result says so, with the notifications it showed.", """{"id":{"type":"string","description":"The command's id, e.g. workbench.view.errorList."},"args":{"type":"array","description":"The command's arguments, for the commands that take any (most take none)."}}""", "id"),
        IdeTools.Read("get_settings", "Codalus IDE's settings: every one set to something other than its default (in the user's or the workspace's settings), or the ones named, with their effective value, default and description. Secrets are never included.", """{"keys":{"type":"array","items":{"type":"string"},"description":"Setting keys (e.g. editor.tabSize), or prefixes ending in a dot (e.g. editor.) for every setting under them."}}"""),
        IdeTools.Read("ide_screenshot", "A PNG picture of Codalus IDE's main window as the user sees it, or of one tool window or document showing in it (by its id or title).", """{"window":{"type":"string","description":"A tool window's id or title (e.g. errorList, Terminal), or a document's title; the whole main window without one."}}"""),
        IdeTools.Action("terminal_send", "Type text into Codalus IDE's integrated terminal (the active one, or a new one in the workspace), as the user would, then return the terminal's visible text once its output settles. The user sees the terminal and what runs in it.", """{"text":{"type":"string","description":"What to type."},"enter":{"type":"boolean","description":"Press Enter after the text (default true)."},"newTerminal":{"type":"boolean","description":"Open a new terminal instead of using the active one."},"waitSeconds":{"type":"integer","description":"How long to wait for the output to settle (default 3, at most 60)."}}""", "text"),
    ];

    /// <summary>
    /// The commands run_command never runs, by id (D52): the ones that end or restart the app, or reach past the IDE
    /// into what is the user's alone to decide. Ending the app would end the agent's own connection; settings sync,
    /// plugins (installing, removing, turning on or off, their sources), sign-ins, accounts and credentials, updates,
    /// workspace trust, and AI's own settings and agents are choices of the user's, and an agent that could make them
    /// could give itself more than the user allowed; opening an external address leaves the IDE for the browser, and
    /// pushing or publishing sends the user's work off the machine. <see cref="DeniedPrefixes"/> and
    /// <see cref="DeniedWords"/> extend the list to the commands of each kind added later. Beyond these, the shell skips
    /// the commands the command sweep marks as leaving the machine, and cancels any dialog or picker a command opens,
    /// since nobody would answer it.
    /// </summary>
    public static IReadOnlySet<string> DeniedCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "workbench.quit",
        "workbench.restart",
        "workbench.closeWindow",
        "workbench.newWindow",
        "workbench.openExternal",
        "workbench.setSetting",
        "workbench.importSettings",
        "workbench.importVsCodeSettings",
        "workbench.importVsSettings",
        "workbench.exportSettings",
        "workbench.openPluginsFolder",
        "workbench.browsePluginFiles",
        "solution.trust",
        "remote.open",
        "remote.reopenInContainer",
        "debug.justInTime",
        "help.reportProblem",
        "help.suggestFeature",
        "aspire.openDashboard",
    };

    /// <summary>Id prefixes of whole areas agents never get (see <see cref="DeniedCommands"/>).</summary>
    public static IReadOnlyList<string> DeniedPrefixes { get; } =
    [
        "settingsSync.", // settings sync: the user's repository and their other machines
        "plugins.", // installing, removing, turning on or off, updating plugins, and their sources
        "update.", // checking for and applying updates, which restarts the app
        "accounts.", // sign-ins and their accounts
        "hosting.", // publishing to the user's hosting accounts
        "ai.", // AI's settings, providers and agents, and starting other agents (AI Console)
        "profiles.", // the user's profiles switch every setting and plugin at once
    ];

    /// <summary>Words that deny a command wherever they are a word of its id (<see cref="HasWord"/>; see <see cref="DeniedCommands"/>).</summary>
    public static IReadOnlyList<string> DeniedWords { get; } =
    [
        "signIn", "signOut", "login", "logout", "account", "credential", "token", "secret", // sign-ins and secrets
        "trust", // workspace trust and the OS's certificate trust
        "install", // installs on the machine (SDKs, tools, language servers, plugins)
        "push", "publish", // the user's work off the machine
        "openInBrowser", "openExternal", // an external address
        "quit", "exit", "restart", // the app itself (debug.restart is the debugger's, and allowed)
    ];

    /// <summary>Denied words that a command may still have for something agents may do.</summary>
    private static readonly HashSet<string> s_allowedDespiteWords = new(StringComparer.Ordinal)
    {
        "debug.restart", // restarts the debug session
    };

    /// <summary>Why agents never get <paramref name="id"/>, or null when they may run it.</summary>
    public static string? DeniedReason(string id)
    {
        if (DeniedCommands.Contains(id))
        {
            return "agents never get it (it ends the app, changes what is the user's alone to decide, or leaves the IDE)";
        }

        if (DeniedPrefixes.FirstOrDefault(p => id.StartsWith(p, StringComparison.Ordinal)) is { } prefix)
        {
            return $"agents never get the '{prefix}' commands";
        }

        if (!s_allowedDespiteWords.Contains(id) && DeniedWords.FirstOrDefault(w => HasWord(id, w)) is { } word)
        {
            return $"agents never get commands that {WordReason(word)}";
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="word"/> is a word of <paramref name="id"/>: it starts a segment or a camel-case hump and
    /// ends one (a plural's s allowed), so <c>run.configureStartupProjects</c> has no "restart" and
    /// <c>web.manageUserSecrets</c> has "secret".
    /// </summary>
    internal static bool HasWord(string id, string word)
    {
        for (var at = id.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = id.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if (end < id.Length && id[end] == 's' && (end + 1 == id.Length || !char.IsAsciiLetterLower(id[end + 1])))
            {
                end++;
            }

            var starts = at == 0 || id[at - 1] == '.' || char.IsAsciiLetterUpper(id[at]);
            var ends = end == id.Length || !char.IsAsciiLetterLower(id[end]);
            if (starts && ends)
            {
                return true;
            }
        }

        return false;
    }

    private static string WordReason(string word) => word switch
    {
        "trust" => "change trust",
        "install" => "install software",
        "push" or "publish" => "send the user's work off the machine",
        "openInBrowser" or "openExternal" => "open external addresses",
        "quit" or "exit" or "restart" => "end or restart the app",
        _ => "sign in, sign out, or handle accounts, credentials and secrets",
    };
}
