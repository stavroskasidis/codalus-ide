using System.Collections.ObjectModel;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>One node of Class View or the Object Browser: a project, assembly, namespace, type or member, loaded lazily.</summary>
public sealed class SymbolBrowserNode : ObservableObject
{
    private readonly Func<SymbolBrowserNode, Task<IReadOnlyList<SymbolBrowserNode>>>? _load;
    private bool _loaded;
    private bool _isExpanded;

    public SymbolBrowserNode(string project, string? id, string name, string icon, string? detail, Func<SymbolBrowserNode, Task<IReadOnlyList<SymbolBrowserNode>>>? load)
    {
        Project = project;
        Id = id;
        Name = name;
        Icon = icon;
        Detail = detail;
        _load = load;
        if (load is not null)
        {
            Children.Add(Placeholder(project, Localizer.Get("msg.symbolBrowser.loading", "Loading…")));
        }
    }

    /// <summary>The project file the node is browsed in.</summary>
    public string Project { get; }

    /// <summary>The <c>codalus/browse</c> id; null for a project's node and placeholders.</summary>
    public string? Id { get; }

    public string Name { get; }

    public string Icon { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsPlaceholder => Id is null && _load is null;

    public ObservableCollection<SymbolBrowserNode> Children { get; } = [];

    public string AccessibleName => Detail is null ? Name : $"{Name}, {Detail}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Loads the children once; completes when they are there.</summary>
    public async Task LoadAsync()
    {
        if (_loaded || _load is null)
        {
            return;
        }

        _loaded = true;
        IReadOnlyList<SymbolBrowserNode> children;
        try
        {
            children = await _load(this).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            children = [Placeholder(Project, Localizer.Format("msg.symbolBrowser.couldNotLoad", "Could not load: {0}", ex.Message))];
        }

        Children.Clear();
        foreach (var child in children.Count == 0 ? [Placeholder(Project, Localizer.Get("msg.symbolBrowser.none", "(none)"))] : children)
        {
            Children.Add(child);
        }
    }

    public static SymbolBrowserNode Placeholder(string project, string text) => new(project, null, text, "dot", null, null);
}

/// <summary>
/// Class View (a tool window: every C# and VB project's namespaces, types and members) and the Object Browser (a
/// document: the assemblies a project compiles against, its own first), both through <c>codalus/browse</c> (deferred
/// from M12a). A search box lists matching types and members; the selected item's declaration and summary show below;
/// Enter or a double-click goes to its declaration (in source, or a read-only metadata document).
/// </summary>
public sealed class SymbolBrowserViewModel : ObservableObject, IDisposable
{
    public const string ClassViewId = "classView";
    public const string ObjectBrowserId = "objectBrowser";

    private readonly Func<LanguageHostClient?> _client;
    private readonly SolutionService _solutions;
    private readonly Func<Location, Task> _open;
    private readonly bool _references;
    private SymbolBrowserNode? _selected;
    private string _search = string.Empty;
    private string? _declaration;
    private string? _description;
    private ProjectInfo? _project;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _describeCts;

    public SymbolBrowserViewModel(bool references, Func<LanguageHostClient?> client, SolutionService solutions, Func<Location, Task> open)
    {
        _references = references;
        _client = client;
        _solutions = solutions;
        _open = open;
    }

    /// <summary>The Object Browser (a project's assemblies), else Class View (the solution's code).</summary>
    public bool IsObjectBrowser => _references;

    public string Title => _references ? Localizer.Get("symbolBrowser.objectBrowser", "Object Browser") : Localizer.Get("symbolBrowser.classView", "Class View");

    public ObservableCollection<SymbolBrowserNode> Roots { get; } = [];

    /// <summary>The C# and VB projects (the Object Browser's picker).</summary>
    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    /// <summary>The Object Browser's project: its assemblies are the roots.</summary>
    public ProjectInfo? Project
    {
        get => _project;
        set
        {
            if (SetField(ref _project, value))
            {
                _ = RebuildAsync();
            }
        }
    }

