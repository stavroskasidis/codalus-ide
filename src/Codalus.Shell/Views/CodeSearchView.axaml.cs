using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Code Search's window (M47). The keyboard: the box keeps the focus while Up and Down move in the results; Enter opens
/// the result (Ctrl+Enter keeps the window open), Esc closes it; Alt and a chip's letter picks the chip; Tab goes from
/// the box to the chips, the scope, the results and the preview, whose editor is a real one (Ctrl+S saves it). Floating,
/// it is dragged by its tabs' row and resized from its corner; the same view fills the tool window when docked
/// (<see cref="IsToolWindow"/>). Only the view that is showing hosts the preview's editor.
/// </summary>
public sealed partial class CodeSearchView : UserControl, Controls.IPaneView
{
    private readonly TranslateTransform _offset = new();
    private CodeSearchViewModel? _viewModel;
    private Point? _dragStart;
    private Vector _offsetAtDragStart;

    public CodeSearchView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        List.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            {
                _ = _viewModel?.AcceptAsync();
            }
        };
        TitleRow.PointerPressed += OnTitlePressed;
        TitleRow.PointerMoved += OnTitleMoved;
        TitleRow.PointerReleased += (_, e) =>
        {
            _dragStart = null;
            e.Pointer.Capture(null);
        };
        ResizeGrip.DragDelta += (_, e) =>
        {
            if (_viewModel is { } vm && !IsToolWindow)
            {
                vm.Width += e.Vector.X;
                vm.Height += e.Vector.Y;
            }
        };
    }

    /// <summary>The docked instance, in the Code Search tool window (no drag, no resize grip, no fixed size).</summary>
    public bool IsToolWindow { get; init; }

    /// <summary>The preview's host (tests).</summary>
    public ContentControl Preview => PreviewHost;

    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    /// <summary>The tool window took the focus (F6, its tab): the box takes it.</summary>
    public void FocusPrimary() => FocusInput();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.PreviewChanged -= OnPreviewChanged;
            _viewModel.FocusRequested -= OnFocusRequested;
        }

        _viewModel = DataContext as CodeSearchViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.PreviewChanged += OnPreviewChanged;
            _viewModel.FocusRequested += OnFocusRequested;
            ApplyGeometry();
            ApplyPreviewRows();
            OnPreviewChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsToolWindow)
        {
            RenderTransform = _offset;
        }
        else
        {
            ResizeGrip.IsVisible = false;
            DockButton.IsVisible = true;
            CloseButton.IsVisible = false;
            Frame.Classes.Remove("overlay");
            Frame.BorderThickness = new Thickness(0);
        }

        ApplyGeometry();
        OnPreviewChanged(this, EventArgs.Empty);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CodeSearchViewModel.SelectedIndex) when _viewModel is { SelectedIndex: >= 0 and var index }:
                List.ScrollIntoView(index);
                break;
            case nameof(CodeSearchViewModel.Width) or nameof(CodeSearchViewModel.Height) or nameof(CodeSearchViewModel.OffsetX) or nameof(CodeSearchViewModel.OffsetY):
                ApplyGeometry();
                break;
            case nameof(CodeSearchViewModel.HasPreview):
                ApplyPreviewRows();
                break;
        }
    }

    private void ApplyGeometry()
    {
        if (_viewModel is not { } vm || IsToolWindow)
        {
            Frame.Width = double.NaN;
            Frame.Height = double.NaN;
            return;
        }

        Frame.Width = vm.Width;
        Frame.Height = vm.Height;
        _offset.X = vm.OffsetX;
        _offset.Y = vm.OffsetY;
    }

    /// <summary>The preview takes the lower part of the body, below the splitter, or nothing when hidden.</summary>
    private void ApplyPreviewRows()
    {
        var shown = _viewModel?.HasPreview == true;
        Body.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        Body.RowDefinitions[1].Height = shown ? GridLength.Auto : new GridLength(0);
        Body.RowDefinitions[2].Height = shown ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>The preview's editor goes into the view that shows: the floating window's, or the tool window's when docked.</summary>
    private void OnPreviewChanged(object? sender, EventArgs e)
    {
        var mine = _viewModel is { } vm && vm.IsDocked == IsToolWindow && this.IsAttachedToVisualTree() ? vm.PreviewContent as Control : null;
        if (ReferenceEquals(PreviewHost.Content, mine))
        {
            return;
        }

        if (mine?.Parent is ContentControl other && other != PreviewHost)
        {
            other.Content = null;
        }

        PreviewHost.Content = mine;
    }

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        if (_viewModel is { } vm && vm.IsDocked == IsToolWindow)
        {
            Dispatcher.UIThread.Post(FocusInput, DispatcherPriority.Loaded);
        }
    }

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsToolWindow || !e.GetCurrentPoint(TitleRow).Properties.IsLeftButtonPressed || Parent is not Visual parent
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null
            || (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || (e.Source as Visual)?.FindAncestorOfType<TabStripItem>(includeSelf: true) is not null)
        {
            return;
        }

        _dragStart = e.GetPosition(parent);
        _offsetAtDragStart = new Vector(_offset.X, _offset.Y);
        e.Pointer.Capture(TitleRow);
        e.Handled = true;
    }

    private void OnTitleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || Parent is not Visual parent || _viewModel is not { } vm)
        {
            return;
        }

        // The title row stays inside the main window, so the window can always be dragged back.
        var offset = _offsetAtDragStart + (e.GetPosition(parent) - start);
        var origin = Bounds.Position - new Point(_offset.X, _offset.Y);
        vm.OffsetX = Math.Clamp(offset.X, -origin.X, Math.Max(-origin.X, parent.Bounds.Width - origin.X - 120));
        vm.OffsetY = Math.Clamp(offset.Y, -origin.Y, Math.Max(-origin.Y, parent.Bounds.Height - origin.Y - 40));
    }

    /// <summary>Whether the focus is in the preview's editor (keys there are the editor's, except Esc and Ctrl+S).</summary>
    private bool InPreview(object? source) => source is Visual visual && PreviewHost.IsVisualAncestorOf(visual);

    /// <summary>
    /// The keys this window takes before the main window's key bindings (<see cref="MainWindow"/> asks): Esc, Enter,
    /// the arrows outside the preview, Alt and a chip's letter, and Ctrl+S in the preview.
    /// </summary>
    public bool TakesKey(KeyEventArgs e)
    {
        var inPreview = InPreview(e.Source);
        return e.Key switch
        {
            Key.Escape => true,
            Key.S when e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta => inPreview,
            Key.Enter or Key.Up or Key.Down or Key.PageUp or Key.PageDown => !inPreview && e.Source is not ComboBox,
            Key.A or Key.F or Key.T or Key.M or Key.X when e.KeyModifiers == KeyModifiers.Alt => true,
            _ => false,
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm || !TakesKey(e))
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: true })
        {
            return; // the scope's drop-down takes its own keys
        }

        switch (e.Key)
        {
            case Key.Escape when InPreview(e.Source) && (e.Source as Visual)?.FindAncestorOfType<TextEditor>() is { } editor && !editor.TextArea.Selection.IsEmpty:
                editor.TextArea.ClearSelection();
                break;
            case Key.Escape:
                vm.Close();
                break;
            case Key.S:
                _ = vm.SavePreviewAsync();
                break;
            case Key.Down:
                vm.Move(1);
                break;
            case Key.Up:
                vm.Move(-1);
                break;
            case Key.PageDown:
                vm.Move(10);
                break;
            case Key.PageUp:
                vm.Move(-10);
                break;
            case Key.Enter:
                _ = vm.AcceptAsync(keepOpen: e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
                break;
            default:
                vm.SelectChipByKey(e.Key.ToString()[0]);
                break;
        }

        e.Handled = true;
    }
}
