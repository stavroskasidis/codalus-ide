using System.Collections.ObjectModel;
using Avalonia;
using Daedalus.Projects.Protocol;
using Daedalus.Rpc;
using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.Projects;

namespace Daedalus.Shell.ViewModels;

/// <summary>One visible line of the Solution Explorer (the tree is flattened so the list can virtualize).</summary>
public sealed class ExplorerRowViewModel(ExplorerNode node, int depth, bool expanded) : ObservableObject
{
    private ExplorerNode _node = node;
    private bool _isExpanded = expanded;

    public ExplorerNode Node
    {
        get => _node;
        set
        {
            if (SetField(ref _node, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(IsWarning));
                OnPropertyChanged(nameof(IsLinked));
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string Id => _node.Id;

    public int Depth { get; } = depth;

    public Thickness Indent => new(4 + (Depth * 16), 0, 0, 0);

    public string Name => _node.Name;

    public string? Detail => _node.Detail;

    public bool HasDetail => _node.Detail is not null;

    public string Icon => _node.Icon;

    public bool IsWarning => _node.IsWarning;

    public bool IsLinked => _node.IsLinked;

    public bool HasChildren => _node.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string? Chevron => HasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    public string AccessibleName
    {
        get
        {
            var state = HasChildren ? (_isExpanded ? ", expanded" : ", collapsed") : string.Empty;
            var detail = _node.Detail is { } d ? " " + d : string.Empty;
            var warning = _node.IsWarning ? ", warning" : string.Empty;
            return $"{_node.Name}{detail}{warning}{state}";
        }
    }
}

/// <summary>
/// The Solution Explorer. The tree is rebuilt from the build host's model (<see cref="SolutionTree"/>) and shown as
/// a flattened list; expansion and selection survive rebuilds by node id. Operations act on the selected row.
/// TODO(issue): "Show All Files" (files on disk that aren't project items; the host's fs/list is ready for it) and
/// multi-selection. (Open documents whose files were deleted are flagged in their tabs.)
/// </summary>
public sealed class SolutionExplorerViewModel : ObservableObject
{
    public const string ToolWindowId = "solutionExplorer";

    private readonly ShellHost _shell;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private ExplorerNode? _root;
    private ExplorerRowViewModel? _selected;
    private bool _refreshQueued;
    private string? _pendingReveal;
    private (IReadOnlyList<string> Paths, bool Cut)? _clipboard;

    public SolutionExplorerViewModel(ShellHost shell)
    {
        _shell = shell;
        var solutions = shell.Solutions;
        solutions.SolutionChanged += (_, _) => QueueRefresh();
        solutions.ProjectChanged += (_, _) => QueueRefresh();
        solutions.CompilerInputsChanged += (_, _) => QueueRefresh();
        OpenSolutionCommand = new RelayCommand(() => shell.Run("workbench.openSolution"));
        Refresh();
    }

    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public RelayCommand OpenSolutionCommand { get; }

    public bool HasSolution => _shell.Solutions.IsOpen;

    public ExplorerRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                _shell.Workbench.Context.Set("explorerItemKind", value is null ? null : KindKey(value.Node.Kind));
                _shell.Workbench.Context.Set("explorerItemIsProjectReference", value?.Node is { Kind: ExplorerNodeKind.Dependency } n && n.Id.Contains("|project|", StringComparison.Ordinal));
            }
        }
    }

    public ExplorerNode? Root => _root;

    public static string KindKey(ExplorerNodeKind kind) => kind switch
    {
        ExplorerNodeKind.SolutionFolder => "solutionFolder",
        ExplorerNodeKind.SolutionItem => "solutionItem",
        ExplorerNodeKind.DependencyGroup => "dependencyGroup",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ------------------------------------------------------------------ tree

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var solutions = _shell.Solutions;
        _root = solutions.Solution is { } solution ? SolutionTree.Build(solution, solutions.Projects, solutions.CompilerInputs) : null;
        OnPropertyChanged(nameof(HasSolution));
        if (_pendingReveal is { } pending && _root is not null && Path(_root, pending) is not null)
        {
            Reveal(pending);
            return;
        }
        var rows = new List<(ExplorerNode Node, int Depth)>();
        if (_root is not null)
        {
            Flatten(_root, 0, rows);
        }

        Apply(rows);
    }

    private void Flatten(ExplorerNode node, int depth, List<(ExplorerNode, int)> rows)
    {
        if (_seen.Add(node.Id) && node.Kind is ExplorerNodeKind.Solution or ExplorerNodeKind.SolutionFolder)
        {
            _expanded.Add(node.Id);
        }

        rows.Add((node, depth));
        if (_expanded.Contains(node.Id))
        {
            foreach (var child in node.Children)
            {
                Flatten(child, depth + 1, rows);
            }
        }
    }

