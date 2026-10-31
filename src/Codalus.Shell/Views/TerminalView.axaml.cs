using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Terminal tool window: shows the selected tab's terminal (or split pair) and wires scrolling and find.</summary>
public sealed partial class TerminalView : UserControl, IPaneView
{
    private TerminalViewModel? _viewModel;
    private TerminalControl _lastFocused;

    public TerminalView()
    {
        InitializeComponent();
        _lastFocused = Primary;
        foreach (var (control, scroll) in new[] { (Primary, PrimaryScroll), (Secondary, SecondaryScroll) })
        {
            control.ViewportChanged += (_, _) => SyncScroll(control, scroll);
            control.GotFocus += (_, _) => _lastFocused = control;
            control.LinkActivated += (_, link) => _viewModel?.OpenLink(link);
            scroll.Scroll += (_, e) => control.ScrollTo((int)Math.Round(e.NewValue));
        }

        ProfilePicker.SelectionChanged += (_, _) =>
        {
            if (ProfilePicker.SelectedItem is TerminalProfileChoice choice && _viewModel is { } vm)
            {
                vm.NewTerminal(choice.Id);
                ProfilePicker.SelectedItem = null;
                Focus(Primary);
            }
        };
    }

    public void FocusPrimary() => Focus(_lastFocused.Session is null ? Primary : _lastFocused);

