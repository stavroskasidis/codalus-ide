using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>A table's data (ADR 0037): the toolbar (pages, filter, edits, export and import), the grid, the status.</summary>
public sealed class TableDataView : UserControl, IPaneView
{
    private readonly TableDataViewModel _vm;
    private readonly DataGridView _grid = new();
    private readonly TextBox _filter;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _message = new() { Margin = new Thickness(10, 4), TextWrapping = TextWrapping.Wrap };
    private readonly List<Control> _editing = [];

    public TableDataView(TableDataViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        _status.Classes.Add("muted");
        _filter = new TextBox { Width = 260, PlaceholderText = "Filter: a WHERE condition, then Enter", Text = vm.Filter, Margin = new Thickness(6, 0) };
        AutomationProperties.SetName(_filter, "Filter rows");
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                vm.Filter = _filter.Text ?? string.Empty;
                _ = vm.GoAsync(0);
                e.Handled = true;
            }
        };

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3) };
        toolbar.Children.Add(Button("refresh", null, vm.RefreshCommand, "Refresh"));
        toolbar.Children.Add(Button(null, "|◀", vm.FirstCommand, "First page"));
        toolbar.Children.Add(Button(null, "◀", vm.PreviousCommand, "Previous page"));
        toolbar.Children.Add(Button(null, "▶", vm.NextCommand, "Next page"));
        toolbar.Children.Add(Button(null, "▶|", vm.LastCommand, "Last page"));
        toolbar.Children.Add(_filter);
        foreach (var (text, command, tip) in new[]
        {
            ("Add Row", vm.AddRowCommand, "A new row, inserted when you commit"),
            ("Delete Row", vm.DeleteRowCommand, "Mark the selected row for deletion"),
            ("Commit", vm.CommitCommand, "Save the changes in one transaction"),
            ("Revert", vm.RevertCommand, "Discard the changes"),
        })
        {
            var button = Button(null, text, command, tip);
            _editing.Add(button);
            toolbar.Children.Add(button);
        }

        toolbar.Children.Add(Button(null, "Export…", vm.ExportCommand, "Export every row (not just this page) as CSV or JSON"));
        var import = Button(null, "Import…", vm.ImportCommand, "Insert the rows of a CSV or JSON file");
        _editing.Add(import);
        toolbar.Children.Add(import);

        var footer = new DockPanel { Margin = new Thickness(4, 2) };
        footer.Children.Add(_status);
        var top = new StackPanel();
        top.Children.Add(toolbar);
        top.Children.Add(_message);
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(_grid);
        Content = root;

        _grid.SelectionChanged += (_, row) => vm.SelectedRow = row;
        vm.GridChanged += (_, _) => Update();
        vm.PropertyChanged += (_, _) => UpdateText();
        Update();
    }

    public DataGridView Grid => _grid;

    private static Button Button(string? icon, string? text, System.Windows.Input.ICommand command, string tip)
    {
        var button = new Button { Command = command, Content = icon is not null ? new Icon { Kind = icon } : text };
        button.Classes.Add("toolbar");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, text is { Length: > 2 } ? text : tip);
        return button;
    }

    private void Update()
    {
        _grid.Model = _vm.Grid;
        UpdateText();
    }

    private void UpdateText()
    {
        _status.Text = _vm.Status + (_vm.HasPendingChanges ? $" · {_vm.PendingChanges} pending change(s)" : string.Empty) + (_vm.IsBusy ? " · working…" : string.Empty);
        _message.Text = _vm.Message;
        _message.IsVisible = _vm.HasMessage;
        foreach (var control in _editing)
        {
            control.IsEnabled = _vm.IsEditable;
        }
    }

    public void FocusPrimary() => _grid.List.Focus();
}

