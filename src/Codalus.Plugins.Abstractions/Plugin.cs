using System.Text.Json;

namespace Codalus.Plugins;

/// <summary>Constants of this version of the plugin API.</summary>
public static class PluginApi
{
    /// <summary>
    /// The API version that a plugin's <c>engines.codalus</c> range is checked against. Since 1.0 (M22a), minor versions
    /// only add to the API and a member goes away only after a major version, deprecated first with the
    /// <see cref="DeprecationDiagnostic"/> warning (ADR 0003, docs/plugins.md#changelog).
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>The diagnostic id of deprecated API members (<c>[Obsolete(..., DiagnosticId = "CODALUS0001")]</c>).</summary>
    public const string DeprecationDiagnostic = "CODALUS0001";
}

/// <summary>
/// The entry point of a plugin. The plugin host creates one instance (it needs a public parameterless
/// constructor) and calls <see cref="ActivateAsync"/> when one of the plugin's activation events fires.
/// </summary>
public interface ICodalusPlugin
{
    /// <summary>Registers commands, views and status items. Keep it fast: it runs on the user's critical path.</summary>
    Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>Called on shutdown or when the plugin is disabled. <see cref="IPluginContext.Subscriptions"/> are disposed afterwards.</summary>
    Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Identity of the running plugin.</summary>
/// <param name="Id">The manifest id, <c>publisher.name</c>.</param>
/// <param name="Version">The plugin's SemVer version.</param>
/// <param name="InstallPath">The folder containing <c>codalus-plugin.json</c>.</param>
public sealed record PluginInfo(string Id, string Version, string InstallPath);

/// <summary>Everything a plugin can use. Obtained in <see cref="ICodalusPlugin.ActivateAsync"/>.</summary>
public interface IPluginContext
{
    /// <summary>The running plugin's identity.</summary>
    PluginInfo Plugin { get; }

    /// <summary>Register and execute commands.</summary>
    ICommands Commands { get; }

    /// <summary>Notifications, Output channels, status bar items and tool window content.</summary>
    IWindow Window { get; }

    /// <summary>Effective settings and change notifications.</summary>
    ISettings Settings { get; }

    /// <summary>The plugin's log, shown in its Output channel.</summary>
    IPluginLog Log { get; }

    /// <summary>Disposables owned by the plugin; disposed when it is deactivated.</summary>
    IList<IDisposable> Subscriptions { get; }

    // The services below were proposed APIs (0.2.1 to 0.2.7) and are stable since 1.0. The plugin host implements
    // every one; the defaults let a test's own context implement only what it needs.

    /// <summary>Test controllers for the Test Explorer (ADR 0010).</summary>
    ITests Tests => throw new NotSupportedException("This context has no test controllers.");

    /// <summary>The open solution, its projects, packages and outputs, and test candidates from the language service.</summary>
    IWorkspace Workspace => throw new NotSupportedException("This context has no workspace.");

    /// <summary>Builds through the IDE's build (one at a time, with the Build output).</summary>
    IBuild Build => throw new NotSupportedException("This context has no build.");

    /// <summary>Debug sessions through the IDE's debugger, and debugger visualizers.</summary>
    IDebug Debug => throw new NotSupportedException("This context has no debugger.");

    /// <summary>Integrated terminals: open a shell or run a command in one, send it text, read its output.</summary>
    ITerminals Terminals => throw new NotSupportedException("This context has no terminals.");

    /// <summary>Git hosting providers: pull requests, review comments, CI status, work items, publishing (ADR 0031).</summary>
    IHosting Hosting => throw new NotSupportedException("This context has no hosting.");

    /// <summary>The plugin's secrets, in the OS credential store.</summary>
    ISecrets Secrets => throw new NotSupportedException("This context has no secrets.");
}

/// <summary>The arguments a command was invoked with (JSON values, so commands work across processes).</summary>
public sealed class CommandInvocation(IReadOnlyList<JsonElement> args)
{
    /// <summary>The raw JSON arguments.</summary>
    public IReadOnlyList<JsonElement> Args { get; } = args;

    /// <summary>Deserializes argument <paramref name="index"/>, or returns <paramref name="fallback"/> when absent or null.</summary>
    public T? Arg<T>(int index, T? fallback = default) =>
        index < Args.Count && Args[index].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? Args[index].Deserialize<T>()
            : fallback;
}

/// <summary>The command system, shared by the shell and all plugins.</summary>
public interface ICommands
{
    /// <summary>
    /// Provides the implementation of a command. Commands declared in the manifest appear in menus and the command
    /// palette before activation; registering an undeclared id creates a hidden command that other code can execute.
    /// The handler's result (any JSON-serializable value) is returned to the caller.
    /// </summary>
    IDisposable Register(string commandId, Func<CommandInvocation, CancellationToken, Task<object?>> handler);

