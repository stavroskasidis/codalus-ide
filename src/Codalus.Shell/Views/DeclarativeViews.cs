using Avalonia.Controls;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell.Views;

/// <summary>A plugin's tool window of kind <c>view</c> (D2, plugin API 1.0): its declarative view.</summary>
public sealed class DeclarativeToolViewModel
{
    public DeclarativeToolViewModel(string viewId, PluginManager plugins)
    {
        ViewId = viewId;
        Plugins = plugins;
    }

    public string ViewId { get; }

    public PluginManager Plugins { get; }
}

/// <summary>Hosts a declarative view by its id: shows it, follows its updates, and sends what the user does to its plugin.</summary>
public sealed class DeclarativeViewHost : UserControl
{
    private readonly DeclarativeView _view = new();
    private readonly PluginManager _plugins;
    private readonly string _viewId;

    public DeclarativeViewHost(string viewId, PluginManager plugins, bool activate)
    {
        _viewId = viewId;
        _plugins = plugins;
        Content = _view;
        _view.Root = plugins.GetView(viewId);
        _view.Raised += (id, kind, value, row) =>
            _ = plugins.SendViewEventAsync(new ViewEventParams(viewId, id, kind, value, row), CancellationToken.None);
        plugins.ViewChanged += OnViewChanged;
        DetachedFromVisualTree += (_, _) => plugins.ViewChanged -= OnViewChanged;
        AttachedToVisualTree += (_, _) =>
        {
            plugins.ViewChanged -= OnViewChanged;
            plugins.ViewChanged += OnViewChanged;
            _view.Root = plugins.GetView(viewId);
        };
        if (activate)
        {
            _ = plugins.OnViewOpenedAsync(viewId, CancellationToken.None);
        }
    }

    /// <summary>The renderer (tests).</summary>
    public DeclarativeView View => _view;

    private void OnViewChanged(object? sender, string id)
    {
        if (id == _viewId)
        {
            Ui.Post(() => _view.Root = _plugins.GetView(_viewId));
        }
    }
}

/// <summary>
/// A file open in a plugin's custom editor (plugin API 1.0, M22a): the plugin's declarative view of it. The plugin's
/// new text for the file makes it modified; Save writes that text.
/// </summary>
public sealed class CustomEditorDocument : ShellDocument
{
    public CustomEditorDocument(string path, string viewId, ContributedCustomEditor editor, PluginManager plugins)
    {
        Plugins = plugins;
        Path = path;
        ViewId = viewId;
        Editor = editor;
        Id = IdFor(path);
        Title = System.IO.Path.GetFileName(path);
    }

    public override string Kind => "customEditor";

    public override string? FilePath => Path;

    public string Path { get; }

    public string ViewId { get; }

    public ContributedCustomEditor Editor { get; }

    public PluginManager Plugins { get; }

    /// <summary>The text Save writes, once the plugin gave it.</summary>
    public string? PendingText { get; private set; }

    public static string IdFor(string path) => "customEditor:" + path;

    public void SetText(string text)
    {
        PendingText = text;
        IsModified = true;
    }

    public async Task SaveAsync()
    {
        if (PendingText is { } text)
        {
            await File.WriteAllTextAsync(Path, text).ConfigureAwait(true);
            IsModified = false;
        }
    }
}
