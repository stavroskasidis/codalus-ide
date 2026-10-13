namespace Codalus.Plugins.Protocol;

/// <summary>Method names of the shell ↔ plugin host protocol (docs/plugin-protocol.md).</summary>
public static class PluginMethods
{
    /// <summary>Protocol version (major.minor), negotiated in <see cref="Initialize"/>.</summary>
    public const string ProtocolVersion = "0.2";

    /// <summary>The plugin API version that <c>engines.codalus</c> ranges are checked against.</summary>
    public const string ApiVersion = "0.3.0";

    // shell → host
    public const string Initialize = "initialize";
    public const string LoadPlugins = "plugins/load";
    public const string ActivatePlugin = "plugins/activate";
    public const string DeactivatePlugin = "plugins/deactivate";
    public const string ExecuteCommand = "commands/execute";
    public const string GetTreeChildren = "treeView/getChildren";
    public const string SettingsDidChange = "settings/didChange";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    // host → shell
    public const string RegisterCommand = "commands/register";
    public const string UnregisterCommand = "commands/unregister";
    public const string ExecuteCommandInShell = "commands/executeInShell";
    public const string ShowNotification = "window/showNotification";

    /// <summary>Since API 0.1.1.</summary>
    public const string ShowQuickPick = "window/showQuickPick";

    /// <summary>Since API 0.1.1.</summary>
    public const string ShowInputBox = "window/showInputBox";
    public const string OutputAppend = "output/append";
    public const string OutputClear = "output/clear";
    public const string OutputShow = "output/show";
    public const string StatusBarUpdate = "statusBar/update";
    public const string StatusBarRemove = "statusBar/remove";
    public const string TreeViewRefresh = "treeView/refresh";
    public const string MarkdownViewSet = "markdownView/set";
    public const string Log = "log";
}

/// <summary>Codalus-specific JSON-RPC error codes.</summary>
public static class PluginErrorCodes
{
    public const int PluginNotFound = -33000;
    public const int PluginNotActivated = -33001;
    public const int ActivationFailed = -33002;
    public const int CommandFailed = -33003;
    public const int ViewNotFound = -33004;

    /// <summary>A proposed API the shell doesn't offer (here), or a plugin that didn't enable proposed APIs.</summary>
    public const int ProposedApiUnavailable = -33005;
}