    /// <summary>Updates <see cref="Rows"/> in place: only the changed middle part is replaced, so scrolling is kept.</summary>
    private void Apply(List<(ExplorerNode Node, int Depth)> rows)
    {
        var selectedId = _selected?.Id;
        var prefix = 0;
        while (prefix < rows.Count && prefix < Rows.Count && Rows[prefix].Id == rows[prefix].Node.Id && Rows[prefix].Depth == rows[prefix].Depth)
        {
            Rows[prefix].Node = rows[prefix].Node;
            Rows[prefix].IsExpanded = _expanded.Contains(rows[prefix].Node.Id);
            prefix++;
        }

        var suffix = 0;
        while (suffix < rows.Count - prefix && suffix < Rows.Count - prefix
               && Rows[Rows.Count - 1 - suffix].Id == rows[rows.Count - 1 - suffix].Node.Id
               && Rows[Rows.Count - 1 - suffix].Depth == rows[rows.Count - 1 - suffix].Depth)
        {
            var row = Rows[Rows.Count - 1 - suffix];
            row.Node = rows[rows.Count - 1 - suffix].Node;
            row.IsExpanded = _expanded.Contains(row.Id);
            suffix++;
        }

        for (var i = Rows.Count - suffix - 1; i >= prefix; i--)
        {
            Rows.RemoveAt(i);
        }

        for (var i = prefix; i < rows.Count - suffix; i++)
        {
            Rows.Insert(i, new ExplorerRowViewModel(rows[i].Node, rows[i].Depth, _expanded.Contains(rows[i].Node.Id)));
        }

        Selected = Rows.FirstOrDefault(r => r.Id == selectedId) ?? (selectedId is null ? null : Selected);
        if (Selected is not null && !Rows.Contains(Selected))
        {
            Selected = null;
        }
    }

    public void SetExpanded(ExplorerRowViewModel row, bool expanded)
    {
        if (!row.HasChildren || (expanded ? !_expanded.Add(row.Id) : !_expanded.Remove(row.Id)))
        {
            return;
        }

        Refresh();
    }

    public void Toggle(ExplorerRowViewModel row) => SetExpanded(row, !_expanded.Contains(row.Id));

    public void CollapseAll()
    {
        _expanded.RemoveWhere(id => id != "solution");
        Refresh();
    }

    /// <summary>Reveals <paramref name="id"/> as soon as the node exists (it may come with the next model update).</summary>
    public void RevealWhenAvailable(string id)
    {
        _pendingReveal = id;
        Reveal(id);
    }

    /// <summary>Selects (and reveals) the node with <paramref name="id"/>.</summary>
    public void Reveal(string id)
    {
        if (_root is null || Path(_root, id) is not { } path)
        {
            return;
        }

        if (_pendingReveal == id)
        {
            _pendingReveal = null;
        }

        foreach (var ancestor in path.SkipLast(1))
        {
            _expanded.Add(ancestor.Id);
        }

        Refresh();
        Selected = Rows.FirstOrDefault(r => r.Id == id);
    }

    private static List<ExplorerNode>? Path(ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return [node];
        }

