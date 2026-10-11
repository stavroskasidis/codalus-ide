using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The data grid (ADR 0037): a header row and a virtualized list of rows, each a row of cells with the column widths.
/// Editable grids use text boxes (a cell's NULL shows as a "NULL" placeholder; Ctrl+0 sets NULL, as in other SQL tools);
/// read-only grids use text. AOT-clean: cells are built in code, without reflection bindings.
/// </summary>
public sealed class DataGridView : UserControl
{
    private const double MarkerWidth = 22;
    private readonly ListBox _list;
    private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
    private DataGridModel? _model;

    public DataGridView()
    {
        _list = new ListBox
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<DataRowViewModel>((row, _) => row is null ? new Panel() : BuildRow(row)),
        };
        _list.Classes.Add("grid");
        _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(MinHeightProperty, 0d),
            },
        });
        AutomationProperties.SetName(_list, "Rows");
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, _list.SelectedItem as DataRowViewModel);
        var headerBorder = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, headerBorder.GetResourceObservable("Shell.Border"));
        headerBorder.Bind(Border.BackgroundProperty, headerBorder.GetResourceObservable("Shell.Panel"));
        var panel = new DockPanel();
        DockPanel.SetDock(headerBorder, Avalonia.Controls.Dock.Top);
        panel.Children.Add(headerBorder);
        panel.Children.Add(_list);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
    }

    public event EventHandler<DataRowViewModel?>? SelectionChanged;

    /// <summary>A cell was edited.</summary>
    public event EventHandler? Edited;

    public DataGridModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            Rebuild();
        }
    }

    public ListBox List => _list;

    public void Rebuild()
    {
        _header.Children.Clear();
        _list.ItemsSource = null;
        if (_model is null)
        {
            return;
        }

        _header.Children.Add(new Border { Width = MarkerWidth });
        for (var i = 0; i < _model.Columns.Length; i++)
        {
            var column = _model.Columns[i];
            var title = new TextBlock
            {
                Text = column.Name,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(6, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(title, $"{column.Name} ({column.Type}{(column.Nullable == true ? ", null" : string.Empty)})");
            _header.Children.Add(new Border { Width = _model.Widths.Length > i ? _model.Widths[i] : 120, Child = title });
        }

        _list.ItemsSource = _model.Rows;
    }

    private StackPanel BuildRow(DataRowViewModel row)
    {
        var model = _model!;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var marker = new TextBlock { Width = MarkerWidth, Text = row.Marker, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataRowViewModel.State))
            {
                marker.Text = row.Marker;
                panel.Opacity = row.State == DataRowStates.Deleted ? 0.45 : 1;
            }
        };
        panel.Opacity = row.State == DataRowStates.Deleted ? 0.45 : 1;
        panel.Children.Add(marker);
        for (var i = 0; i < model.Columns.Length; i++)
        {
            var width = model.Widths.Length > i ? model.Widths[i] : 120;
            panel.Children.Add(model.Editable ? EditableCell(row, i, width) : (Control)ReadOnlyCell(row.Values[i], width));
        }

        return panel;
    }

    private static TextBlock ReadOnlyCell(string? value, double width)
    {
        var text = new TextBlock
        {
            Width = width,
            Padding = new Thickness(6, 3),
            Text = value ?? "NULL",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (value is null)
        {
            text.Classes.Add("muted");
            text.FontStyle = FontStyle.Italic;
        }
        else if (value.Length > 40)
        {
            ToolTip.SetTip(text, value.Length > 2000 ? value[..2000] + "…" : value);
        }

        return text;
    }

    private TextBox EditableCell(DataRowViewModel row, int column, double width)
    {
        var box = new TextBox
        {
            Width = width,
            MinHeight = 0,
            Height = 24,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Text = row.Values[column] ?? string.Empty,
            PlaceholderText = row.Values[column] is null ? "NULL" : null,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, _model!.Columns[column].Name);
        var updating = false;
        box.TextChanged += (_, _) =>
        {
            if (updating)
            {
                return;
            }

            // Typing into a NULL cell makes it a value; clearing a value makes it an empty string (Ctrl+0 for NULL).
            row.Set(column, box.Text ?? string.Empty);
            box.PlaceholderText = null;
            box.FontWeight = row.IsChanged(column) ? FontWeight.SemiBold : FontWeight.Normal;
            Edited?.Invoke(this, EventArgs.Empty);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.D0 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                updating = true;
                box.Text = string.Empty;
                updating = false;
                row.Set(column, null);
                box.PlaceholderText = "NULL";
                box.FontWeight = row.IsChanged(column) ? FontWeight.SemiBold : FontWeight.Normal;
                Edited?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
        box.GotFocus += (_, _) => _list.SelectedItem = row;
        return box;
    }
}
