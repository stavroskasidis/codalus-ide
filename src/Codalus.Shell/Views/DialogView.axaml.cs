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
/// grows with its content up to 720 (960 with notes); a preview of changes takes most of the window instead
/// (<see cref="PreviewSize"/>, M34h's gallery fixes), its changes filling what the header, the message and the footer
/// leave.
/// </summary>
public sealed partial class DialogView : UserControl
{
    private const double Width520 = 520;
    private const double WindowMargin = 32;

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
        BrowseButton.Click += (_, _) => _ = BrowseAsync();
    }

    /// <summary>Browse beside a path's input: the OS's picker where the typed path is; the choice replaces the input.</summary>
    private async Task BrowseAsync()
    {
        if (_viewModel is not { Request: { Browse: { } kind } request } vm)
        {
            return;
        }

        var start = Controls.PathBox.StartFolder(vm.InputText, request.BrowseFolder);
        var picked = vm.Picker is { } picker
            ? await picker(kind, start).ConfigureAwait(true)
            : await Controls.PathBox.PickAsync(this, kind, start).ConfigureAwait(true);
        if (picked is null || !ReferenceEquals(vm.Request, request))
        {
            return;
        }

        vm.InputText = Controls.PathBox.Relative(picked, request.BrowseFolder);
        Input.Focus();
        Input.CaretIndex = Input.Text?.Length ?? 0;
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
            Set(Frame, MinWidthProperty, 0);
            Set(Frame, MaxWidthProperty, double.PositiveInfinity);
            Set(Frame, WidthProperty, size.Width);
            Set(Frame, HeightProperty, size.Height);
            Set(ChangesBox, MaxHeightProperty, double.PositiveInfinity);
        }
        else
        {
            var minimum = double.IsInfinity(availableSize.Width) ? Width520 : Math.Min(Width520, Math.Max(0, availableSize.Width - WindowMargin));
            Set(Frame, MinWidthProperty, minimum);
            Set(Frame, MaxWidthProperty, _viewModel?.HasDetails == true ? 960 : 720);
            Set(Frame, WidthProperty, double.NaN);
            Set(Frame, HeightProperty, double.NaN);
            Set(ChangesBox, MaxHeightProperty, 420);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>Sets a size only when it changes, so measuring doesn't invalidate itself again.</summary>
    private static void Set(Control control, Avalonia.StyledProperty<double> property, double value)
    {
        if (!control.GetValue(property).Equals(value))
        {
            control.SetValue(property, value);
        }
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
