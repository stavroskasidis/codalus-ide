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
        else if (e.PropertyName is nameof(TerminalViewModel.FindVisible) && _viewModel?.FindVisible == true)
        {
            FindBox.Focus();
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

    /// <summary>Finds the text in the buffer (case-insensitive), from the last match on.</summary>
    private void OnFind(object? sender, bool backwards)
    {
        var control = Active;
        if (control.Session is not { } session || _viewModel?.FindText is not { Length: > 0 } query)
        {
            return;
        }

        var terminal = session.Terminal;
        lock (terminal.SyncRoot)
        {
            var total = terminal.TotalRows;
            var (startRow, startColumn) = _lastMatch ?? (backwards ? (total - 1, int.MaxValue) : (control.ViewportTop, -1));
            for (var step = 0; step <= total; step++)
            {
                var row = ((backwards ? startRow - step : startRow + step) % total + total) % total;
                var text = terminal.GetRow(row).GetText();
                var columns = terminal.GetRow(row).TextColumns();
                var index = backwards
                    ? (step == 0 && startColumn != int.MaxValue ? (startColumn > 0 ? text.LastIndexOf(query, Math.Min(startColumn - 1, text.Length - 1), StringComparison.OrdinalIgnoreCase) : -1) : text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase))
                    : text.IndexOf(query, step == 0 ? Math.Min(startColumn + 1, text.Length) : 0, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var column = index < columns.Length ? columns[index] : index;
                    _lastMatch = (row, index);
                    control.Select(row, column, query.Length);
                    return;
                }
            }
        }

        _lastMatch = null;
        control.ClearSelection();
    }
}
