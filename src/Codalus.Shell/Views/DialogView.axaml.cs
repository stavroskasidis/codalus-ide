using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The in-window modal dialog: Enter chooses the default button, Escape cancels, Tab stays inside. A preview of
/// changes takes most of the window (<see cref="PreviewSize"/>, M34h); other forms size to their content.
/// </summary>
public sealed partial class DialogView : UserControl
{
    /// <summary>The share of the window a preview of changes takes.</summary>
    public const double PreviewShare = 0.75;

    /// <summary>What a preview leaves of the window on each side, at the least.</summary>
    public const double PreviewMargin = 16;

    /// <summary>A preview's smallest size where the window has room (a document's minimum width, ui-guidelines.md).</summary>
    public static readonly Size PreviewMinSize = new(640, 400);

    private DialogViewModel? _viewModel;

    public DialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Opened -= OnOpened;
            _viewModel.PropertyChanged -= OnPropertyChanged;
        }

        _viewModel = DataContext as DialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
            _viewModel.PropertyChanged += OnPropertyChanged;
        }
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DialogViewModel.HasChanges) or nameof(DialogViewModel.HasDetails))
        {
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// The size of a preview of changes over an area of <paramref name="area"/> (the window's content): three quarters
    /// of it, at least <see cref="PreviewMinSize"/>, at most the area less <see cref="PreviewMargin"/> on each side.
    /// </summary>
    public static Size PreviewSize(Size area)
    {
        static double Fit(double available, double minimum)
        {
            var most = Math.Max(0, available - (2 * PreviewMargin));
            return Math.Min(most, Math.Max(available * PreviewShare, minimum));
        }

        return new Size(Fit(area.Width, PreviewMinSize.Width), Fit(area.Height, PreviewMinSize.Height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The layer gives the dialog the window's size to center in.
        if (_viewModel is { HasChanges: true } && double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height))
        {
            var size = PreviewSize(availableSize);
            Frame.MaxWidth = double.PositiveInfinity;
            Frame.Width = Math.Max(size.Width, Frame.MinWidth);
            Frame.Height = size.Height;
            ChangesBox.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            Frame.Width = double.NaN;
            Frame.Height = double.NaN;
            Frame.MaxWidth = _viewModel?.HasDetails == true ? 960 : 560;
            ChangesBox.MaxHeight = 420;
        }

        return base.MeasureOverride(availableSize);
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
        }
    }
}
