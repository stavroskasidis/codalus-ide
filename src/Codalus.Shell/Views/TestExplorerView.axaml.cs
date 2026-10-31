using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Views;

/// <summary>
/// The Test Explorer: Enter runs the selection (Ctrl+Enter debugs), double-click opens the test's source,
/// Left/Right collapse and expand, Ctrl+F focuses the filter; the context menu runs, debugs, measures coverage and adds
/// tests to playlists. The list is a <see cref="TreeRowsList"/>: a tree to a screen reader (M57).
/// </summary>
public sealed partial class TestExplorerView : UserControl, IPaneView
{
    private bool _shown;
    private TestExplorerViewModel? _reported; // the view model told whether the explorer is shown

    public TestExplorerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // A closed tool window clears its view's data context before the view leaves the tree: the view model it
            // reported to hears that the explorer is hidden now.
            if (_reported is { } old && !ReferenceEquals(old, ViewModel))
            {
                if (_shown)
                {
                    old.SetViewShown(this, false);
                }

                _reported = null;
                _shown = false;
            }

            if (ViewModel is { } vm && !ReferenceEquals(vm, _reported))
            {
                _reported = vm;
                _shown = false;
                UpdateShown();
            }
        };
        // Shown or hidden: a closed or auto-hidden tool window keeps its view attached, invisible, and its next layout
        // pass tells (no event is raised for an ancestor's visibility). The whole view: the list hides while it's empty.
        AttachedToVisualTree += (_, _) => UpdateShown();
        DetachedFromVisualTree += (_, _) => UpdateShown(detaching: true); // still attached while the event is raised
        LayoutUpdated += (_, _) => UpdateShown();
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        List.ExpandRow = (row, expanded) => ViewModel?.SetExpanded((TestRowViewModel)row, expanded); // a screen reader's, as Left and Right
        Filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.ApplyFilter();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && ViewModel is { HasTests: true } vm)
            {
                vm.Selected ??= vm.Rows[0];
                List.ContainerFromItem(vm.Selected)?.Focus();
                e.Handled = true;
            }
        };
        List.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.Source) is not null && !IsChevron(e.Source))
            {
                _ = ViewModel?.OpenSelectedAsync();
            }
        };
        List.Tapped += (_, e) =>
        {
            if (RowAt(e.Source) is { } row && IsChevron(e.Source))
            {
                ViewModel?.SetExpanded(row, !row.IsExpanded);
            }
        };
        List.ContextRequested += OnContextRequested;
    }

    private TestExplorerViewModel? ViewModel => DataContext as TestExplorerViewModel;

    /// <summary>Tells the view model whether the explorer is shown: changes to the tests wait while it isn't (M56).</summary>
    private void UpdateShown(bool detaching = false)
    {
        var shown = !detaching && this.IsAttachedToVisualTree() && IsEffectivelyVisible;
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        _reported?.SetViewShown(this, shown);
    }

    private ShellHost? Shell => (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as ShellHost;

    public void FocusPrimary()
    {
        if (ViewModel is { HasTests: true } vm)
        {
            vm.Selected ??= vm.Rows.FirstOrDefault();
            if (vm.Selected is { } selected && List.ContainerFromItem(selected) is { } container && container.Focus())
            {
                return;
            }

            List.Focus();
        }
        else
        {
            Filter.Focus();
        }
    }

    private static TestRowViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TestRowViewModel;

    private static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron");

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } row } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                vm.DebugSelectedCommand.Execute(null);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                vm.RunSelectedCommand.Execute(null);
                break;
            case Key.F12 when e.KeyModifiers == KeyModifiers.None:
                _ = vm.OpenSelectedAsync();
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.None && row.HasChildren && !row.IsExpanded:
                vm.SetExpanded(row, true);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None && row.HasChildren && row.IsExpanded:
                vm.SetExpanded(row, false);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None && vm.Rows.IndexOf(row) is var index and > 0:
                for (var i = index - 1; i >= 0; i--)
                {
                    if (vm.Rows[i].Depth < row.Depth)
                    {
                        vm.Selected = vm.Rows[i];
                        List.ScrollIntoView(vm.Rows[i]);
                        List.ContainerFromItem(vm.Rows[i])?.Focus();
                        break;
                    }
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || RowAt(e.Source) is not { } row || Shell is not { } shell)
        {
            return;
        }

        vm.Selected = row;
        var keys = vm.SelectedKeys;
        MenuItem Item(string header, Action action, string? icon = null, bool enabled = true)
        {
            var item = Controls.MenuEntry.Create(header, icon, click: action);
            item.IsEnabled = enabled;
            return item;
        }

        var idle = !shell.Tests.IsRunning;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Run", () => _ = shell.RunTestsAsync(TestRunKind.Run, keys), "play", idle));
        menu.Items.Add(Item("Debug", () => _ = shell.RunTestsAsync(TestRunKind.Debug, keys), "debug", idle));
        menu.Items.Add(Item("Run with Coverage", () => _ = shell.RunTestsAsync(TestRunKind.Coverage, keys), "coverage", idle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Go to Test", () => _ = vm.OpenSelectedAsync(), "go-to", enabled: row.Group.Node?.Uri is not null));
        menu.Items.Add(Item("Add to Playlist…", () => _ = shell.AddToPlaylistAsync(keys), "add"));
        if (vm.Playlist is { } playlist)
        {
            menu.Items.Add(Item($"Remove from '{playlist.Name}'", () => _ = shell.RemoveFromPlaylistAsync(playlist, keys), "subtract"));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy Name", () => Copy(row.Group.Node?.FullLabel ?? row.Label), "copy"));
        if (row.Group.Node is { IsLeaf: true, Result: { } result })
        {
            menu.Items.Add(Item("Copy Details", () => Copy(TestExplorerViewModel.DetailsText(row.Group.Node, result)), "copy"));
            if (shell.Ai.IsEnabled && result.Outcome is not TestOutcome.Passed)
            {
                menu.Items.Add(Item("Ask AI Why It Fails", () => _ = shell.ExplainTestWithAiAsync(), "sparkle"));
            }
        }

        menu.Open(List.ContainerFromItem(row) ?? List);
        e.Handled = true;
    }

    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, text);
        }
    }

    private void OnOpenFrame(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StackFrameRow frame })
        {
            ViewModel?.OpenFrameCommand.Execute(frame);
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e) => _ = ViewModel?.OpenSelectedAsync();
}
