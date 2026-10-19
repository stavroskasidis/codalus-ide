using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

public sealed partial class FindResultsView : UserControl, IPaneView
{
    public FindResultsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => Activate();
        List.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Activate();
                    e.Handled = true;
                    break;
                case Key.Left or Key.Right when Page is { } page && List.SelectedItem is FindResultRow { IsCollapsible: true } header:
                    e.Handled = page.SetExpanded(header, e.Key == Key.Right);
                    break;
            }
        };

        // The keys' hint in the status line shows where there is room for it beside the counts.
        SizeChanged += (_, e) => Hint.IsVisible = e.NewSize.Width >= 640;

        // F8 and Shift+F8 walk the results from anywhere in the window (its toolbar's tooltips say so).
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F8 && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None && Page is { } page)
            {
                _ = page.GoToAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };

        // A header's chevron collapses and expands its group.
        List.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Border>(includeSelf: true) is { } toggle && toggle.Classes.Contains("groupToggle")
                && toggle.DataContext is FindResultRow row && Page is { } page)
            {
                page.Toggle(row);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private FindResultsPage? Page => (DataContext as FindResultsViewModel)?.CurrentPage;

    /// <summary>Opens the selected match, or collapses or expands the selected header.</summary>
    private void Activate()
    {
        if (Page is not { } page || List.SelectedItem is not FindResultRow row)
        {
            return;
        }

        if (row.IsHeader)
        {
            page.Toggle(row);
        }
        else
        {
            _ = page.OpenAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
