namespace Codalus.Shell.ViewModels;

/// <summary>
/// A read-only tree of changed files shown as Git Changes shows its sections (M57): a commit's changes and the Compare
/// document. The tree (<see cref="GitChangesViewModel.BuildTree"/>) is built off the UI thread and shown as a virtualized
/// list of rows, updated in place; a folder expands and collapses by inserting or removing only the rows under it, and the
/// folders the user collapsed stay collapsed when the tree is built again. Their tree views made an item for every file
/// on the UI thread, with every folder expanded: seconds for 10,000 files, at each open and each Refresh.
/// </summary>
public sealed class GitChangeTree : ITreeRowsOwner
{
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private IReadOnlyList<GitChangeNode> _roots = [];

    /// <summary>The visible rows: each root, then the rows under it when it's expanded.</summary>
    public ResettableCollection<GitChangeRow> Rows { get; } = [];

    /// <summary>The tree's top nodes.</summary>
    public IReadOnlyList<GitChangeNode> Roots => _roots;

    /// <summary>The folders the user collapsed, by key, for a tree built again (a copy: it's read off the UI thread).</summary>
    public IReadOnlySet<string> Collapsed => _collapsed.ToHashSet(StringComparer.Ordinal);

    /// <summary>For the nodes the tree is built of: a node's <see cref="GitChangeNode.IsExpanded"/> set from outside moves its rows.</summary>
    public Action<GitChangeNode, bool> ExpandedChanged => OnExpandedChanged;

    /// <summary>The rows of <paramref name="roots"/>, computed with them (off the UI thread).</summary>
    public static IReadOnlyList<(GitChangeNode Node, int Depth)> Flatten(IReadOnlyList<GitChangeNode> roots)
    {
        var rows = new List<(GitChangeNode Node, int Depth)>();
        GitChangesViewModel.Flatten(roots, 0, rows);
        return rows;
    }

    /// <summary>Shows a tree built again: the rows of the same nodes in the same places stay, with their new nodes.</summary>
    public void Show(IReadOnlyList<GitChangeNode> roots, IReadOnlyList<(GitChangeNode Node, int Depth)> rows)
    {
        _roots = roots;
        PatchRows(Rows, rows);
    }

    /// <summary>The row of the node <paramref name="key"/> names, or null (none, or under a collapsed folder).</summary>
    public GitChangeRow? RowOf(string key) => Rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    /// <summary>The row of the first node that <paramref name="match"/>es, the folders above it expanded first; null when there's none.</summary>
    public GitChangeRow? Reveal(Func<GitChangeNode, bool> match)
    {
        if (PathTo(_roots, match) is not { } path)
        {
            return null;
        }

        foreach (var folder in path.Take(path.Count - 1))
        {
            if (RowOf(folder.Key) is { } row)
            {
                SetExpanded(row, true);
            }
        }

        return RowOf(path[^1].Key);

        static List<GitChangeNode>? PathTo(IEnumerable<GitChangeNode> nodes, Func<GitChangeNode, bool> match)
        {
            foreach (var node in nodes)
            {
                if (match(node))
                {
                    return [node];
                }

                if (PathTo(node.Children, match) is { } below)
                {
                    below.Insert(0, node);
                    return below;
                }
            }

            return null;
        }
    }

    /// <summary>Expands or collapses a row (the chevron, Left and Right, double-click): only the rows under it change.</summary>
    public void SetExpanded(GitChangeRow row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        Remember(row.Key, expanded);
        row.SetExpanded(expanded);
        ShowChildren(Rows, row, expanded);
    }

    /// <summary>Expand All and Collapse All on a row: it and every folder under it, the rows shown again at once.</summary>
    public void SetExpandedUnder(GitChangeRow row, bool expanded)
    {
        Apply(row.Node);
        PatchRows(Rows, Flatten(_roots));

        void Apply(GitChangeNode node)
        {
            if (node.IsFile)
            {
                return;
            }

            Remember(node.Key, expanded);
            node.SetExpanded(expanded);
            foreach (var child in node.Children)
            {
                Apply(child);
            }
        }
    }

    void ITreeRowsOwner.SetExpanded(ITreeRow row, bool expanded)
    {
        if (row is GitChangeRow change)
        {
            SetExpanded(change, expanded);
        }
    }

    void ITreeRowsOwner.SetExpandedUnder(ITreeRow row, bool expanded)
    {
        if (row is GitChangeRow change)
        {
            SetExpandedUnder(change, expanded);
        }
    }

    private void OnExpandedChanged(GitChangeNode node, bool expanded)
    {
        if (Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { } row)
        {
            node.SetExpanded(!expanded);
            SetExpanded(row, expanded);
            return;
        }

        Remember(node.Key, expanded);
    }

    private void Remember(string key, bool expanded)
    {
        if (expanded)
        {
            _collapsed.Remove(key);
        }
        else
        {
            _collapsed.Add(key);
        }
    }

    /// <summary>Shows or hides the rows under <paramref name="row"/>, just expanded or collapsed: one insertion or one removal.</summary>
    internal static void ShowChildren(ResettableCollection<GitChangeRow> rows, GitChangeRow row, bool expanded)
    {
        var index = rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (expanded)
        {
            var below = new List<(GitChangeNode Node, int Depth)>();
            GitChangesViewModel.Flatten(row.Node.Children, row.Depth + 1, below);
            rows.InsertRange(index + 1, [.. below.Select(r => new GitChangeRow(r.Node, r.Depth))]);
            return;
        }

        var end = index + 1;
        while (end < rows.Count && rows[end].Depth > row.Depth)
        {
            end++;
        }

        rows.RemoveRange(index + 1, end - index - 1);
    }

    /// <summary>Shows a tree's new rows: the rows of the same nodes in the same places stay, with their new nodes.</summary>
    internal static void PatchRows(ResettableCollection<GitChangeRow> target, IReadOnlyList<(GitChangeNode Node, int Depth)> rows) =>
        target.Patch(rows, (row, r) => row.Depth == r.Depth && string.Equals(row.Key, r.Node.Key, StringComparison.Ordinal), r => new GitChangeRow(r.Node, r.Depth), (row, r) => row.Node = r.Node);
}
