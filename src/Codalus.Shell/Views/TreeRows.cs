using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A list of a flattened tree's rows (<see cref="ITreeRow"/>) behaves as a tree view (M56, M57: one code path for Git
/// Changes, the Git Repository tree, a commit's changes and Compare): the chevron toggles a folder on each click, a
/// double-click's second too (it raises no tap), and leaves the selection (its press doesn't reach the row, as a tree
/// view's expander: M57 review, a folder selected by its chevron dropped the Git Repository's history); a double-click
/// elsewhere on a folder toggles it, on another row activates it, off the rows does nothing (M57 review: it checked out
/// the selected branch); and in the tunnel (a list takes Enter itself, to select, before a handler added to it hears it) Left and
/// Right collapse and expand a folder or go to its parent, with Ctrl every folder under it too, as Ctrl+Enter does; + and
/// - expand and collapse, * and / everything under it; Enter toggles a folder or activates the row. A row's hover button
/// that has the focus (it keeps it after a click) takes its own keys and clicks. The list is a <see cref="TreeRowsList"/>:
/// a screen reader's Expand and Collapse on a row (its tree item's peer) do what Left and Right do.
/// </summary>
internal static class TreeRows
{
    /// <summary>
    /// Gives <paramref name="list"/> a tree's mouse and keys over the rows of <paramref name="owner"/>; <paramref name="activate"/>
    /// is Enter and the double-click on a row that isn't a folder (true when it did something). With
    /// <paramref name="offRowsActivatesSelected"/> a double-click off the rows acts on the selected row (Git Changes, as its
    /// lists do: there it opens a file's diff, where the Git Repository would check a branch out).
    /// </summary>
    public static void Attach(TreeRowsList list, Func<ITreeRowsOwner?> owner, Func<ITreeRow, bool>? activate = null, bool offRowsActivatesSelected = false)
    {
        list.ExpandRow = (row, expanded) => owner()?.SetExpanded(row, expanded);

        // In the tunnel, before the row selects itself on the press: the chevron toggles (on the tap) and selects nothing.
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (IsChevron(e.Source) && e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                if (!list.IsKeyboardFocusWithin)
                {
                    list.Focus(NavigationMethod.Pointer);
                }
            }
        }, RoutingStrategies.Tunnel);
        list.Tapped += (_, e) =>
        {
            if (IsChevron(e.Source) && RowAt(e.Source) is { } row && owner() is { } tree)
            {
                tree.SetExpanded(row, !row.IsExpanded);
                e.Handled = true;
            }
        };
        list.DoubleTapped += (_, e) =>
        {
            if (owner() is not { } tree)
            {
                return;
            }

            if (IsChevron(e.Source) && RowAt(e.Source) is { } folder)
            {
                tree.SetExpanded(folder, !folder.IsExpanded);
                e.Handled = true;
            }
            else if (!InButton(e.Source) && (RowAt(e.Source) ?? (offRowsActivatesSelected ? list.SelectedItem as ITreeRow : null)) is { } row && Activate(tree, row, activate))
            {
                e.Handled = true;
            }
        };
        list.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(list, owner(), activate, e), RoutingStrategies.Tunnel);
    }

    /// <summary>The row under <paramref name="source"/>, or null.</summary>
    public static ITreeRow? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ITreeRow;

    /// <summary>Whether a key or a click comes from a row's hover button.</summary>
    public static bool InButton(object? source) => (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    /// <summary>The chevron of a folder's row (a file's row has a blank place for it, where a double-click activates the row).</summary>
    public static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron")
        && RowAt(source) is { HasChildren: true };

    /// <summary>
    /// Shows <paramref name="row"/>: scrolled into view, selected and with the focus when the list has it (the lists don't
    /// scroll to their selection by themselves: Avalonia's scroll ran after every change to the rows, M56).
    /// </summary>
    public static void Reveal(ListBox list, object row, bool focus)
    {
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        if (focus)
        {
            (list.ContainerFromItem(row) as Control)?.Focus(NavigationMethod.Directional);
        }
    }

    private static bool Activate(ITreeRowsOwner tree, ITreeRow row, Func<ITreeRow, bool>? activate)
    {
        if (row.HasChildren)
        {
            tree.SetExpanded(row, !row.IsExpanded);
            return true;
        }

        return activate?.Invoke(row) ?? false;
    }

    private static void OnKeyDown(ListBox list, ITreeRowsOwner? tree, Func<ITreeRow, bool>? activate, KeyEventArgs e)
    {
        if (tree is null || list.SelectedItem is not ITreeRow row || InButton(e.Source))
        {
            return;
        }

        var recursive = e.KeyModifiers == KeyModifiers.Control;
        if (e.KeyModifiers != KeyModifiers.None && !recursive)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when row.HasChildren && recursive:
                tree.SetExpandedUnder(row, expanded: true);
                break;
            case Key.Left when row.HasChildren && recursive:
                tree.SetExpandedUnder(row, expanded: false);
                break;
            case Key.Enter when row.HasChildren && recursive:
                tree.SetExpandedUnder(row, expanded: !row.IsExpanded);
                break;
            case Key.Enter when !recursive:
                if (!Activate(tree, row, activate))
                {
                    return;
                }

                break;
            case Key.Right or Key.Add when row.HasChildren && !row.IsExpanded && !recursive:
                tree.SetExpanded(row, true);
                break;
            case Key.Multiply when row.HasChildren && !recursive:
                tree.SetExpandedUnder(row, expanded: true);
                break;
            case Key.Divide when row.HasChildren && !recursive:
                tree.SetExpandedUnder(row, expanded: false);
                break;
            case Key.Left or Key.Subtract when row.HasChildren && row.IsExpanded && !recursive:
                tree.SetExpanded(row, false);
                break;
            case Key.Left when !recursive && list.ItemsSource is System.Collections.IList rows && rows.IndexOf(row) is var index and > 0:
                // To the parent row.
                for (var i = index - 1; i >= 0; i--)
                {
                    if (rows[i] is ITreeRow parent && parent.Depth < row.Depth)
                    {
                        Reveal(list, parent, focus: true);
                        break;
                    }
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
