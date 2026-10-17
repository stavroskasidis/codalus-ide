using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Views;

/// <summary>
/// The Windows Forms designer's view (M41a, ADR 0073): the designer host's frame of the form, and over it the surface's
/// own adorners and gestures: selection (a click; Ctrl or Shift to add; a rubber band), moving (a drag, into another
/// container when dropped in one), resizing (the primary selection's eight grab handles), the arrow keys (Shift to
/// resize), Delete, Ctrl+C, Ctrl+V and Ctrl+D, Ctrl+Z and Ctrl+Y (the designer file's undo), a double-click (the default
/// event's handler), and the Toolbox (a click or a drag with an item armed, or an item dragged in).
/// </summary>
public sealed class WinFormsDesignerView : UserControl, IPaneView
{
    /// <summary>A Toolbox item dragged onto the surface: its type's full name.</summary>
    public static readonly DataFormat<string> ToolboxFormat = DataFormat.CreateInProcessFormat<string>("codalus.toolbox.type");

    private readonly WinFormsDesignerDocument _document;
    private readonly DesignerSurface _surface;
    private readonly ScrollViewer _scroll;

    public WinFormsDesignerView(WinFormsDesignerDocument document)
    {
        _document = document;
        DataContext = document;
        _surface = new DesignerSurface(document);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(16), Child = _surface, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
        };
        _scroll.Bind(BackgroundProperty, _scroll.GetResourceObservable("Shell.Editor.Background"));