    public SymbolBrowserNode? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                _ = DescribeAsync(value);
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                _ = RebuildAsync();
            }
        }
    }

    /// <summary>The selected item's declaration (<c>public sealed class Circle</c>).</summary>
    public string? Declaration
    {
        get => _declaration;
        private set
        {
            if (SetField(ref _declaration, value))
            {
                OnPropertyChanged(nameof(HasDeclaration));
            }
        }
    }

    public bool HasDeclaration => _declaration is not null;

    /// <summary>Its container, assembly and summary, one per line.</summary>
    public string? Description
    {
        get => _description;
        private set => SetField(ref _description, value);
    }

    /// <summary>The projects changed (a solution opened or loaded): the roots again.</summary>
    public async Task RefreshAsync()
    {
        var projects = _solutions.Projects.Values
            .Where(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || p.Path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Projects.Clear();
        foreach (var project in projects)
        {
            Projects.Add(project);
        }

        if (_project is null || !projects.Any(p => SolutionService.PathComparer.Equals(p.Path, _project.Path)))
        {
            _project = projects.FirstOrDefault();
            OnPropertyChanged(nameof(Project));
        }

        await RebuildAsync().ConfigureAwait(true);
    }

    private async Task RebuildAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var projects = _references ? (_project is null ? [] : [_project]) : Projects.ToList();
        var query = _search.Trim();
        var roots = new List<SymbolBrowserNode>();
        if (query.Length == 0)
        {
            roots.AddRange(_references && _project is not null
                ? await ChildrenAsync(_project.Path, null).ConfigureAwait(true)
                : projects.Select(p => new SymbolBrowserNode(p.Path, null, p.Name, "project", null, node => ChildrenAsync(node.Project, null))));
        }
        else
        {
            try
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(true); // typing settles
                foreach (var project in projects)
                {
                    roots.AddRange(await ChildrenAsync(project.Path, null, query, cts.Token).ConfigureAwait(true));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(root);
        }
    }

    private async Task<IReadOnlyList<SymbolBrowserNode>> ChildrenAsync(string project, string? parent, string? query = null, CancellationToken cancellationToken = default)
    {
        if (_client() is not { } client)
        {
            return [SymbolBrowserNode.Placeholder(project, Localizer.Get("msg.symbolBrowser.noLanguageService", "The C# language service isn't running."))];
        }

        var result = await client.BrowseAsync(new BrowseParams(project, parent, _references) { Query = query }, cancellationToken).ConfigureAwait(true);
        return
        [
            .. result.Items.Select(item => new SymbolBrowserNode(project, item.Id, item.Name, item.Kind == SymbolKind.Package ? "package" : SymbolNodeViewModel.IconFor(item.Kind), item.Detail,
                item.HasChildren || item.Kind is SymbolKind.Namespace or SymbolKind.Package ? node => ChildrenAsync(node.Project, node.Id) : null)),
        ];
    }

    private async Task DescribeAsync(SymbolBrowserNode? node)
    {
        _describeCts?.Cancel();
        if (node?.Id is not { } id || _client() is not { } client)
        {
            Declaration = null;
            Description = null;
            return;
        }

        var cts = _describeCts = new CancellationTokenSource();
        try
        {
            var described = await client.DescribeAsync(node.Project, id, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Declaration = described?.Declaration;
            Description = described is null ? null : string.Join("\n", new[] { described.Container, described.Assembly is { } assembly ? Localizer.Format("msg.symbolBrowser.assembly", "Assembly: {0}", assembly) : null, described.Summary }.OfType<string>());
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
            Declaration = null;
            Description = null;
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _describeCts?.Cancel();
        _describeCts?.Dispose();
    }

    /// <summary>Goes to the selected item's declaration.</summary>
    public async Task OpenSelectedAsync()
    {
        if (_selected?.Id is not { } id || _client() is not { } client)
        {
            return;
        }

        if (await client.DescribeAsync(_selected.Project, id, CancellationToken.None).ConfigureAwait(true) is { Location: { } location })
        {
            await _open(location).ConfigureAwait(true);
        }
    }
}
