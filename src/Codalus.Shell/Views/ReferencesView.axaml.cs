using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

public sealed partial class ReferencesView : UserControl, IPaneView
{
    public ReferencesView()
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
                case Key.Left or Key.Right when Model is { } model && List.SelectedItem is ReferenceRow { IsCollapsible: true } header:
                    e.Handled = model.SetExpanded(header, e.Key == Key.Right);
                    break;
                case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                    Copy();
                    e.Handled = true;
                    break;
            }
        };

        // F8 and Shift+F8 walk the references from anywhere in the window.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F8 && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None && Model is { } model)
            {
                _ = model.GoToAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };

        // A header's chevron collapses and expands its group.
        List.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Border>(includeSelf: true) is { } toggle && toggle.Classes.Contains("groupToggle")
                && toggle.DataContext is ReferenceRow row && Model is { } model)
            {
                model.Toggle(row);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        CopyButton.Click += (_, _) => Copy();

        // The header lines up with the rows, whatever inset the theme gives them (Classic's 3 px and the rows' padding).
        List.LayoutUpdated += (_, _) => AlignHeader();

        // The columns' splitters resize every row as they move; letting go saves the widths.
        Header.AddHandler(Thumb.DragDeltaEvent, (_, e) =>
        {
            if (e.Source is Thumb { Tag: string tag } && int.TryParse(tag, out var column) && Model is { } model)
            {
                model.Resize(column, e.Vector.X);
            }
        });
        Header.AddHandler(Thumb.DragCompletedEvent, (_, _) => Model?.CommitWidths());

        // A column's filter button lists the column's values to check and uncheck.
        Header.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: string tag } button && Enum.TryParse<ReferenceColumn>(tag, out var column)
                && Model?.Filters.FirstOrDefault(f => f.Column == column) is { } filter)
            {
                ShowFilter(button, filter);
                e.Handled = true;
            }
        });
    }

    private ReferencesViewModel? Model => DataContext as ReferencesViewModel;

    private void AlignHeader()
    {
        if (List.GetRealizedContainers().FirstOrDefault()?.GetVisualDescendants().OfType<ColumnsPanel>().FirstOrDefault() is not { } row
            || row.TranslatePoint(default, List) is not { } at)
        {
            return;
        }

        var margin = new Thickness(at.X, 0, Math.Max(0, List.Bounds.Width - at.X - row.Bounds.Width), 0);
        if (Header.Margin != margin)
        {
            Header.Margin = margin;
        }
    }

    /// <summary>Opens the selected reference, or collapses or expands the selected header.</summary>
    private void Activate()
    {
        if (Model is not { } model || List.SelectedItem is not ReferenceRow row)
        {
            return;
        }

        if (row.IsHeader)
        {
            model.Toggle(row);
        }
        else
        {
            _ = model.OpenAsync(row);
        }
    }

    private void Copy()
    {
        if (Model is { } model && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && model.CopyText() is { Length: > 0 } text)
        {
            _ = Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, text);
        }
    }

    /// <summary>
    /// The filter's flyout: Select All and Clear, then the column's values with their check boxes, in a list box that
    /// creates only the rows in view (a check box for each of thousands of files or members took a second to open, M56).
    /// The check boxes follow the values, Select All's and Clear's too. The keyboard works as with the check boxes before:
    /// Tab reaches a selected row, the arrow keys move, Space checks or unchecks (<see cref="ReferenceFilterList"/>).
    /// </summary>
    private void ShowFilter(Button button, ReferenceColumnFilter filter)
    {
        Button Action(string text, bool check)
        {
            var action = new Button { Content = text, Classes = { "subtle" } };
            action.Click += (_, _) => (check ? filter.SelectAllCommand : filter.ClearCommand).Execute(null); // one refresh for the whole list
            return action;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        actions.Children.Add(Action(Localizer.Get("references.selectAll", "Select All"), true));
        actions.Children.Add(Action(Localizer.Get("references.clearSelection", "Clear"), false));
        var list = new ReferenceFilterList
        {
            ItemsSource = filter.Values,
            ItemTemplate = (IDataTemplate)Resources["FilterValueTemplate"]!,
            MaxHeight = 320,
            Background = Avalonia.Media.Brushes.Transparent,
            Classes = { "filterValues" },
        };
        AutomationProperties.SetName(list, ToolTip.GetTip(button) as string ?? string.Empty);
        var content = new DockPanel { MinWidth = 180 };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Top);
        content.Children.Add(actions);
        content.Children.Add(list);
        FilterFlyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedRight };
        FilterFlyout.ShowAt(button);
    }

    /// <summary>The last filter flyout shown (tests).</summary>
    internal Flyout? FilterFlyout { get; private set; }

    public void FocusPrimary() => List.Focus();
}
