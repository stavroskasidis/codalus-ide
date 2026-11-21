using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using Codalus.WinForms.DesignTools;
using Microsoft.DotNet.DesignTools.Designers.Actions;
using Microsoft.DotNet.DesignTools.Designers.Behaviors;
using Microsoft.WinForms.Utilities.Shared;
using DesignerActionListCollection = Microsoft.DotNet.DesignTools.Designers.Actions.DesignerActionListCollection;

// The replacement of the designer server's designers (#461, ADR 0106). A vendor's designer derives from these; the
// surface never sees it directly: Codalus's adapter (Adapters.cs), a Windows Forms designer of the same kind, creates it
// and forwards to it, and each default here does what Windows Forms' designer does by calling back into the adapter's
// base class. Without an adapter (a designer a vendor's code creates on its own) the defaults do nothing.

namespace Microsoft.DotNet.DesignTools.Designers;

/// <summary>What a control's selection allows (the values of Windows Forms' <c>SelectionRules</c>).</summary>
[Flags]
public enum SelectionRules
{
    None = 0,
    TopSizeable = 1,
    BottomSizeable = 2,
    LeftSizeable = 4,
    RightSizeable = 8,
    AllSizeable = 15,
    Moveable = 0x10000000,
    Visible = 0x40000000,
    Locked = unchecked((int)0x80000000),
}

/// <summary>A component's designer.</summary>
public class ComponentDesigner : DisposableObject, ITreeDesigner, IDesignerFilter, IComponentInitializer
{
    private IComponent? _component;

    /// <summary>The Windows Forms designer that gives this one to the surface, or null.</summary>
    internal IVendorAdapter? Adapter { get; private set; }

    public virtual IComponent Component => _component ?? throw new InvalidOperationException("The designer isn't initialized.");

    public virtual DesignerActionListCollection ActionLists => [];

    public virtual IReadOnlyCollection<IComponent> AssociatedComponents => Adapter?.BaseAssociatedComponents ?? [];

    public IDesignerHost Host => GetService<IDesignerHost>() ?? throw new InvalidOperationException("The designer has no designer host.");

    public bool IsInherited => InheritanceAttribute is { InheritanceLevel: not InheritanceLevel.NotInherited };

    public virtual DesignerVerbCollection Verbs => Adapter?.BaseVerbs ?? [];

    protected internal virtual InheritanceAttribute InheritanceAttribute => Adapter?.BaseInheritanceAttribute ?? InheritanceAttribute.NotInherited;

    protected internal virtual IComponent? ParentComponent => Adapter?.BaseParentComponent;

    /// <summary>Called by the adapter before Windows Forms' base designer initializes, so the vendor's overrides it calls see the component.</summary>
    internal void Attach(IVendorAdapter adapter, IComponent component)
    {
        Adapter = adapter;
        _component = component;
    }

    internal void Detach() => Adapter = null;

    public virtual void Initialize(IComponent component) => _component = component;

    public virtual void InitializeNewComponent(IDictionary? defaultValues) => Adapter?.BaseInitializeNewComponent(defaultValues);

    public virtual void InitializeExistingComponent(IDictionary? defaultValues) => Adapter?.BaseInitializeExistingComponent(defaultValues);

    public virtual void DoDefaultAction() => Adapter?.BaseDoDefaultAction();

    public T? GetService<T>() => (T?)GetService(typeof(T));

    public T GetRequiredService<T>() => GetService<T>() ?? throw new InvalidOperationException($"The service {typeof(T).FullName} isn't available.");

    /// <summary>The service from the component's site, as a vendor's designer sees it: the replacement's services, and a designer host that gives its designers, not the surface's adapters.</summary>
    protected internal virtual object? GetService(Type serviceType) => VendorServices.Get(_component, serviceType, () => Adapter?.BaseGetService(serviceType) ?? _component?.Site?.GetService(serviceType));

    protected internal virtual void PreFilterAttributes(IDictionary attributes) => Adapter?.BasePreFilterAttributes(attributes);

    protected internal virtual void PreFilterEvents(IDictionary events) => Adapter?.BasePreFilterEvents(events);

