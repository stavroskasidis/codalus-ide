using System.Collections.Concurrent;
using System.Text;

namespace Codalus.Plugins.Testing;

/// <summary>A notification the plugin showed, and the action the test's responder chose.</summary>
/// <param name="Severity">Info, warning or error.</param>
/// <param name="Message">Its text.</param>
/// <param name="Actions">Its buttons.</param>
/// <param name="Chosen">What <see cref="TestWindow.NotificationResponder"/> answered (null: dismissed).</param>
public sealed record ShownNotification(NotificationSeverity Severity, string Message, IReadOnlyList<string> Actions, string? Chosen);

/// <summary>A task the plugin ran with <see cref="IWindow.WithProgressAsync{T}"/>.</summary>
public sealed class ProgressRecord
{
    private readonly ConcurrentQueue<ProgressReport> _reports = new();

    internal ProgressRecord(string title, bool cancellable, CancellationTokenSource cancel)
    {
        Title = title;
        Cancellable = cancellable;
        Cancellation = cancel;
    }

    /// <summary>Its title.</summary>
    public string Title { get; }

    /// <summary>Whether the user could cancel it.</summary>
    public bool Cancellable { get; }

    /// <summary>What it reported, in order.</summary>
    public IReadOnlyList<ProgressReport> Reports => [.. _reports];

    /// <summary>Whether it has finished (completed, failed or cancelled).</summary>
    public bool IsDone { get; internal set; }

    internal CancellationTokenSource Cancellation { get; }

    /// <summary>Cancels it, as the status bar's ✕ does. Throws for a task that isn't cancellable.</summary>
    public void Cancel()
    {
        if (!Cancellable)
        {
            throw new InvalidOperationException($"The task '{Title}' isn't cancellable.");
        }

        Cancellation.Cancel();
    }

    internal void Add(ProgressReport report) => _reports.Enqueue(report);
}

/// <summary>An output channel in a test: its text.</summary>
public sealed class TestOutputChannel(string name) : IOutputChannel
{
    private readonly StringBuilder _text = new();

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <summary>Everything appended since the last clear.</summary>
    public string Text
    {
        get
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Whether the plugin asked to show it.</summary>
    public bool Shown { get; private set; }

    /// <inheritdoc/>
    public void Append(string text)
    {
        lock (_text)
        {
            _text.Append(text);
        }
    }

    /// <inheritdoc/>
    public void AppendLine(string line)
    {
        lock (_text)
        {
            _text.Append(line).Append('\n');
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_text)
        {
            _text.Clear();
        }
    }

    /// <inheritdoc/>
    public void Show() => Shown = true;
}

/// <summary>A status bar item in a test.</summary>
public sealed class TestStatusBarItem(string id, StatusBarAlignment alignment, int priority) : IStatusBarItem
{
    /// <summary>Its id.</summary>
    public string Id { get; } = id;

    /// <summary>Its side of the status bar.</summary>
    public StatusBarAlignment Alignment { get; } = alignment;

    /// <summary>Its priority.</summary>
    public int Priority { get; } = priority;

    /// <inheritdoc/>
    public string Text { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string? Tooltip { get; set; }

    /// <inheritdoc/>
    public string? Command { get; set; }

    /// <summary>Whether it is shown.</summary>
    public bool Visible { get; private set; }

    /// <summary>Whether the plugin disposed it.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc/>
    public void Show() => Visible = true;

    /// <inheritdoc/>
    public void Hide() => Visible = false;

    /// <inheritdoc/>
    public void Dispose()
    {
        Visible = false;
        Disposed = true;
    }
}

/// <summary>A tree view's rows, as the IDE asks for them: what <see cref="ITreeDataProvider{T}.GetTreeItem"/> gave, and the element.</summary>
/// <param name="Item">The row.</param>
/// <param name="Element">The plugin's element, to expand it with <see cref="TestWindow.GetTreeChildrenAsync"/>.</param>
public sealed record TestTreeNode(TreeItem Item, object Element);

/// <summary>
/// The window in a test: it records what the plugin shows, answers its questions with the test's responders, and lets
/// the test act on its trees, views, tasks and custom editors.
/// </summary>
public sealed class TestWindow : IWindow
{
    private readonly TestCommands _commands;
    private readonly ConcurrentQueue<ShownNotification> _notifications = new();
    private readonly ConcurrentDictionary<string, TestOutputChannel> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TestStatusBarItem> _statusItems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<object?, CancellationToken, Task<IReadOnlyList<TestTreeNode>>>> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _treeRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _markdown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ViewNode> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<ViewEvent, CancellationToken, Task>> _viewHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ICustomEditorProvider> _customEditors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _customEditorTexts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ProgressRecord> _progress = new();
    private int _nextEditor;