        foreach (var child in node.Children)
        {
            if (Path(child, id) is { } path)
            {
                path.Insert(0, node);
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ activation

    /// <summary>A single click on a file shows it in the preview tab, keeping focus here.</summary>
    public async Task PreviewAsync(ExplorerRowViewModel row)
    {
        if (row.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem, Path: { } path } && File.Exists(path))
        {
            await _shell.OpenFileAsync(path, preview: true).ConfigureAwait(true);
        }
    }

    /// <summary>Enter or double-click: files open, containers expand or collapse, project references jump to the project.</summary>
    public async Task ActivateAsync(ExplorerRowViewModel row)
    {
        switch (row.Node)
        {
            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem, Path: { } path }:
                await _shell.OpenFileAsync(path).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.Dependency, Path: { } target } when target.EndsWith("proj", StringComparison.OrdinalIgnoreCase):
                Reveal("p:" + target);
                break;
            default:
                Toggle(row);
                break;
        }
    }

    // ------------------------------------------------------------------ operations

    private ExplorerNode? Node => _selected?.Node;

    /// <summary>The folder new items go into: the selected folder or project, or the selected file's folder.</summary>
    private string? TargetDirectory => Node switch
    {
        { Kind: ExplorerNodeKind.Folder, Path: { } folder } => folder,
        { Kind: ExplorerNodeKind.Project, Path: { } project } => System.IO.Path.GetDirectoryName(project),
        { Kind: ExplorerNodeKind.File, Path: { } file } => System.IO.Path.GetDirectoryName(file),
        _ => null,
    };

    public async Task NewFileAsync()
    {
        if (TargetDirectory is not { } directory)
        {
            return;
        }

        var name = await _shell.Dialogs.InputAsync("New File", $"Create a file in {Display(directory)}.", "NewFile.cs", n => ValidateName(directory, n), (0, 7)).ConfigureAwait(true);
        if (name is not null && await TryAsync(() => _shell.Solutions.CreateFileAsync(System.IO.Path.Combine(directory, name), Template(directory, name), CancellationToken.None)).ConfigureAwait(true) is { } result)
        {
            await _shell.OpenTextFileAsync(result.CreatedPaths[0], string.Empty).ConfigureAwait(true);
        }
    }

    public async Task NewFolderAsync()
    {
        if (TargetDirectory is not { } directory)
        {
            return;
        }

        var name = await _shell.Dialogs.InputAsync("New Folder", $"Create a folder in {Display(directory)}.", "NewFolder", n => ValidateName(directory, n)).ConfigureAwait(true);
        if (name is not null)
        {
            await TryAsync(() => _shell.Solutions.CreateFolderAsync(System.IO.Path.Combine(directory, name), CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task RenameAsync()
    {
        switch (Node)
        {
            case { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id, Name: var current }:
            {
                var name = await _shell.Dialogs.InputAsync("Rename Solution Folder", "New name:", current, n => string.IsNullOrWhiteSpace(n) || n.IndexOfAny(['/', '\\']) >= 0 ? "Enter a name without slashes." : null).ConfigureAwait(true);
                if (name is not null && name != current)
                {
                    await TryAsync(() => _shell.Solutions.RenameSolutionFolderAsync(id, name, CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            }

            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder or ExplorerNodeKind.Project, Path: { } path, IsLinked: false }:
            {
                var directory = System.IO.Path.GetDirectoryName(path)!;
                var current = System.IO.Path.GetFileName(path);
                var stem = Node.Kind == ExplorerNodeKind.Folder ? current.Length : System.IO.Path.GetFileNameWithoutExtension(current).Length;
                var name = await _shell.Dialogs.InputAsync(
                    Node.Kind == ExplorerNodeKind.Project ? "Rename Project File" : "Rename",
                    $"New name for '{current}':",
                    current,
                    n => n == current ? null : ValidateName(directory, n, current),
                    (0, stem)).ConfigureAwait(true);
                if (name is not null && name != current)
                {
                    await TryAsync(() => _shell.Solutions.RenameAsync(path, System.IO.Path.Combine(directory, name), CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            }
        }
    }

    public async Task DeleteAsync()
    {
        switch (Node)
        {
            case { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id, Name: var name }:
                if (await _shell.Dialogs.ConfirmAsync("Remove Solution Folder", $"Remove the solution folder '{name}' and everything in it from the solution? Projects stay on disk.", "Remove", danger: true).ConfigureAwait(true))
                {
                    await TryAsync(() => _shell.Solutions.RemoveSolutionFolderAsync(id, CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            case { Kind: ExplorerNodeKind.Project, Path: { } project }:
                await RemoveProjectAsync(project).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.Dependency, ProjectPath: { } owner, Path: { } target } when target.EndsWith("proj", StringComparison.OrdinalIgnoreCase):
                await TryAsync(() => _shell.Solutions.RemoveProjectReferenceAsync(owner, target, CancellationToken.None)).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false, Name: var name }:
                if (await _shell.Dialogs.ConfirmAsync("Delete", $"Move '{name}' to the {TrashName}?", "Delete", danger: true).ConfigureAwait(true))
                {
                    await TryAsync(() => _shell.Solutions.DeleteAsync([path], CancellationToken.None)).ConfigureAwait(true);
                }

                break;
        }
    }

    public async Task RemoveProjectAsync(string project)
    {
        if (await _shell.Dialogs.ConfirmAsync("Remove Project", $"Remove '{System.IO.Path.GetFileNameWithoutExtension(project)}' from the solution? Its files stay on disk.", "Remove").ConfigureAwait(true))
        {
            await TryAsync(() => _shell.Solutions.RemoveProjectAsync(project, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    private static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public void Cut() => _clipboard = Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false } ? ([path], true) : _clipboard;

    public void Copy() => _clipboard = Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false } ? ([path], false) : _clipboard;

    public bool CanPaste => _clipboard is not null;

    public async Task PasteAsync()
    {
        if (_clipboard is not { } clip || TargetDirectory is not { } directory)
        {
            return;
        }

        if (await TryAsync(() => _shell.Solutions.MoveAsync(clip.Paths, directory, copy: !clip.Cut, CancellationToken.None)).ConfigureAwait(true) is not null && clip.Cut)
        {
            _clipboard = null;
        }
    }

    /// <summary>Drag and drop: moves (or with Ctrl, copies) <paramref name="paths"/> into the folder or project of <paramref name="target"/>.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths, ExplorerNode target, bool copy)
    {
        var directory = target switch
        {
            { Kind: ExplorerNodeKind.Folder, Path: { } folder } => folder,
            { Kind: ExplorerNodeKind.Project, Path: { } project } => System.IO.Path.GetDirectoryName(project),
            { Kind: ExplorerNodeKind.File, Path: { } file } => System.IO.Path.GetDirectoryName(file),
            _ => null,
        };
        var moving = paths.Where(p => directory is not null && !SolutionService.PathComparer.Equals(System.IO.Path.GetDirectoryName(p), directory)).ToList();
        if (directory is not null && moving.Count > 0)
        {
            await TryAsync(() => _shell.Solutions.MoveAsync(moving, directory, copy, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task AddSolutionFolderAsync()
    {
        var parent = Node is { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id } ? id : null;
        var name = await _shell.Dialogs.InputAsync("New Solution Folder", "Name:", "New Folder", n => string.IsNullOrWhiteSpace(n) || n.IndexOfAny(['/', '\\']) >= 0 ? "Enter a name without slashes." : null).ConfigureAwait(true);
        if (name is not null)
        {
            await TryAsync(() => _shell.Solutions.AddSolutionFolderAsync(name, parent, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task AddProjectReferenceAsync()
    {
        if (Node?.ProjectPath is not { } project || _shell.Solutions.Solution is not { } solution)
        {
            return;
        }

        var existing = _shell.Solutions.Projects.GetValueOrDefault(project)?.References.Where(r => r.Kind == ReferenceKinds.Project).Select(r => r.Path).ToHashSet(SolutionService.PathComparer) ?? [];
        var candidates = solution.Projects
            .Where(p => !SolutionService.PathComparer.Equals(p.Path, project) && !existing.Contains(p.Path))
            .Select(p => new QuickPickItem(p.Name) { Detail = Display(p.Path), Tag = p.Path })
            .ToList();
        if (candidates.Count == 0)
        {
            _shell.Workbench.Notifications.Info("Every other project of the solution is already referenced.");
            return;
        }

        if (await _shell.Palette.ShowAsync(candidates, $"Add a project reference to {System.IO.Path.GetFileNameWithoutExtension(project)}").ConfigureAwait(true) is { Tag: string reference })
        {
            await TryAsync(() => _shell.Solutions.AddProjectReferenceAsync(project, reference, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task CopyPathAsync()
    {
        if (Node?.Path is { } path && _shell.Window?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, path).ConfigureAwait(true);
        }
    }

    public void RevealInFileManager()
    {
        if (Node?.Path is { } path)
        {
            try
            {
                Daedalus.Platform.ShellIntegration.RevealInFileManager(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _shell.Workbench.Notifications.Error($"Could not open the file manager: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private string Display(string path) =>
        _shell.Solutions.Solution is { } solution ? System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(solution.Path)!, path) : path;

    private static string? ValidateName(string directory, string name, string? current = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name.";
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
        {
            return "The name contains characters that aren't allowed in file names.";
        }

        var target = System.IO.Path.Combine(directory, name);
        var caseOnlyRename = current is not null && string.Equals(current, name, StringComparison.OrdinalIgnoreCase);
        return !caseOnlyRename && (File.Exists(target) || Directory.Exists(target)) ? $"'{name}' already exists here." : null;
    }

    /// <summary>
    /// A starting point for new C# files: a class named after the file, in the namespace the project name and the
    /// folder imply (as Visual Studio does). Other files start empty.
    /// </summary>
    private string? Template(string directory, string name)
    {
        var type = System.IO.Path.GetFileNameWithoutExtension(name);
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(type, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return null;
        }

        var segments = new List<string>();
        if (Node?.ProjectPath is { } project)
        {
            segments.Add(System.IO.Path.GetFileNameWithoutExtension(project));
            var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(project)!, directory);
            if (relative != ".")
            {
                segments.AddRange(relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
            }
        }

        var ns = string.Join('.', segments.SelectMany(s => s.Split('.')).Select(Identifier));
        return ns.Length == 0
            ? $"public class {type}\n{{\n}}\n"
            : $"namespace {ns};\n\npublic class {type}\n{{\n}}\n";
    }

    private static string Identifier(string segment)
    {
        var chars = segment.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var text = new string(chars);
        return text.Length > 0 && char.IsDigit(text[0]) ? "_" + text : text;
    }

    /// <summary>Runs an operation; failures become a notification with the host's message.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> operation)
        where T : class
    {
        try
        {
            return await operation().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            _shell.Workbench.Notifications.Error(ex.Message);
            return null;
        }
    }

    private async Task TryAsync(Func<Task> operation) => await TryAsync<object>(async () =>
    {
        await operation().ConfigureAwait(true);
        return new object();
    }).ConfigureAwait(true);
}