    protected internal virtual void PreFilterProperties(IDictionary properties) => Adapter?.BasePreFilterProperties(properties);

    protected internal virtual void PostFilterAttributes(IDictionary attributes) => Adapter?.BasePostFilterAttributes(attributes);

    protected internal virtual void PostFilterEvents(IDictionary events) => Adapter?.BasePostFilterEvents(events);

    protected internal virtual void PostFilterProperties(IDictionary properties) => Adapter?.BasePostFilterProperties(properties);

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    IComponent IDesigner.Component => Component;

    DesignerVerbCollection IDesigner.Verbs => Verbs;

    void IDesigner.DoDefaultAction() => DoDefaultAction();

    void IDesigner.Initialize(IComponent component) => Initialize(component);

    ICollection ITreeDesigner.Children => Adapter?.BaseChildren ?? Array.Empty<IDesigner>();

    IDesigner? ITreeDesigner.Parent => Adapter?.BaseParent;

    void IDesignerFilter.PostFilterAttributes(IDictionary attributes) => PostFilterAttributes(attributes);

    void IDesignerFilter.PostFilterEvents(IDictionary events) => PostFilterEvents(events);

    void IDesignerFilter.PostFilterProperties(IDictionary properties) => PostFilterProperties(properties);

    void IDesignerFilter.PreFilterAttributes(IDictionary attributes) => PreFilterAttributes(attributes);

    void IDesignerFilter.PreFilterEvents(IDictionary events) => PreFilterEvents(events);

    void IDesignerFilter.PreFilterProperties(IDictionary properties) => PreFilterProperties(properties);

    void IComponentInitializer.InitializeExistingComponent(IDictionary? defaultValues) => InitializeExistingComponent(defaultValues);

    void IComponentInitializer.InitializeNewComponent(IDictionary? defaultValues) => InitializeNewComponent(defaultValues);
}

/// <summary>A control's designer.</summary>
public class ControlDesigner : ComponentDesigner
{
    private IControlAdapter? ControlAdapter => Adapter as IControlAdapter;

    public virtual Control Control => (Control)Component;

    public bool AutoResizeHandles
    {
        get => ControlAdapter?.AutoResizeHandles ?? false;
        set
        {
            if (ControlAdapter is { } adapter)
            {
                adapter.AutoResizeHandles = value;
            }
        }
    }

    public BehaviorService? BehaviorService => GetService<BehaviorService>();

    public virtual bool ParticipatesWithSnapLines => ControlAdapter?.BaseParticipatesWithSnapLines ?? true;

    public virtual SelectionRules SelectionRules => ControlAdapter is { } adapter ? (SelectionRules)(int)adapter.BaseSelectionRules : SelectionRules.Visible | SelectionRules.Moveable | SelectionRules.AllSizeable;

    public virtual IList<SnapLine> SnapLines => ControlAdapter?.BaseSnapLines ?? [];

    protected internal virtual bool EnableDragRect => ControlAdapter?.BaseEnableDragRect ?? false;

    public virtual bool CanBeParentedTo(IDesigner parentDesigner) => ControlAdapter?.BaseCanBeParentedTo(parentDesigner) ?? true;

    public virtual GlyphCollection GetGlyphs(GlyphSelectionType selectionType) => [];

    protected internal virtual ControlBodyGlyph GetControlGlyph(GlyphSelectionType selectionType) =>
        new(Control.Parent is not null && BehaviorService is { } behaviors ? behaviors.ControlRectInAdornerWindow(Control) : Control.Bounds, Cursors.SizeAll, Component, (Behavior?)null);

    protected internal virtual bool GetHitTest(Point point) => ControlAdapter?.BaseGetHitTest(point) ?? false;

    protected bool EnableDesignMode(Control child, string name) => ControlAdapter?.BaseEnableDesignMode(child, name) ?? false;

    protected void UnhookChildControls(Control firstChild) => ControlAdapter?.BaseUnhookChildControls(firstChild);

    protected void DefWndProc(ref Message m)
    {
        if (ControlAdapter is { } adapter)
        {
            adapter.BaseDefWndProc(ref m);
        }
    }