    /// <summary>Provides a command implementation without arguments or result.</summary>
    IDisposable Register(string commandId, Action handler);

    /// <summary>Executes any command (built-in or contributed) and returns its JSON result.</summary>
    Task<JsonElement?> ExecuteAsync(string commandId, CancellationToken cancellationToken, params object?[] args);
}

/// <summary>Severity of a notification.</summary>
public enum NotificationSeverity
{
    /// <summary>Informational; dismissed automatically after a few seconds.</summary>
    Info,

    /// <summary>Something needs the user's attention.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>Side of the status bar.</summary>
public enum StatusBarAlignment
{
    /// <summary>Left side; higher priority is further left.</summary>
    Left,

    /// <summary>Right side; higher priority is further right.</summary>
    Right,
}

/// <summary>The parts of the workbench UI a plugin can contribute to.</summary>
public interface IWindow
{
    /// <summary>Shows a notification. Completes with the chosen action, or null when the user dismissed it.</summary>
    Task<string?> ShowNotificationAsync(NotificationSeverity severity, string message, IReadOnlyList<string>? actions = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the user pick one of <paramref name="items"/> in the command palette (fuzzy filtering, keyboard).
    /// Completes with the chosen item, or null when cancelled. Since API 0.1.1.
    /// </summary>
    Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, string? placeholder = null, CancellationToken cancellationToken = default);

    /// <summary>Asks the user for a line of text. Completes with the text, or null when cancelled. Since API 0.1.1.</summary>
    Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> as a task the user sees in the status bar, with what it reports (since 1.0). When
    /// <paramref name="cancellable"/>, the user can cancel it: the token passed to <paramref name="work"/> is cancelled.
    /// </summary>
    Task<T> WithProgressAsync<T>(string title, Func<IProgress<ProgressReport>, CancellationToken, Task<T>> work, bool cancellable = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This window has no progress.");

    /// <summary>A channel of the Output window, shown as "&lt;plugin&gt;: &lt;name&gt;".</summary>
    IOutputChannel CreateOutputChannel(string name);

    /// <summary>A status bar entry. It stays hidden until <see cref="IStatusBarItem.Show"/>.</summary>
    IStatusBarItem CreateStatusBarItem(string id, StatusBarAlignment alignment = StatusBarAlignment.Left, int priority = 0);

    /// <summary>Supplies the content of a tool window declared with <c>"kind": "tree"</c>.</summary>
    IDisposable RegisterTreeDataProvider<T>(string viewId, ITreeDataProvider<T> provider)
        where T : notnull;

    /// <summary>Sets the content of a tool window declared with <c>"kind": "markdown"</c>.</summary>
    void SetMarkdown(string viewId, string markdown);
}

/// <summary>A text channel in the Output window.</summary>
public interface IOutputChannel
{
    /// <summary>The channel's display name.</summary>
    string Name { get; }

    /// <summary>Appends text as-is.</summary>
    void Append(string text);

    /// <summary>Appends text followed by a newline.</summary>
    void AppendLine(string line);

    /// <summary>Removes all text.</summary>
    void Clear();

    /// <summary>Reveals the channel in the Output window.</summary>
    void Show();
}

/// <summary>An entry in the status bar. Property changes are pushed to the shell while it is shown.</summary>
public interface IStatusBarItem : IDisposable
{
    /// <summary>The visible text.</summary>
    string Text { get; set; }

    /// <summary>Hover text.</summary>
    string? Tooltip { get; set; }

    /// <summary>Command executed when the item is clicked.</summary>
    string? Command { get; set; }

    /// <summary>Makes the item visible.</summary>
    void Show();

    /// <summary>Hides the item (it can be shown again).</summary>
    void Hide();
}

/// <summary>Whether a tree item can be expanded, and its initial state.</summary>
public enum TreeItemCollapsibleState
{
    /// <summary>A leaf.</summary>
    None,

    /// <summary>Has children; initially collapsed.</summary>
    Collapsed,

    /// <summary>Has children; initially expanded.</summary>
    Expanded,
}

/// <summary>A command run when a tree item is activated (double-click or Enter).</summary>
/// <param name="CommandId">The command to execute.</param>
/// <param name="Args">JSON-serializable arguments.</param>
public sealed record TreeItemCommand(string CommandId, params object?[] Args);

/// <summary>How an element of a tree view is displayed. <see cref="Icon"/> names an icon of the shell's built-in set.</summary>
/// <param name="Label">The main text.</param>
public sealed record TreeItem(string Label)
{
    /// <summary>Secondary text shown after the label, dimmed.</summary>
    public string? Description { get; init; }

    /// <summary>Hover text.</summary>
    public string? Tooltip { get; init; }