    private static void Focus(TerminalControl control) => control.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Appearance.PropertyChanged -= OnAppearanceChanged;
            _viewModel.ClearRequested -= OnClear;
            _viewModel.FindRequested -= OnFind;
        }

        _viewModel = DataContext as TerminalViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelChanged;
            vm.Appearance.PropertyChanged += OnAppearanceChanged;
            vm.ClearRequested += OnClear;
            vm.FindRequested += OnFind;
            ApplyAppearance();
            Bind();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalViewModel.Selected))
        {
            Bind();
        }
        else if (e.PropertyName is nameof(TerminalViewModel.FindVisible))
        {
            if (_viewModel?.FindVisible == true)
            {
                FindBox.Focus();
            }
            else
            {
                StopFind(); // closed: a find still looking selects nothing
            }
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => ApplyAppearance();

    private void ApplyAppearance()
    {
        if (_viewModel?.Appearance is not { } appearance)
        {
            return;
        }

        foreach (var control in new[] { Primary, Secondary })
        {
            control.SetFont(appearance.FontFamily, appearance.FontSize);
            control.Palette = appearance.Palette;
            control.CopyOnSelect = appearance.CopyOnSelect;
        }
    }

    private void Bind()
    {
        // Another terminal: a find still looking was the last one's, and so was its last match.
        StopFind();
        _lastMatch = null;
        var tab = _viewModel?.Selected;
        if (tab is not null)
        {
            tab.PropertyChanged -= OnTabChanged;
            tab.PropertyChanged += OnTabChanged;
        }

        Primary.Session = tab?.Primary;
        Secondary.Session = tab?.Secondary;
        var split = tab?.Secondary is not null;
        Splitter.IsVisible = split;
        SecondaryPane.IsVisible = split;
        Panes.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Panes.ColumnDefinitions[1].Width = split ? new GridLength(4) : new GridLength(0);
        Panes.IsVisible = tab is not null;
        SyncScroll(Primary, PrimaryScroll);
        SyncScroll(Secondary, SecondaryScroll);
        if (IsKeyboardFocusWithin || tab is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus(split && _lastFocused == Secondary ? Secondary : Primary));
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTabViewModel.Secondary) && ReferenceEquals(sender, _viewModel?.Selected))
        {
            Bind();
            if (_viewModel?.Selected?.Secondary is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus(Secondary));
            }
        }
    }

    private static void SyncScroll(TerminalControl control, ScrollBar scroll)
    {
        scroll.Maximum = control.MaximumTop;
        scroll.ViewportSize = control.Rows;
        scroll.Value = control.ViewportTop;
        scroll.IsVisible = control.MaximumTop > 0;
    }

    private TerminalControl Active => _lastFocused.Session is not null ? _lastFocused : Primary;

    private void OnClear(object? sender, EventArgs e) => Active.Session?.Terminal.Clear();

    private void OnFindNext(object? sender, RoutedEventArgs e) => _viewModel?.FindNext(backwards: false);

    private void OnFindPrevious(object? sender, RoutedEventArgs e) => _viewModel?.FindNext(backwards: true);

    private (int Row, int Column)? _lastMatch;
    private int _findGeneration;
    private bool _finding;
    private (TerminalControl Control, string Query, bool Backwards) _search; // the find still looking, while _finding

    /// <summary>How many rows a find looks through per dispatcher pass, holding the terminal for that long.</summary>
    internal const int FindSliceRows = 2000;

    /// <summary>Whether a find is still looking through the rows (tests).</summary>
    internal bool IsFinding => _finding;

    /// <summary>
    /// Finds the text in the buffer (case-insensitive), from the last match on, a slice of rows per dispatcher pass at
    /// background priority, a newer find (the next keystroke) dropping the rest: the whole scrollback at once, up to
    /// 1,000,000 rows, held the UI thread and the terminal's output for seconds (M56).
    /// </summary>
    private void OnFind(object? sender, bool backwards)
    {
        var control = Active;
        if (_finding && _search == (control, _viewModel?.FindText, backwards))
        {
            return; // Find Next while the same find is still looking: it goes on (starting it again lost the rows looked at)
        }

        StopFind();
        if (control.Session is not { } session || _viewModel?.FindText is not { Length: > 0 } query)
        {
            return;
        }

        var generation = _findGeneration;
        _search = (control, query, backwards);

        var terminal = session.Terminal;
        int startRow, startColumn;
        lock (terminal.SyncRoot)
        {
            (startRow, startColumn) = _lastMatch ?? (backwards ? (terminal.TotalRows - 1, int.MaxValue) : (control.ViewportTop, -1));
        }

        _finding = true;
        FindFrom(control, terminal, query, backwards, startRow, startColumn, 0, generation);
    }

    /// <summary>Drops the find still looking, if any: its next slice does nothing.</summary>
    private void StopFind()
    {
        _findGeneration++;
        _finding = false;
    }

    private void FindFrom(TerminalControl control, Codalus.Terminal.VirtualTerminal terminal, string query, bool backwards, int startRow, int startColumn, int step, int generation)
    {
        if (generation != _findGeneration)
        {
            return;
        }

        lock (terminal.SyncRoot)
        {
            // Rows that scroll out of a full scrollback move the others up meanwhile: a slice may look at a row twice or
            // miss one, but what it selects is the row it read, under this lock.
            var total = terminal.TotalRows;
            for (var end = Math.Min(step + FindSliceRows, total + 1); step < end; step++)
            {
                var row = ((backwards ? startRow - step : startRow + step) % total + total) % total;
                var line = terminal.GetRow(row);
                var text = line.GetText();
                var index = backwards
                    ? (step == 0 && startColumn != int.MaxValue ? (startColumn > 0 ? text.LastIndexOf(query, Math.Min(startColumn - 1, text.Length - 1), StringComparison.OrdinalIgnoreCase) : -1) : text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase))
                    : text.IndexOf(query, step == 0 ? Math.Min(startColumn + 1, text.Length) : 0, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var columns = line.TextColumns(); // the matched row's only
                    var column = index < columns.Length ? columns[index] : index;
                    _lastMatch = (row, index);
                    _finding = false;
                    control.Select(row, column, query.Length);
                    return;
                }
            }

            if (step > total)
            {
                _lastMatch = null;
                _finding = false;
                control.ClearSelection();
                return;
            }
        }

        var next = step;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => FindFrom(control, terminal, query, backwards, startRow, startColumn, next, generation),
            Avalonia.Threading.DispatcherPriority.Background);
    }
}