    protected internal virtual void WndProc(ref Message m)
    {
        if (ControlAdapter is { } adapter)
        {
            adapter.BaseWndProc(ref m);
        }
    }

    protected internal virtual void OnCreateHandle() => ControlAdapter?.BaseOnCreateHandle();

    protected internal virtual void OnPaintAdornments(PaintEventArgs pe) => ControlAdapter?.BaseOnPaintAdornments(pe);

    protected internal virtual void OnDragDrop(DragEventArgs de) => ControlAdapter?.BaseOnDragDrop(de);

    protected internal virtual void OnDragEnter(DragEventArgs de) => ControlAdapter?.BaseOnDragEnter(de);

    protected internal virtual void OnDragLeave(EventArgs e) => ControlAdapter?.BaseOnDragLeave(e);

    protected internal virtual void OnDragOver(DragEventArgs de) => ControlAdapter?.BaseOnDragOver(de);

    protected internal virtual void OnGiveFeedback(GiveFeedbackEventArgs e) => ControlAdapter?.BaseOnGiveFeedback(e);

    // The mouse: the shell has it (it moves, sizes and selects), so these are never called.
    protected virtual void OnMouseDoubleClick(MouseEventArgs e)
    {
    }

    protected virtual void OnMouseDown(MouseEventArgs e)
    {
    }

    protected virtual void OnMouseMove(MouseEventArgs e)
    {
    }

    protected virtual void OnMouseUp(MouseEventArgs e)
    {
    }

    protected virtual void OnMouseEnter()
    {
    }

    protected virtual void OnMouseHover()
    {
    }

    protected virtual void OnMouseLeave()
    {
    }

    protected virtual void OnMouseDragBegin(int x, int y, Keys modifierKeys)
    {
    }

    protected virtual void OnMouseDragEnd(bool cancel, Keys modifierKeys)
    {
    }

    protected virtual void OnMouseDragMove(int x, int y)
    {
    }

    protected virtual void OnSetCursor()
    {
    }
}

/// <summary>A container control's designer.</summary>
public class ParentControlDesigner : ControlDesigner
{
    private IParentAdapter? ParentAdapter => Adapter as IParentAdapter;

    protected internal virtual bool AllowControlLasso => ParentAdapter?.BaseAllowControlLasso ?? true;

    protected internal virtual bool AllowGenericDragBox => ParentAdapter?.BaseAllowGenericDragBox ?? true;

    protected internal virtual bool AllowSetChildIndexOnDrop => ParentAdapter?.BaseAllowSetChildIndexOnDrop ?? true;

    protected internal virtual bool DrawGrid
    {
        get => ParentAdapter?.BaseDrawGrid ?? false;
        set
        {
            if (ParentAdapter is { } adapter)
            {
                adapter.BaseDrawGrid = value;
            }
        }
    }

    public Size GridSize
    {
        get => ParentAdapter?.GridSize ?? new Size(8, 8);
        set
        {
            if (ParentAdapter is { } adapter)
            {
                adapter.GridSize = value;
            }
        }
    }

    public virtual bool CanParent(Control control) => ParentAdapter?.BaseCanParent(control) ?? true;

    public virtual bool CanParent(ControlDesigner controlDesigner) => controlDesigner is not null && CanParent(controlDesigner.Control);

    protected internal virtual Control? GetParentForComponent(IComponent component) => ParentAdapter?.BaseGetParentForComponent(component);

    protected internal virtual void OnDragComplete(DragEventArgs de) => ParentAdapter?.BaseOnDragComplete(de);
}

/// <summary>A scrolling container's designer.</summary>
public class ScrollableControlDesigner : ParentControlDesigner
{
}

/// <summary>
/// A root designer (a form's, a user control's). A vendor's root designer isn't given to the surface yet: the root is
/// designed with its base type's root designer (ADR 0106).
/// </summary>
public class DocumentDesigner : ScrollableControlDesigner, IRootDesigner
{
    ViewTechnology[] IRootDesigner.SupportedTechnologies => [ViewTechnology.Default];

    object IRootDesigner.GetView(ViewTechnology technology) => throw new NotSupportedException("A control library's root designer isn't supported by Codalus's Windows Forms designer (ADR 0106).");
}