/// <summary>The Query Results window (ADR 0037): result grids, messages, the plan and the history.</summary>
public sealed class QueryResultsView : UserControl, IPaneView
{
    private readonly QueryResultsViewModel _vm;
    private readonly TabControl _tabs = new();
    private readonly TabControl _results = new() { TabStripPlacement = Avalonia.Controls.Dock.Bottom };
    private readonly TextBox _messages;
    private readonly TreeView _plan = new() { Background = Brushes.Transparent };
    private readonly ListBox _history = new() { Background = Brushes.Transparent };
    private readonly TextBlock _connection = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };

    public QueryResultsView(QueryResultsViewModel vm, Infrastructure.EditorFont font)
    {
        _vm = vm;
        DataContext = vm;
        _status.Classes.Add("muted");
        _messages = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = font.Family, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(_messages, "Messages");
        AutomationProperties.SetName(_plan, "Execution plan");
        AutomationProperties.SetName(_history, "Query history");
        _plan.ItemTemplate = new FuncTreeDataTemplate<PlanNodeViewModel>((node, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Icon { Kind = "plan", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = node.Operation, FontWeight = node.IsExpensive ? FontWeight.Bold : FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (node.Detail is { } detail)
            {
                row.Children.Add(new TextBlock { Text = detail, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis });
            }

            var numbers = new TextBlock { Text = node.Numbers, VerticalAlignment = VerticalAlignment.Center };
            numbers.Classes.Add("muted");
            row.Children.Add(numbers);
            AutomationProperties.SetName(row, node.AccessibleName);
            return row;
        }, node => node.Children);
        _plan.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem item)
            {
                item.IsExpanded = true;
            }
        };
        _plan.ItemsSource = vm.Plan;
        _history.ItemTemplate = new FuncDataTemplate<QueryHistoryRow>((row, _) =>
        {
            var panel = new StackPanel { Spacing = 1 };
            if (row is null)
            {
                return panel;
            }

            panel.Children.Add(new TextBlock { Text = row.Text, FontFamily = font.Family, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = row.Detail, FontSize = 11 };
            detail.Classes.Add("muted");
            panel.Children.Add(detail);
            AutomationProperties.SetName(panel, row.AccessibleName);
            return panel;
        });
        _history.ItemsSource = vm.History;
        _history.DoubleTapped += (_, _) =>
        {
            if (_history.SelectedItem is QueryHistoryRow row)
            {
                vm.OpenHistory(row);
            }
        };

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3) };
        toolbar.Children.Add(Button("play", "Execute", vm.ExecuteCommand, "Run the selection, or the whole query (Ctrl+Shift+E)"));
        toolbar.Children.Add(Button(null, "Execute Statement", vm.ExecuteStatementCommand, "Run the statement at the caret"));
        toolbar.Children.Add(Button("stop", "Cancel", vm.CancelCommand, "Cancel the running query (Alt+Break)"));
        toolbar.Children.Add(Button("plan", "Estimated Plan", vm.EstimatedPlanCommand, "Show the estimated execution plan (Ctrl+L)"));
        toolbar.Children.Add(Button(null, "Actual Plan", vm.ActualPlanCommand, "Run the query and show its actual plan (Ctrl+M)"));
        var connection = Button("database", null, vm.ConnectionCommand, "Change the query editor's connection");
        toolbar.Children.Add(connection);
        toolbar.Children.Add(_connection);

        var messagesHost = new ScrollViewer { Content = _messages };
        var planHost = new DockPanel();
        var openPlan = Button(null, "Open Plan", vm.OpenPlanCommand, "Open the engine's own plan (XML, JSON or text) in an editor");
        DockPanel.SetDock(openPlan, Avalonia.Controls.Dock.Top);
        openPlan.HorizontalAlignment = HorizontalAlignment.Left;
        planHost.Children.Add(openPlan);
        planHost.Children.Add(_plan);
        var historyHost = new DockPanel();
        var clear = Button(null, "Clear History", vm.ClearHistoryCommand, "Forget the query history");
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(clear, Avalonia.Controls.Dock.Top);
        historyHost.Children.Add(clear);
        historyHost.Children.Add(_history);

        _tabs.Items.Add(new TabItem { Header = "Results", Content = _results });
        _tabs.Items.Add(new TabItem { Header = "Messages", Content = messagesHost });
        _tabs.Items.Add(new TabItem { Header = "Plan", Content = planHost });
        _tabs.Items.Add(new TabItem { Header = "History", Content = historyHost });
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex >= 0)
            {
                vm.SelectedTab = _tabs.SelectedIndex;
            }
        };

        var top = new DockPanel();
        DockPanel.SetDock(_status, Avalonia.Controls.Dock.Right);
        top.Children.Add(_status);
        top.Children.Add(toolbar);
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_tabs);
        Content = root;

        vm.ResultsChanged += (_, _) => Update();
        vm.PropertyChanged += (_, _) => UpdateText();
        Update();
    }

    /// <summary>The result grids (tests and screenshots).</summary>
    public IEnumerable<DataGridView> ResultGrids => _results.Items.OfType<TabItem>().Select(t => t.Content).OfType<DataGridView>();

    private static Button Button(string? icon, string? text, System.Windows.Input.ICommand command, string tip)
    {
        object content = icon is not null && text is not null
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new Icon { Kind = icon }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } }
            : icon is not null ? new Icon { Kind = icon } : text!;
        var button = new Button { Command = command, Content = content };
        button.Classes.Add("toolbar");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, text ?? tip);
        return button;
    }

    private void Update()
    {
        _results.Items.Clear();
        foreach (var set in _vm.Results)
        {
            _results.Items.Add(new TabItem { Header = set.Header, Content = new DataGridView { Model = set.Grid } });
        }

        if (_results.Items.Count > 0)
        {
            _results.SelectedIndex = 0;
        }

        UpdateText();
    }

    private void UpdateText()
    {
        _connection.Text = _vm.ConnectionLabel ?? "No connection: open a .sql editor";
        _status.Text = _vm.IsRunning ? "Running…" : _vm.Status;
        _messages.Text = _vm.Messages;
        if (_tabs.SelectedIndex != _vm.SelectedTab)
        {
            _tabs.SelectedIndex = _vm.SelectedTab;
        }
    }

    public void FocusPrimary() => _tabs.Focus();
}