    internal TestWindow(TestCommands commands) => _commands = commands;

    /// <summary>Chooses a notification's action (default: none, as if dismissed).</summary>
    public Func<ShownNotification, string?> NotificationResponder { get; set; } = _ => null;

    /// <summary>Chooses an item of a quick pick (default: none, as if cancelled).</summary>
    public Func<IReadOnlyList<QuickPickItem>, QuickPickItem?> QuickPickResponder { get; set; } = _ => null;

    /// <summary>Answers an input box (default: null, as if cancelled).</summary>
    public Func<InputBoxOptions, string?> InputBoxResponder { get; set; } = _ => null;

    /// <summary>The notifications shown, in order.</summary>
    public IReadOnlyList<ShownNotification> Notifications => [.. _notifications];

    /// <summary>The output channels created, by name.</summary>
    public IReadOnlyDictionary<string, TestOutputChannel> OutputChannels => _channels;

    /// <summary>The status bar items created, by id.</summary>
    public IReadOnlyDictionary<string, TestStatusBarItem> StatusBarItems => _statusItems;

    /// <summary>The tasks run with progress, in order.</summary>
    public IReadOnlyList<ProgressRecord> Progress => [.. _progress];

    /// <summary>A Markdown view's content.</summary>
    public string? GetMarkdown(string viewId) => _markdown.TryGetValue(viewId, out var markdown) ? markdown : null;

    /// <summary>A declarative view's current content (a tool window's, or a custom editor's by its view id).</summary>
    public ViewNode? GetView(string viewId) => _views.TryGetValue(viewId, out var view) ? view : null;

    /// <summary>The element <paramref name="elementId"/> of a declarative view, wherever it is in the tree.</summary>
    public ViewNode? FindElement(string viewId, string elementId)
    {
        static ViewNode? Find(ViewNode node, string id) =>
            node.Id == id ? node : (node.Children ?? []).Select(child => Find(child, id)).FirstOrDefault(found => found is not null);
        return GetView(viewId) is { } root ? Find(root, elementId) : null;
    }

    /// <summary>How many times the tree view refreshed (its provider raised <see cref="ITreeDataProvider{T}.Changed"/>).</summary>
    public int TreeRefreshes(string viewId) => _treeRefreshes.GetValueOrDefault(viewId);

    /// <summary>The text a custom editor's document has now (null: unchanged since it opened).</summary>
    public string? GetCustomEditorText(string viewId) => _customEditorTexts.TryGetValue(viewId, out var text) ? text : null;

    /// <inheritdoc/>
    public Task<string?> ShowNotificationAsync(NotificationSeverity severity, string message, IReadOnlyList<string>? actions = null, CancellationToken cancellationToken = default)
    {
        var shown = new ShownNotification(severity, message, actions ?? [], null);
        var chosen = NotificationResponder(shown);
        _notifications.Enqueue(shown with { Chosen = chosen });
        return Task.FromResult(chosen);
    }

    /// <inheritdoc/>
    public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, string? placeholder = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(QuickPickResponder(items));

    /// <inheritdoc/>
    public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult(InputBoxResponder(options));

