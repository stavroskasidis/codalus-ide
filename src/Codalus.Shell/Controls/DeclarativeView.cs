using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Plugins.Protocol;

namespace Codalus.Shell.Controls;

/// <summary>
/// Renders a plugin's declarative view (D2, plugin API 1.0, M22a) with the shell's own controls and theme. Built in code
/// from the element tree (no bindings, so AOT stays clean); an update rebuilds it, keeping what the user typed in a
/// text box whose value the plugin didn't change. What the user does goes to <see cref="Raised"/>.
/// </summary>
public sealed class DeclarativeView : UserControl
{
    private readonly Dictionary<string, string> _typed = new(StringComparer.Ordinal);
    private Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private ViewNodeDto? _root;

    public DeclarativeView()
    {
        Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>What the user did: element id, kind, value and row (as the plugin API's <c>ViewEvent</c>).</summary>
    public event Action<string, string, string?, int?>? Raised;

    /// <summary>The empty state while the plugin hasn't set its view yet.</summary>
    public string EmptyText { get; set; } = "Loading…";

    public ViewNodeDto? Root
    {
        get => _root;
        set
        {
            var previous = _values;
            _root = value;
            _values = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (value is not null)
            {
                Collect(value, _values);
            }

            // A value the plugin changed replaces what was typed; one it kept leaves the typing alone.
            foreach (var id in _typed.Keys.ToList())
            {
                if (!_values.TryGetValue(id, out var now) || !previous.TryGetValue(id, out var before) || now != before)
                {
                    _typed.Remove(id);
                }
            }

            ((ScrollViewer)Content!).Content = value is null
                ? new TextBlock { Text = EmptyText, Classes = { "emptyState" } }
                : new Border { Padding = new Thickness(12), Child = Build(value) };
        }
    }

    private static void Collect(ViewNodeDto node, Dictionary<string, string?> values)
    {
        if (node.Id is { } id)
        {
            values[id] = node.Value;
        }

        foreach (var child in node.Children ?? [])
        {
            Collect(child, values);
        }
    }

    private void Raise(string id, string kind, string? value = null, int? row = null) => Raised?.Invoke(id, kind, value, row);

    private Control Build(ViewNodeDto node)
    {
        Control control = node.Kind switch
        {
            "stack" => Stack(node),
            "text" => Text(node),
            "button" => Button(node),
            "textbox" => TextBox(node),
            "checkbox" => CheckBox(node),
            "dropdown" => Dropdown(node),
            "table" => Table(node),
            "toolbar" => Toolbar(node),
            "separator" => new Border { Height = 1, Margin = new Thickness(0, 4), Classes = { "separator" }, Background = Brush("Shell.Border") },
            _ => new TextBlock { Text = $"(unknown element '{node.Kind}')", Classes = { "muted" } },
        };
        control.IsEnabled = !node.Disabled;
        if (node.Id is { } id)
        {
            control.Name = "view_" + id;
            Avalonia.Automation.AutomationProperties.SetAutomationId(control, id);
        }

        return control;
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;

    private StackPanel Stack(ViewNodeDto node)
    {
        var horizontal = node.Orientation == "horizontal";
        var panel = new StackPanel { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = horizontal ? 8 : 6 };
        foreach (var child in node.Children ?? [])
        {
            panel.Children.Add(Build(child));
        }

        return panel;
    }

    private static TextBlock Text(ViewNodeDto node)
    {
        var text = new TextBlock { Text = node.Text ?? string.Empty, TextWrapping = TextWrapping.Wrap };
        switch (node.Style)
        {
            case "heading":
                text.FontSize = 16;
                text.FontWeight = FontWeight.SemiBold;
                break;
            case "muted":
                text.Classes.Add("muted");
                break;
            case "code":
                text.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
                break;
        }

        return text;
    }

    private Button Button(ViewNodeDto node)
    {
        var button = new Button { Content = node.Text ?? node.Id };
        if (node.Style == "primary")
        {
            button.Classes.Add("accent");
        }

        button.Click += (_, _) => Raise(node.Id ?? string.Empty, "click");
        return button;
    }

    private TextBox TextBox(ViewNodeDto node)
    {
        var id = node.Id ?? string.Empty;
        var box = new TextBox
        {
            Text = _typed.TryGetValue(id, out var typed) ? typed : node.Value ?? string.Empty,
            PlaceholderText = node.Placeholder,
            MinWidth = 160,
        };
        Avalonia.Automation.AutomationProperties.SetName(box, node.Placeholder ?? id);
        var committed = box.Text;
        void Commit()
        {
            if (box.Text != committed)
            {
                committed = box.Text;
                Raise(id, "change", box.Text);
            }
        }

        box.TextChanged += (_, _) => _typed[id] = box.Text ?? string.Empty;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    private CheckBox CheckBox(ViewNodeDto node)
    {
        var box = new CheckBox { Content = node.Text, IsChecked = node.Checked == true };
        box.IsCheckedChanged += (_, _) => Raise(node.Id ?? string.Empty, "change", box.IsChecked == true ? "true" : "false");
        return box;
    }

    private ComboBox Dropdown(ViewNodeDto node)
    {
        var options = node.Options ?? [];
        var box = new ComboBox { ItemsSource = options, SelectedIndex = node.Value is { } selected ? Array.IndexOf(options, selected) : -1, MinWidth = 160 };
        Avalonia.Automation.AutomationProperties.SetName(box, node.Id);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string value && value != node.Value)
            {
                Raise(node.Id ?? string.Empty, "change", value);
            }
        };
        return box;
    }

    /// <summary>A table: a header row, then rows that select on click and activate on double-click or Enter.</summary>
    private StackPanel Table(ViewNodeDto node)
    {
        var columns = node.Columns ?? [];
        var rows = node.Rows ?? [];
        var id = node.Id ?? string.Empty;
        var definitions = string.Join(',', Enumerable.Repeat("*", Math.Max(1, columns.Length)));
        Grid Row(IReadOnlyList<string> cells, bool header)
        {
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(definitions) };
            for (var i = 0; i < Math.Max(1, columns.Length); i++)
            {
                var cell = new TextBlock { Text = i < cells.Count ? cells[i] : string.Empty, Margin = new Thickness(6, 3), TextTrimming = TextTrimming.CharacterEllipsis };
                if (header)
                {
                    cell.FontWeight = FontWeight.SemiBold;
                }

                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }

            return grid;
        }

        var list = new ListBox { ItemsSource = rows.Select(r => Row(r, header: false)).ToList(), MaxHeight = 480 };
        Avalonia.Automation.AutomationProperties.SetName(list, id);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                Raise(id, "select", null, list.SelectedIndex);
            }
        };
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                Raise(id, "activate", null, list.SelectedIndex);
            }
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedIndex >= 0)
            {
                Raise(id, "activate", null, list.SelectedIndex);
                e.Handled = true;
            }
        };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush("Shell.Border"), Child = Row(columns, header: true) });
        panel.Children.Add(list);
        return panel;
    }

    private StackPanel Toolbar(ViewNodeDto node)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var child in node.Children ?? [])
        {
            var built = Build(child);
            if (built is Button button)
            {
                button.Classes.Add("toolbar");
            }

            bar.Children.Add(built);
        }

        return bar;
    }
}
