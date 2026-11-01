using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Coverage Results: double-click or Enter opens a class or method, Left/Right collapse and expand. The list is a
/// <see cref="TreeRowsList"/>: a tree to a screen reader (M57).
/// </summary>
public sealed partial class CoverageResultsView : UserControl, IPaneView
{
    public CoverageResultsView()
    {
        InitializeComponent();
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        List.ExpandRow = (row, expanded) => ViewModel?.SetExpanded((CoverageRowViewModel)row, expanded); // a screen reader's, as Left and Right
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
    }

    private CoverageResultsViewModel? ViewModel => DataContext as CoverageResultsViewModel;

    public void FocusPrimary()
    {
        if (ViewModel is { HasCoverage: true } vm && vm.Rows.Count > 0)
        {
            vm.Selected ??= vm.Rows[0];
            if (List.ContainerFromItem(vm.Selected) is { } container && container.Focus())
            {
                return;
            }
        }

        List.Focus();
    }

    private static CoverageRowViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as CoverageRowViewModel;

    private static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron");

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } row } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.F12:
                _ = vm.OpenSelectedAsync();
                break;
            case Key.Right when row.HasChildren && !row.IsExpanded:
                vm.SetExpanded(row, true);
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                vm.SetExpanded(row, false);
                break;
            default:
                return;
        }

        e.Handled = true;
        if (vm.Selected is { } selected)
        {
            List.ContainerFromItem(selected)?.Focus();
        }
    }
}
