using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.WinForms.Utilities.Shared;
using SwfBehavior = System.Windows.Forms.Design.Behavior;

// The replacement of the designer server's behaviors, glyphs and snap lines (#461, ADR 0106). Codalus's shell draws the
// selection and moves and sizes the controls itself, so a vendor's glyphs and behaviors are kept as its designer creates
// them but never drawn or given the mouse; snap lines go to the surface (the host reads a control's baseline from them).

namespace Microsoft.DotNet.DesignTools.Designers.Behaviors;

public enum GlyphSelectionType
{
    NotSelected,
    Selected,
    SelectedPrimary,
}

public enum SnapLineType
{
    Top,
    Bottom,
    Left,
    Right,
    Horizontal,
    Vertical,
    Baseline,
}

public enum SnapLinePriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Always = 4,
}

/// <summary>A line a control snaps to.</summary>
public sealed class SnapLine
{
    public SnapLine(SnapLineType type, int offset)
        : this(type, offset, null, SnapLinePriority.Low)
    {
    }

    public SnapLine(SnapLineType type, int offset, string? filter)
        : this(type, offset, filter, SnapLinePriority.Low)
    {
    }

    public SnapLine(SnapLineType type, int offset, SnapLinePriority priority)
        : this(type, offset, null, priority)
    {
    }

    public SnapLine(SnapLineType type, int offset, string? filter, SnapLinePriority priority)
    {
        SnapLineType = type;
        Offset = offset;
        Filter = filter;
        Priority = priority;
    }

    public string? Filter { get; }

    public bool IsHorizontal => SnapLineType is SnapLineType.Top or SnapLineType.Bottom or SnapLineType.Horizontal or SnapLineType.Baseline;

    public bool IsVertical => SnapLineType is SnapLineType.Left or SnapLineType.Right or SnapLineType.Vertical;

    public int Offset { get; private set; }

    public SnapLinePriority Priority { get; }

    public SnapLineType SnapLineType { get; }

    public void AdjustOffset(int adjustment) => Offset += adjustment;

    internal SwfBehavior.SnapLine ToSurface() => new((SwfBehavior.SnapLineType)(int)SnapLineType, Offset, Filter, (SwfBehavior.SnapLinePriority)(int)Priority);

    internal static SnapLine FromSurface(SwfBehavior.SnapLine line) => new((SnapLineType)(int)line.SnapLineType, line.Offset, line.Filter, (SnapLinePriority)(int)line.Priority);
}

/// <summary>What a glyph does with the mouse. Kept, never called: the shell has the mouse.</summary>
public class Behavior
{
    public Behavior()
    {
    }

    public Behavior(bool callParentBehavior, BehaviorService behaviorService)
    {
        CallParentBehavior = callParentBehavior;
        BehaviorService = behaviorService;
    }

    protected bool CallParentBehavior { get; }

    protected BehaviorService? BehaviorService { get; }

    public virtual Cursor Cursor => Cursors.Default;

    public virtual bool DisableAllCommands => false;

    public virtual bool OnMouseDoubleClick(Glyph? g, MouseButtons button, Point mouseLoc) => false;

    public virtual bool OnMouseDown(Glyph? g, MouseButtons button, Point mouseLoc) => false;

    public virtual bool OnMouseEnter(Glyph? g) => false;

    public virtual bool OnMouseHover(Glyph? g, Point mouseLoc) => false;

    public virtual bool OnMouseLeave(Glyph? g) => false;

    public virtual bool OnMouseMove(Glyph? g, MouseButtons button, Point mouseLoc) => false;

    public virtual bool OnMouseUp(Glyph? g, MouseButtons button, Point mouseLoc) => false;

    public virtual void OnDragDrop(Glyph? g, DragEventArgs e)
    {
    }

    public virtual void OnDragEnter(Glyph? g, DragEventArgs e)
    {
    }

    public virtual void OnDragLeave(Glyph? g, EventArgs e)
    {
    }

    public virtual void OnDragOver(Glyph? g, DragEventArgs e)
    {
    }

    public virtual void OnGiveFeedback(Glyph? g, GiveFeedbackEventArgs e)
    {
    }

    public virtual void OnLoseCapture(Glyph? g, EventArgs e)
    {
    }
}

/// <summary>Something drawn over the surface. Kept, never drawn: the shell draws the selection.</summary>
public class Glyph
{
    private readonly Behavior? _behavior;

    protected Glyph(Behavior? behavior) => _behavior = behavior;

    public virtual Behavior? Behavior => _behavior;

    public virtual Rectangle Bounds => Rectangle.Empty;

    public virtual Cursor? GetHitTest(Point p) => null;

    public virtual void Paint(PaintEventArgs pe)
    {
    }
}

/// <summary>A glyph of a component.</summary>
public class ComponentGlyph : Glyph
{
    public ComponentGlyph(IComponent? relatedComponent)
        : this(relatedComponent, null)
    {
    }

    public ComponentGlyph(IComponent? relatedComponent, Behavior? behavior)
        : base(behavior) => RelatedComponent = relatedComponent;

