using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Codalus.Shell.Controls;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The AI Console tool window (ADR 0095): the selected console's terminal, the agent picker that starts a new one, and
/// files dropped on it (from Solution Explorer or the OS), which become references in the prompt.
/// </summary>
public sealed partial class AiConsoleView : UserControl, IPaneView
{
    private AiConsoleViewModel? _viewModel;
    private AiConsoleTabViewModel? _tab;

    public AiConsoleView()
    {
        InitializeComponent();
        Console.ViewportChanged += (_, _) => SyncScroll();
        Console.LinkActivated += (_, link) => _viewModel?.OpenLink(link);
        ConsoleScroll.Scroll += (_, e) => Console.ScrollTo((int)Math.Round(e.NewValue));
        AgentPicker.SelectionChanged += (_, _) =>
        {
            if (AgentPicker.SelectedItem is AiConsoleAgentChoice choice && _viewModel is { } vm)
            {
                AgentPicker.SelectedItem = null;
                vm.NewCommand.Execute(choice.Name);
            }
        };
        Pane.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Pane.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>The console's terminal (tests).</summary>
    internal TerminalControl Terminal => Console;

    public void FocusPrimary() => Console.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Shown (its command, or the layout that had it open): the kept consoles start, or the first one.
        _viewModel?.Activate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Appearance.PropertyChanged -= OnAppearanceChanged;
        }

        _viewModel = DataContext as AiConsoleViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelChanged;
            vm.Appearance.PropertyChanged += OnAppearanceChanged;
            ApplyAppearance();
            Bind();
            if (VisualRoot is not null)
            {
                vm.Activate();
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiConsoleViewModel.Selected))
        {
            Bind();
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => ApplyAppearance();

    private void ApplyAppearance()
    {
        if (_viewModel?.Appearance is { } appearance)
        {
            Console.SetFont(appearance.FontFamily, appearance.FontSize);
            Console.Palette = appearance.Palette;
            Console.CopyOnSelect = appearance.CopyOnSelect;
        }
    }

    private void Bind()
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabChanged;
        }

        _tab = _viewModel?.Selected;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabChanged;
        }

        ShowSession();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Restart puts another session in the same tab.
        if (e.PropertyName == nameof(AiConsoleTabViewModel.Session))
        {
            ShowSession();
        }
    }

    private void ShowSession()
    {
        // A tab whose CLI is starting again keeps what the last one printed until the new session is there.
        if (_tab is null || _tab.Session is not null)
        {
            Console.Session = _tab?.Session;
        }

        Pane.IsVisible = _tab is not null;
        SyncScroll();
        if (_tab?.Session is not null && (IsKeyboardFocusWithin || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Console.Focus());
        }
    }

    private void SyncScroll()
    {
        ConsoleScroll.Maximum = Console.MaximumTop;
        ConsoleScroll.ViewportSize = Console.Rows;
        ConsoleScroll.Value = Console.ViewportTop;
        ConsoleScroll.IsVisible = Console.MaximumTop > 0;
    }

    /// <summary>The files a drag carries: Solution Explorer's own paths, or files from the OS.</summary>
    internal static IReadOnlyList<string> DroppedPaths(IDataTransfer data) =>
        data.TryGetValue(SolutionExplorerView.s_paths) is { Length: > 0 } paths
            ? paths
            : [.. (data.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _tab is not null && DroppedPaths(e.DataTransfer).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is not { } vm)
        {
            return;
        }

        foreach (var path in DroppedPaths(e.DataTransfer))
        {
            await vm.AddFileAsync(path).ConfigureAwait(true);
        }

        Console.Focus();
    }
}