        Button Tool(string icon, string name, System.Windows.Input.ICommand command)
        {
            var button = new Button { Content = new Icon { Kind = icon }, Classes = { "toolbar" }, Command = command };
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            return button;
        }

        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "muted" }, Margin = new Thickness(8, 0) };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        var busy = new ProgressBar { IsIndeterminate = true, Width = 60, Height = 2, MinHeight = 2, VerticalAlignment = VerticalAlignment.Center };
        var toolbar = new DockPanel
        {
            Margin = new Thickness(6, 4),
            Children =
            {
                Tool("code", Localizer.Get("msg.winFormsDesigner.viewCode", "View Code (F7)"), document.ViewCodeCommand),
                Tool("refresh", Localizer.Get("msg.winFormsDesigner.reload", "Load the form again (and restart the designer)"), document.RefreshCommand),
                busy,
                status,
            },
        };
        foreach (var child in toolbar.Children.Take(3))
        {
            DockPanel.SetDock((Control)child, Avalonia.Controls.Dock.Left);
        }

        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);

        // The statements the designer couldn't run, in the error color; a long list scrolls (M34g: it was cut at 120 px).
        var problems = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4), Classes = { "error" } };
        Avalonia.Automation.AutomationProperties.SetName(problems, Localizer.Get("msg.winFormsDesigner.problems", "What the designer couldn't run"));
        var problemBar = new Border { Child = new ScrollViewer { Content = problems, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderThickness = new Thickness(0, 1, 0, 0) };
        problemBar.Bind(Border.BorderBrushProperty, problemBar.GetResourceObservable("Shell.Border"));
        DockPanel.SetDock(problemBar, Avalonia.Controls.Dock.Bottom);

        // The component tray (simple: M41b has the full one): non-visual components, to select them.
        var tray = new ItemsControl
        {
            ItemsSource = document.Tray,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DesignerTrayItem>((item, _) =>
            {
                var button = new Button { Content = item?.Name, Command = item?.Select, Classes = { "toolbar" }, Margin = new Thickness(2) };
                ToolTip.SetTip(button, item?.Type);
                Avalonia.Automation.AutomationProperties.SetName(button, item?.Name);
                return button;
            }),
            Margin = new Thickness(6, 2),
        };
        var trayBar = new Border { Child = tray, BorderThickness = new Thickness(0, 1, 0, 0) };
        trayBar.Bind(Border.BorderBrushProperty, trayBar.GetResourceObservable("Shell.Border"));
        DockPanel.SetDock(trayBar, Avalonia.Controls.Dock.Bottom);

        // Before the first frame (opening, reading the form, starting the designer) and when it fails: the status where
        // the form will be, wrapped (M34g: a failure's reason was one trimmed line in the toolbar).
        var message = new TextBlock { Classes = { "emptyState" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(message, Avalonia.Automation.AutomationLiveSetting.Polite);
        var surfaceArea = new Panel { Children = { _scroll, message } };

        Content = new DockPanel { Children = { toolbar, problemBar, trayBar, surfaceArea } };

        // The document's state, without reflection bindings (the shell is Native AOT).
        void Update()
        {
            status.Text = document.Status;
            ToolTip.SetTip(status, string.IsNullOrEmpty(document.Status) ? null : document.Status);
            message.Text = document.Status;
            message.IsVisible = document.Frame is null && !string.IsNullOrEmpty(document.Status);
            busy.IsVisible = document.IsBusy;
            problems.Text = document.Problems;
            problemBar.IsVisible = document.HasProblems;
            trayBar.IsVisible = document.HasTray;
        }

        Update();
        document.PropertyChanged += (_, _) => Infrastructure.Ui.Post(Update);
        document.SurfaceChanged += (_, _) => Infrastructure.Ui.Post(Update);
        Avalonia.Automation.AutomationProperties.SetName(_surface, document.Title);
    }

    public DesignerSurface Surface => _surface;

    public void FocusPrimary() => _surface.Focus();
}

/// <summary>The frame and the adorners over it, and the pointer and keyboard gestures (frame coordinates: one unit a pixel of the form).</summary>
public sealed class DesignerSurface : Control
{
    private const double Handle = 6;
    private readonly WinFormsDesignerDocument _document;
    private Point? _pressed;
    private Mode _mode;
    private int _handle = -1;
    private Point _current;
    private string? _bandContainer;
    private bool _moved;

    private enum Mode
    {
        None,
        Move,
        Resize,
        Band,
        Draw,
    }

    public DesignerSurface(WinFormsDesignerDocument document)
    {
        _document = document;
        Focusable = true;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        document.SurfaceChanged += (_, _) => Infrastructure.Ui.Post(() =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(WinFormsDesignerView.ToolboxFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(WinFormsDesignerView.ToolboxFormat) is { Length: > 0 } type)
            {
                var at = e.GetPosition(this);
                e.Handled = true;
                _ = _document.CreateAsync(type, at.X, at.Y);
            }
        });
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, _document.FrameWidth), Math.Max(1, _document.FrameHeight));

    public override void Render(DrawingContext context)
    {
        if (_document.Frame is { } frame)
        {
            context.DrawImage(frame, new Rect(0, 0, _document.FrameWidth, _document.FrameHeight));
        }
        else
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        }

        // The adorners in the theme's tokens (M34g: the handles were white and the rubber band DodgerBlue in every theme).
        var accent = Token("Shell.Accent") ?? Token("Shell.Focus") ?? Brushes.Gray;
        var handleFill = Token("Shell.Panel") ?? Brushes.Transparent;
        var thin = new Pen(accent, 1, new DashStyle([2, 2], 0));
        var solid = new Pen(accent, 1);
        foreach (var name in _document.Selection)
        {
            if (_document.Find(name) is not { IsControl: true } view)
            {
                continue;
            }

            var rect = Rect(WinFormsDesignerDocument.Bounds(view));
            if (_mode == Mode.Move && _moved && view != _document.Root)
            {
                rect = rect.Translate(_current - _pressed!.Value);
            }

            if (_mode == Mode.Resize && name == _document.PrimarySelection)
            {
                rect = Rect(Resized(WinFormsDesignerDocument.Bounds(view)));
            }

            context.DrawRectangle(null, name == _document.PrimarySelection ? solid : thin, rect.Inflate(1));
            if (name == _document.PrimarySelection)
            {
                foreach (var handle in Handles(rect))
                {
                    context.DrawRectangle(handleFill, solid, handle);
                }
            }
        }

        if (_mode is Mode.Band or Mode.Draw && _pressed is { } start)
        {
            var band = accent is ISolidColorBrush solidAccent ? new SolidColorBrush(solidAccent.Color, 0.12) : null;
            context.DrawRectangle(band, thin, new Rect(start, _current).Normalize());
        }
    }

    private IBrush? Token(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;

    private static Rect Rect(DesignerRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The eight grab handles: corners and edge middles, clockwise from the top left.</summary>
    private static Rect[] Handles(Rect rect)
    {
        double[] xs = [rect.Left, rect.Center.X, rect.Right];
        double[] ys = [rect.Top, rect.Center.Y, rect.Bottom];
        (int X, int Y)[] at = [(0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2), (0, 1)];
        return [.. at.Select(p => new Rect(xs[p.X] - (Handle / 2), ys[p.Y] - (Handle / 2), Handle, Handle))];
    }

    private DesignerRect Resized(DesignerRect start)
    {
        if (_pressed is not { } pressed)
        {
            return start;
        }

        var dx = _current.X - pressed.X;
        var dy = _current.Y - pressed.Y;
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        switch (_handle)
        {
            case 0: left += dx; top += dy; break;
            case 1: top += dy; break;
            case 2: right += dx; top += dy; break;
            case 3: right += dx; break;
            case 4: right += dx; bottom += dy; break;
            case 5: bottom += dy; break;
            case 6: left += dx; bottom += dy; break;
            case 7: left += dx; break;
        }

        return DesignerRect.Between(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    private int HandleAt(Point point)
    {
        if (_document.PrimarySelection is not { } primary || _document.Find(primary) is not { IsControl: true, IsPart: false } view)
        {
            return -1;
        }

        var handles = Handles(Rect(WinFormsDesignerDocument.Bounds(view)));
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i].Inflate(2).Contains(point))
            {
                // The root resizes from its right, bottom and bottom-right handles only (its location is the designer's).
                return view == _document.Root && i is not (3 or 4 or 5) ? -1 : i;
            }
        }

        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _mode = Mode.None;
            _pressed = null;
            _ = _document.DefaultEventAsync(point.X, point.Y);
            e.Handled = true;
            return;
        }

        _pressed = point;
        _current = point;
        _moved = false;
        if (Toolbox.Armed is not null)
        {
            _mode = Mode.Draw;
        }
        else if ((_handle = HandleAt(point)) >= 0)
        {
            _mode = Mode.Resize;
        }
        else
        {
            var toggle = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0;
            var hit = _document.HitTest(point.X, point.Y);
            if (hit is null || hit == _document.Root || (hit.IsContainer && !_document.Selection.Contains(hit.Name) && !toggle && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
            {
                _mode = Mode.Band;
                _bandContainer = hit?.Name ?? _document.Root?.Name;
                if (!toggle)
                {
                    _document.Click(point.X, point.Y, toggle: false);
                }
            }
            else
            {
                _document.Click(point.X, point.Y, toggle);
                _mode = Mode.Move;
            }
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_pressed is { } pressed)
        {
            _current = point;
            _moved |= Math.Abs(point.X - pressed.X) >= 3 || Math.Abs(point.Y - pressed.Y) >= 3;
            InvalidateVisual();
            return;
        }

        var handle = HandleAt(point);
        Cursor = new Cursor(handle switch
        {
            0 or 4 => StandardCursorType.TopLeftCorner,
            2 or 6 => StandardCursorType.TopRightCorner,
            1 or 5 => StandardCursorType.SizeNorthSouth,
            3 or 7 => StandardCursorType.SizeWestEast,
            _ => Toolbox.Armed is not null ? StandardCursorType.Cross : StandardCursorType.Arrow,
        });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is not { } pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var mode = _mode;
        _pressed = null;
        _mode = Mode.None;
        e.Pointer.Capture(null);
        e.Handled = true;
        switch (mode)
        {
            case Mode.Draw when Toolbox.Armed is { } item:
                if (_moved)
                {
                    var rect = DesignerRect.Between(pressed.X, pressed.Y, point.X, point.Y);
                    _ = _document.CreateAsync(item.Type, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    _ = _document.CreateAsync(item.Type, point.X, point.Y);
                }

                break;
            case Mode.Move when _moved:
                _ = _document.MoveSelectionAsync(point.X - pressed.X, point.Y - pressed.Y, point.X, point.Y);
                break;
            case Mode.Resize when _moved && _document.PrimarySelection is { } primary && _document.Find(primary) is { } view:
                _current = point;
                _pressed = pressed;
                var bounds = Resized(WinFormsDesignerDocument.Bounds(view));
                _pressed = null;
                _ = _document.ResizeAsync(primary, bounds);
                break;
            case Mode.Band when _moved && _bandContainer is { } container:
                _document.SelectInRectangle(DesignerRect.Between(pressed.X, pressed.Y, point.X, point.Y), container, add: (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var primary = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                _ = _document.NudgeAsync(dx, dy, resize: shift);
                break;
            case Key.Delete:
                _ = _document.DeleteSelectionAsync();
                break;
            case Key.Escape:
                if (Toolbox.Armed is not null)
                {
                    Toolbox.Disarm();
                }
                else if (_document.PrimarySelection is { } selected && _document.Find(selected)?.Parent is { } parent)
                {
                    _document.SetSelection([parent], notify: true);
                }

                break;
            case Key.C when primary:
                _document.Copy();
                break;
            case Key.X when primary:
                _document.Copy();
                _ = _document.DeleteSelectionAsync();
                break;
            case Key.V when primary:
                _ = _document.PasteAsync();
                break;
            case Key.D when primary:
                _ = _document.DuplicateAsync();
                break;
            case Key.Z when primary && !shift:
                _document.Undo();
                break;
            case Key.Y when primary:
            case Key.Z when primary && shift:
                _document.Redo();
                break;
            case Key.A when primary:
                if (_document.Root is { } root)
                {
                    _document.SetSelection([.. _document.ChildrenOf(root.Name).Select(c => c.Name)], notify: true);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private ToolboxViewModel Toolbox => _document.Toolbox;
}