    public IComponent? RelatedComponent { get; }

    public override Cursor? GetHitTest(Point p) => null;

    public override void Paint(PaintEventArgs pe)
    {
    }
}

/// <summary>A glyph over a control's body.</summary>
public class ControlBodyGlyph : ComponentGlyph
{
    private readonly Rectangle _bounds;
    private readonly Cursor? _hitTestCursor;

    public ControlBodyGlyph(Rectangle bounds, Cursor? cursor, IComponent? relatedComponent, Behavior? behavior)
        : base(relatedComponent, behavior)
    {
        _bounds = bounds;
        _hitTestCursor = cursor;
    }

    public override Rectangle Bounds => _bounds;

    public override Cursor? GetHitTest(Point p) => _bounds.Contains(p) ? _hitTestCursor : null;
}

public class GlyphCollection : NonNullCollection<Glyph>
{
    public GlyphCollection()
    {
    }

    public GlyphCollection(IEnumerable<Glyph> glyphs)
        : base(glyphs)
    {
    }
}

/// <summary>A layer of glyphs.</summary>
public sealed class Adorner
{
    public Adorner() => Glyphs = [];

    public BehaviorService? BehaviorService { get; set; }

    public bool Enabled { get; set; } = true;

    public GlyphCollection Glyphs { get; }

    public void Invalidate()
    {
    }

    public void Invalidate(Rectangle rectangle)
    {
    }

    public void Invalidate(Region region)
    {
    }
}

public sealed class BehaviorServiceAdornerCollection : NonNullCollection<Adorner>
{
}

public class BehaviorDragDropEventArgs : EventArgs
{
    public BehaviorDragDropEventArgs(ICollection dragComponents) => DragComponents = dragComponents;

    public ICollection DragComponents { get; }
}

public delegate void BehaviorDragDropEventHandler(object? sender, BehaviorDragDropEventArgs e);

/// <summary>
/// The surface's behaviors and adorners, as a vendor's designer adds to them: adorners and behaviors are kept, nothing
/// is drawn, and the coordinates are Windows Forms' service's (the hidden surface's window).
/// </summary>
public sealed class BehaviorService : DisposableObject
{
    private readonly SwfBehavior.BehaviorService? _inner;
    private readonly Stack<Behavior> _behaviors = new();
    private Bitmap? _graphicsBitmap;

    internal BehaviorService(SwfBehavior.BehaviorService? inner) => _inner = inner;

    public BehaviorServiceAdornerCollection Adorners { get; } = [];

    public Graphics AdornerWindowGraphics
    {
        get
        {
            if (_inner is not null)
            {
                return _inner.AdornerWindowGraphics;
            }

            _graphicsBitmap ??= new Bitmap(1, 1);
            return Graphics.FromImage(_graphicsBitmap);
        }
    }

    public Behavior? CurrentBehavior => _behaviors.Count > 0 ? _behaviors.Peek() : null;

    public event BehaviorDragDropEventHandler? BeginDrag;

    public event BehaviorDragDropEventHandler? EndDrag;

    public event EventHandler? Synchronize;

    public Point AdornerWindowPointToScreen(Point p) => _inner?.AdornerWindowPointToScreen(p) ?? p;

    public Point AdornerWindowToScreen() => _inner?.AdornerWindowToScreen() ?? Point.Empty;

    public Point AdornerWindowToScreen(Point p) => AdornerWindowPointToScreen(p);

    public Point ControlToAdornerWindow(Control c) => _inner?.ControlToAdornerWindow(c) ?? Point.Empty;

    public Rectangle ControlRectInAdornerWindow(Control c) => _inner?.ControlRectInAdornerWindow(c) ?? (c is null ? Rectangle.Empty : c.Bounds);

    public Point PointToAdornerWindow(IntPtr handle, Point p) => _inner?.MapAdornerWindowPoint(handle, p) ?? p;

    public Point ScreenToAdornerWindow(Point p) => _inner?.ScreenToAdornerWindow(p) ?? p;

    public void Invalidate()
    {
    }

    public void Invalidate(Rectangle rect)
    {
    }

    public void Invalidate(Region r)
    {
    }

    public void PushBehavior(Behavior behavior) => _behaviors.Push(behavior);

    public void PushCaptureBehavior(Behavior behavior) => _behaviors.Push(behavior);

    public bool PopBehavior(Behavior behavior)
    {
        if (_behaviors.Count == 0 || !ReferenceEquals(_behaviors.Peek(), behavior))
        {
            return false;
        }

        _behaviors.Pop();
        return true;
    }

    public void SyncSelection() => Synchronize?.Invoke(this, EventArgs.Empty);

    internal void OnBeginDrag(BehaviorDragDropEventArgs e) => BeginDrag?.Invoke(this, e);

    internal void OnEndDrag(BehaviorDragDropEventArgs e) => EndDrag?.Invoke(this, e);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _graphicsBitmap?.Dispose();
            _graphicsBitmap = null;
        }

        base.Dispose(disposing);
    }
}