    /// <summary>A built-in icon name, e.g. <c>file</c>, <c>folder</c>, <c>gear</c>, <c>play</c>, <c>info</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Whether the item has children.</summary>
    public TreeItemCollapsibleState Collapsible { get; init; }

    /// <summary>Command executed when the item is activated.</summary>
    public TreeItemCommand? Command { get; init; }
}

/// <summary>Arguments of <see cref="ITreeDataProvider{T}.Changed"/>.</summary>
/// <typeparam name="T">The provider's element type.</typeparam>
public sealed class TreeDataChangedEventArgs<T>(T? element) : EventArgs
{
    /// <summary>The element whose children changed, or default for the whole tree.</summary>
    public T? Element { get; } = element;
}

/// <summary>Provides the elements of a tree view lazily, one level at a time.</summary>
/// <typeparam name="T">The plugin's element type; the host maps elements to opaque handles.</typeparam>
public interface ITreeDataProvider<T>
    where T : notnull
{
    /// <summary>Children of <paramref name="element"/>, or the root elements when it is null/default.</summary>
    Task<IReadOnlyList<T>> GetChildrenAsync(T? element, CancellationToken cancellationToken);

    /// <summary>How <paramref name="element"/> is displayed.</summary>
    TreeItem GetTreeItem(T element);

    /// <summary>Raise to make the shell re-read (part of) the tree.</summary>
    event EventHandler<TreeDataChangedEventArgs<T>>? Changed;
}

/// <summary>Arguments of <see cref="ISettings.Changed"/>.</summary>
public sealed class SettingsChangedEventArgs(IReadOnlyCollection<string> keys) : EventArgs
{
    /// <summary>The keys whose effective value changed.</summary>
    public IReadOnlyCollection<string> Keys { get; } = keys;

    /// <summary>True if <paramref name="keyOrSection"/> or any key under it changed.</summary>
    public bool Affects(string keyOrSection) =>
        Keys.Any(k => k == keyOrSection || k.StartsWith(keyOrSection + ".", StringComparison.Ordinal));
}

/// <summary>Read access to effective settings (including the plugin's own contributed settings).</summary>
public interface ISettings
{
    /// <summary>The setting's value, or <paramref name="fallback"/> when unset or not convertible.</summary>
    T GetValue<T>(string key, T fallback);

    /// <summary>Raised when effective values change (user, workspace or default).</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}

/// <summary>Severity of a log message.</summary>
public enum PluginLogLevel
{
    /// <summary>Diagnostic detail.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Info,

    /// <summary>Something unexpected that the plugin recovered from.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>Writes to the plugin's Output channel; warnings and errors are also logged by the shell.</summary>
public interface IPluginLog
{
    /// <summary>Writes one message.</summary>
    void Write(PluginLogLevel level, string message);
}

/// <summary>Convenience methods for <see cref="IPluginLog"/>.</summary>
public static class PluginLogExtensions
{
    /// <summary>Writes a <see cref="PluginLogLevel.Debug"/> message.</summary>
    public static void Debug(this IPluginLog log, string message) => log.Write(PluginLogLevel.Debug, message);

    /// <summary>Writes a <see cref="PluginLogLevel.Info"/> message.</summary>
    public static void Info(this IPluginLog log, string message) => log.Write(PluginLogLevel.Info, message);

    /// <summary>Writes a <see cref="PluginLogLevel.Warning"/> message.</summary>
    public static void Warning(this IPluginLog log, string message) => log.Write(PluginLogLevel.Warning, message);

    /// <summary>Writes a <see cref="PluginLogLevel.Error"/> message.</summary>
    public static void Error(this IPluginLog log, string message) => log.Write(PluginLogLevel.Error, message);
}

/// <summary>An entry of <see cref="IWindow.ShowQuickPickAsync"/>. Since API 0.1.1.</summary>
/// <param name="Label">The text that is filtered and shown.</param>
public sealed record QuickPickItem(string Label)
{
    /// <summary>Muted text shown after the label.</summary>
    public string? Description { get; init; }

    /// <summary>Muted text shown on the right (for example a key or a path).</summary>
    public string? Detail { get; init; }

    /// <summary>Your own data; not sent to the shell.</summary>
    public object? Tag { get; init; }
}

/// <summary>What <see cref="IWindow.ShowInputBoxAsync"/> asks. Since API 0.1.1.</summary>
/// <param name="Title">The question, for example "Project name".</param>
public sealed record InputBoxOptions(string Title)
{
    /// <summary>Explanatory text under the title.</summary>
    public string? Prompt { get; init; }

    /// <summary>The initial text.</summary>
    public string? Value { get; init; }
}

/// <summary>What a task reports: a message, and how far it is (0 to 100). A null keeps the previous value.</summary>
public sealed record ProgressReport(string? Message = null, double? Percent = null);
