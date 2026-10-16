using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Find and Replace window: Enter runs Find All (Replace All on the Replace tab), Escape closes it, and dragging
/// the title moves it within the main window.
/// </summary>
public sealed partial class FindReplaceView : UserControl
{
    private readonly TranslateTransform _offset = new();
    private FindReplaceViewModel? _viewModel;
    private Point? _dragStart;
    private Vector _offsetAtDragStart;

    public FindReplaceView()
    {
        InitializeComponent();
        RenderTransform = _offset;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        TitleRow.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(TitleRow).Properties.IsLeftButtonPressed && Parent is Visual parent)
            {
                _dragStart = e.GetPosition(parent);
                _offsetAtDragStart = new Vector(_offset.X, _offset.Y);
                e.Pointer.Capture(TitleRow);
                e.Handled = true;
            }
        };
        TitleRow.PointerMoved += (_, e) =>
        {
            if (_dragStart is { } start && Parent is Visual parent)
            {
                Move(_offsetAtDragStart + (e.GetPosition(parent) - start));
            }
        };
        TitleRow.PointerReleased += (_, e) =>
        {
            _dragStart = null;
            e.Pointer.Capture(null);
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is { } old)
            {
                old.FocusRequested -= OnFocusRequested;
            }

            _viewModel = DataContext as FindReplaceViewModel;
            if (_viewModel is { } vm)
            {
                vm.FocusRequested += OnFocusRequested;
            }
        };
    }

    /// <summary>Keeps the title row inside the main window, so the window can always be dragged back.</summary>
    private void Move(Vector offset)
    {
        if (Parent is Visual parent)
        {
            var origin = Bounds.Position;
            var x = Math.Clamp(offset.X, -origin.X, Math.Max(-origin.X, parent.Bounds.Width - origin.X - 80));
            var y = Math.Clamp(offset.Y, -origin.Y, Math.Max(-origin.Y, parent.Bounds.Height - origin.Y - 32));
            _offset.X = x;
            _offset.Y = y;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        // An open drop-down (the history, Look in, File types) takes Escape and Enter itself.
        if ((e.Source as Visual)?.FindAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: true })
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            _ = vm.IsReplace ? vm.ReplaceAllAsync() : vm.FindAllAsync();
            e.Handled = true;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            QueryBox.Focus();
            if (QueryBox.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
            {
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);
}
