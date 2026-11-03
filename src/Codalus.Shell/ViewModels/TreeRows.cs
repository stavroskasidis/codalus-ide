namespace Codalus.Shell.ViewModels;

/// <summary>
/// One visible line of a tree shown as a virtualized list of rows (M56, M57): Git Changes' sections, the Git Repository
/// tree, a commit's changes and the Compare document. The keyboard and the chevron reach it through
/// <see cref="Views.TreeRows"/>, the same code for each; a row's automation peer (the tree item a screen reader expects)
/// reads it here too.
/// </summary>
public interface ITreeRow
{
    /// <summary>Its level: 0 for a root.</summary>
    int Depth { get; }

    /// <summary>A folder (or a group, or the repository): it expands and collapses.</summary>
    bool HasChildren { get; }

    bool IsExpanded { get; }
}

/// <summary>The sizes every tree shares.</summary>
public static class TreeMetrics
{
    /// <summary>
    /// How far each level is indented: 12 px, the classic IDE's, so a deep tree stays compact horizontally. Every tree
    /// uses it: the flattened ones (<see cref="ITreeRow"/>), the <c>TreeView</c>s (its <c>TreeViewItemIndent</c>), the
    /// variable windows, the property grid, the result lists' groups and the WCF Test Client's and profiler's trees.
    /// </summary>
    public const double IndentPerLevel = 12;
}

/// <summary>What owns a tree's rows: it shows or hides the rows under one when it expands or collapses.</summary>
public interface ITreeRowsOwner
{
    /// <summary>Expands or collapses <paramref name="row"/>: only the rows under it change.</summary>
    void SetExpanded(ITreeRow row, bool expanded);

    /// <summary>Expand All and Collapse All on <paramref name="row"/>: it and every folder under it.</summary>
    void SetExpandedUnder(ITreeRow row, bool expanded);
}
