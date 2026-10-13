namespace Codalus.Plugins.Protocol;

/// <summary>Method names of the shell ↔ plugin host protocol (docs/plugin-protocol.md).</summary>
public static class PluginMethods
{
    /// <summary>Protocol version (major.minor), negotiated in <see cref="Initialize"/>.</summary>
    public const string ProtocolVersion = "0.2";

    /// <summary>The plugin API version that <c>engines.codalus</c> ranges are checked against.</summary>
    public const string ApiVersion = "1.1.0";

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

    /// <summary>Host → shell: a plugin's task starts (API 1.0); <c>window/progressReport</c> and <c>window/progressEnd</c> follow.</summary>
    public const string ProgressStart = "window/progressStart";
    public const string ProgressReport = "window/progressReport";
    public const string ProgressEnd = "window/progressEnd";

    /// <summary>Shell → host: the user cancelled a cancellable task.</summary>
    public const string ProgressCancel = "window/progressCancel";

    /// <summary>Host → shell: a declarative view's content (API 1.0).</summary>
    public const string ViewSet = "view/set";

    /// <summary>Shell → host (request): the user acted in a declarative view.</summary>
    public const string ViewEvent = "view/event";

    /// <summary>Shell → host (request): a file opens in the plugin's custom editor.</summary>
    public const string CustomEditorOpen = "customEditor/open";

    /// <summary>Shell → host: a custom editor's document closed.</summary>
    public const string CustomEditorClose = "customEditor/close";

    /// <summary>Host → shell: a custom editor's file has new text.</summary>
    public const string CustomEditorSetText = "customEditor/setText";

    /// <summary>Host → shell: a plugin serves a file system scheme (API 1.0), or stops (<c>fs/unregister</c>).</summary>
    public const string FsRegister = "fs/register";
    public const string FsUnregister = "fs/unregister";

    /// <summary>Host → shell: files of a scheme changed.</summary>
    public const string FsChanged = "fs/changed";

    /// <summary>Host → shell: a source control provider (API 1.0) comes, shows its state, or goes.</summary>
    public const string ScmRegister = "scm/register";
    public const string ScmUpdate = "scm/update";
    public const string ScmUnregister = "scm/unregister";

    /// <summary>Shell → host (requests): commit with the message; a file's original.</summary>
    public const string ScmAccept = "scm/accept";
    public const string ScmOriginal = "scm/original";

    /// <summary>Host → shell: an authentication provider is offered (API 1.0), withdrawn, or its sessions changed.</summary>
    public const string AuthRegister = "auth/register";
    public const string AuthUnregister = "auth/unregister";
    public const string AuthSessionsChanged = "auth/sessionsChanged";

    /// <summary>Host → shell (request): a plugin asks for a session of a provider.</summary>
    public const string AuthGetSession = "auth/getSession";

    /// <summary>Shell → host (requests): a provider's operations.</summary>
    public const string AuthGetSessions = "auth/getSessions";
    public const string AuthCreateSession = "auth/createSession";
    public const string AuthRemoveSession = "auth/removeSession";

    /// <summary>Shell → host (requests): the provider's operations.</summary>
    public const string FsStat = "fs/stat";
    public const string FsReadDirectory = "fs/readDirectory";
    public const string FsReadFile = "fs/readFile";
    public const string FsWriteFile = "fs/writeFile";
    public const string FsCreateDirectory = "fs/createDirectory";
    public const string FsDelete = "fs/delete";
    public const string FsRename = "fs/rename";
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

/// <summary>Where a plugin's file system scheme shows on this machine (API 1.0): a root the editor's file systems mount.</summary>
public static class FileSystemRoots
{
    public static string RootOf(string scheme) =>
        OperatingSystem.IsWindows() ? @"\\codalus-plugin.invalid\" + scheme : "/codalus-plugin/" + scheme;
}

/// <summary>JSON-RPC error codes of file system operations (the shell maps them back to exceptions).</summary>
public static class FsErrors
{
    public const int NotFound = -33100;
    public const int Denied = -33101;
    public const int Failed = -33102;
}
