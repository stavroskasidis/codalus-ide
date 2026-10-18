using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The in-window modal dialog (M34h's frame, docs/design/controls.md): Enter chooses the default button, Escape (or the
/// header's close button) cancels, Tab stays inside; on a checklist Space checks the selected item and Alt+Up and
/// Alt+Down move it. It is 520 px wide (Shell.Size.Dialog.Width), narrower only in a window too small for that, and
/// grows with its content up to 720 (960 with a preview of changes).
/// </summary>
public sealed partial class DialogView : UserControl
{
    private const double Width520 = 520;
    private const double WindowMargin = 32;

    private DialogViewModel? _viewModel;

    public DialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var minimum = double.IsInfinity(availableSize.Width) ? Width520 : Math.Min(Width520, Math.Max(0, availableSize.Width - WindowMargin));
        if (!Frame.MinWidth.Equals(minimum))
        {
            Frame.MinWidth = minimum;
        }

        return base.MeasureOverride(availableSize);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Opened -= OnOpened;
        }

        _viewModel = DataContext as DialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is { HasInput: true } vm)
        {
            Input.Focus();
            var (start, length) = vm.Request?.InputSelection ?? (0, Input.Text?.Length ?? 0);
            Input.SelectionStart = start;
            Input.SelectionEnd = start + length;
        }
        else
        {
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent"))?.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsOpen: true } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.Cancel();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                vm.Accept();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when vm.Reorderable && vm.HasChoices && e.KeyModifiers == KeyModifiers.Alt:
                vm.MoveChoice(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Space when vm.HasChoices && e.Source is ListBoxItem:
                vm.ToggleSelectedChoice();
                e.Handled = true;
                break;
        }
    }
}