    /// <inheritdoc/>
    public async Task<T> WithProgressAsync<T>(string title, Func<IProgress<ProgressReport>, CancellationToken, Task<T>> work, bool cancellable = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var record = new ProgressRecord(title, cancellable, cancel);
        _progress.Enqueue(record);
        try
        {
            return await work(new SynchronousProgress(record), cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            record.IsDone = true;
        }
    }

    /// <inheritdoc/>
    public void SetView(string viewId, ViewNode root) => _views[viewId] = root;

    /// <inheritdoc/>
    public IDisposable OnViewEvent(string viewId, Func<ViewEvent, CancellationToken, Task> handler)
    {
        _viewHandlers[viewId] = handler;
        return new Registration(() => _viewHandlers.TryRemove(viewId, out _));
    }

    /// <summary>Acts in a declarative view, as the user would: a click, a change, a table row selected or activated.</summary>
    public Task RaiseViewEventAsync(string viewId, ViewEvent viewEvent, CancellationToken cancellationToken = default) =>
        _viewHandlers.TryGetValue(viewId, out var handler)
            ? handler(viewEvent, cancellationToken)
            : throw new InvalidOperationException($"The plugin handles no events of the view '{viewId}'.");

    /// <summary>Clicks the button <paramref name="elementId"/>.</summary>
    public Task ClickAsync(string viewId, string elementId) => RaiseViewEventAsync(viewId, new ViewEvent(elementId, "click"));

    /// <summary>Changes a text box, check box or dropdown to <paramref name="value"/>.</summary>
    public Task ChangeAsync(string viewId, string elementId, string value) => RaiseViewEventAsync(viewId, new ViewEvent(elementId, "change", value));

    /// <inheritdoc/>
    public IDisposable RegisterCustomEditor(string viewType, ICustomEditorProvider provider)
    {
        _customEditors[viewType] = provider;
        return new Registration(() => _customEditors.TryRemove(viewType, out _));
    }

    /// <summary>Opens a file in the custom editor <paramref name="viewType"/>; returns the view id its content goes to.</summary>
    public async Task<string> OpenCustomEditorAsync(string viewType, string path, string text, CancellationToken cancellationToken = default)
    {
        if (!_customEditors.TryGetValue(viewType, out var provider))
        {
            throw new InvalidOperationException($"The plugin provides no custom editor '{viewType}'.");
        }

        var viewId = $"{viewType}#{Interlocked.Increment(ref _nextEditor)}";
        await provider.OpenAsync(new CustomEditorDocument(viewId, path, text), cancellationToken).ConfigureAwait(false);
        return viewId;
    }

    /// <summary>Closes a custom editor's document.</summary>
    public Task CloseCustomEditorAsync(string viewType, string viewId, CancellationToken cancellationToken = default) =>
        _customEditors.TryGetValue(viewType, out var provider) ? provider.CloseAsync(viewId, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public void SetCustomEditorText(string viewId, string text) => _customEditorTexts[viewId] = text;

    /// <inheritdoc/>
    public IOutputChannel CreateOutputChannel(string name) => _channels.GetOrAdd(name, n => new TestOutputChannel(n));

    /// <inheritdoc/>
    public IStatusBarItem CreateStatusBarItem(string id, StatusBarAlignment alignment = StatusBarAlignment.Left, int priority = 0) =>
        _statusItems[id] = new TestStatusBarItem(id, alignment, priority);

    /// <inheritdoc/>
    public IDisposable RegisterTreeDataProvider<T>(string viewId, ITreeDataProvider<T> provider)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(provider);
        _trees[viewId] = async (element, ct) =>
        {
            var children = await provider.GetChildrenAsync(element is null ? default : (T)element, ct).ConfigureAwait(false);
            return [.. children.Select(child => new TestTreeNode(provider.GetTreeItem(child), child))];
        };
        void Changed(object? sender, TreeDataChangedEventArgs<T> e) => _treeRefreshes.AddOrUpdate(viewId, 1, (_, n) => n + 1);
        provider.Changed += Changed;
        return new Registration(() =>
        {
            provider.Changed -= Changed;
            _trees.TryRemove(viewId, out _);
        });
    }

    /// <summary>A tree view's rows: the roots, or <paramref name="parent"/>'s children.</summary>
    public Task<IReadOnlyList<TestTreeNode>> GetTreeChildrenAsync(string viewId, TestTreeNode? parent = null, CancellationToken cancellationToken = default) =>
        _trees.TryGetValue(viewId, out var children)
            ? children(parent?.Element, cancellationToken)
            : throw new InvalidOperationException($"The plugin has no tree data provider for '{viewId}'.");

    /// <summary>Clicks a tree row: runs its command, if it has one.</summary>
    public async Task ClickTreeItemAsync(TestTreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Item.Command is { } command)
        {
            await _commands.ExecuteAsync(command.CommandId, cancellationToken, command.Args).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void SetMarkdown(string viewId, string markdown) => _markdown[viewId] = markdown;

    /// <summary>Reports at once, in order (<see cref="Progress{T}"/> would post to the thread pool).</summary>
    private sealed class SynchronousProgress(ProgressRecord record) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => record.Add(value);
    }
}
