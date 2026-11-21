using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.DotNet.DesignTools.Designers;
using Microsoft.DotNet.DesignTools.Designers.Behaviors;
using ComponentDesigner = Microsoft.DotNet.DesignTools.Designers.ComponentDesigner;
using Swf = System.ComponentModel.Design;
using SwfBehavior = System.Windows.Forms.Design.Behavior;
using SwfDesign = System.Windows.Forms.Design;

// The adapters (#461, ADR 0106): Windows Forms designers that give a control library's designer to the design surface.
// The surface creates the adapter (the type descriptor's DesignerAttribute names it: VendorDesignerCatalog), the adapter
// creates the vendor's designer and forwards to it, and the vendor's designer calls back into the adapter's base class
// for what it doesn't override. A vendor's designer that throws is dropped, with the reason in the log, and the adapter
// is its base designer from then on: the component is designed as its base type is (#454).
//
// C# has no mixins, so the three adapters repeat their forwarding: each line forwards one member to AdapterCore.

namespace Codalus.WinForms.DesignTools;

/// <summary>An adapter's vendor designer: created, initialized, guarded, dropped when it fails.</summary>
internal sealed class AdapterCore
{
    private ComponentDesigner? _vendor;
    private bool _ready;
    private IComponent? _component;

    /// <summary>The vendor's designer once its Initialize has run (before, Windows Forms' base designer answers alone).</summary>
    public ComponentDesigner? Vendor => _ready ? _vendor : null;

    public void Create(IVendorAdapter adapter, IComponent component)
    {
        _component = component;
        _vendor = VendorDesignerRegistry.Create(component);
        _vendor?.Attach(adapter, component);
    }

    public void Initialize(IComponent component)
    {
        if (_vendor is { } vendor && Run(vendor, v => v.Initialize(component), nameof(ComponentDesigner.Initialize)))
        {
            _ready = true;
            VendorLog.Write($"{TypeDescriptor.GetClassName(component)} is designed with {vendor.GetType().FullName}.");
        }
    }

    public bool Run(ComponentDesigner vendor, Action<ComponentDesigner> action, string member)
    {
        try
        {
            action(vendor);
            return true;
        }
        catch (Exception ex) when (VendorLog.Survivable(ex))
        {
            Fail(member, ex);
            return false;
        }
    }

    public bool Run(Action<ComponentDesigner> action, string member) => Vendor is { } vendor && Run(vendor, action, member);

    public bool TryGet<T>(Func<ComponentDesigner, T> get, string member, out T value)
    {
        if (Vendor is { } vendor)
        {
            try
            {
                value = get(vendor);
                return true;
            }
            catch (Exception ex) when (VendorLog.Survivable(ex))
            {
                Fail(member, ex);
            }
        }

        value = default!;
        return false;
    }

    public void Fail(string member, Exception ex)
    {
        var vendor = _vendor;
        _vendor = null;
        _ready = false;
        if (vendor is null)
        {
            return;
        }

        VendorLog.Write($"{vendor.GetType().FullName}.{member} failed for {(_component?.Site?.Name ?? _component?.GetType().FullName)}: {VendorLog.Describe(ex)}. It's designed as its base type from now on.");
        vendor.Detach();
        Dispose(vendor);
    }

    public void Dispose()
    {
        var vendor = _vendor;
        _vendor = null;
        _ready = false;
        if (vendor is not null)
        {
            vendor.Detach();
            Dispose(vendor);
        }
    }

    private static void Dispose(ComponentDesigner vendor)
    {
        try
        {
            vendor.Dispose();
        }
        catch (Exception ex) when (VendorLog.Survivable(ex))
        {
            VendorLog.Write($"{vendor.GetType().FullName} failed as it was disposed: {VendorLog.Describe(ex)}.");
        }
    }

    public static IReadOnlyCollection<IComponent> Components(ICollection? collection) => collection is null ? [] : [.. collection.OfType<IComponent>()];

    public static IList SnapLinesOf(IList<SnapLine> lines)
    {
        var surface = new ArrayList(lines.Count);
        foreach (var line in lines)
        {
            surface.Add(line.ToSurface());
        }

        return surface;
    }

    public static IList<SnapLine> SnapLinesFrom(IList? lines) => lines is null ? [] : [.. lines.OfType<SwfBehavior.SnapLine>().Select(SnapLine.FromSurface)];

    public static ICollection Children(ITreeDesigner tree) => tree.Children.Cast<IDesigner>().Select(VendorServices.Unwrap).ToArray();
}

/// <summary>A component's vendor designer, as Windows Forms' <see cref="Swf.ComponentDesigner"/>.</summary>
internal sealed class ComponentAdapter : Swf.ComponentDesigner, IVendorAdapter
{
    private readonly AdapterCore _core = new();

    public ComponentDesigner? Vendor => _core.Vendor;

    public override void Initialize(IComponent component)
    {
        _core.Create(this, component);
        base.Initialize(component);
        _core.Initialize(component);
    }

    public override void InitializeNewComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeNewComponent(defaultValues), nameof(InitializeNewComponent)))
        {
            base.InitializeNewComponent(defaultValues);
        }
    }

    public override void InitializeExistingComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeExistingComponent(defaultValues), nameof(InitializeExistingComponent)))
        {
            base.InitializeExistingComponent(defaultValues);
        }
    }

    public override Swf.DesignerActionListCollection ActionLists
    {
        get
        {
            var lists = new Swf.DesignerActionListCollection();
            if (_core.TryGet(v => v.ActionLists, nameof(ActionLists), out var vendor))
            {
                foreach (var list in vendor)
                {
                    lists.Add(list);
                }
            }

            lists.AddRange(base.ActionLists);
            return lists;
        }
    }

    public override ICollection AssociatedComponents => _core.TryGet(v => v.AssociatedComponents, nameof(AssociatedComponents), out var components) ? components.ToArray() : base.AssociatedComponents;

    public override DesignerVerbCollection Verbs => _core.TryGet(v => v.Verbs, nameof(Verbs), out var verbs) ? verbs : base.Verbs;

    public override void DoDefaultAction()
    {
        if (!_core.Run(v => v.DoDefaultAction(), nameof(DoDefaultAction)))
        {
            base.DoDefaultAction();
        }
    }

    protected override InheritanceAttribute? InheritanceAttribute => _core.TryGet(v => v.InheritanceAttribute, nameof(InheritanceAttribute), out var attribute) ? attribute : base.InheritanceAttribute;

    protected override IComponent? ParentComponent => _core.TryGet(v => v.ParentComponent, nameof(ParentComponent), out var parent) ? parent : base.ParentComponent;

    protected override void PreFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PreFilterAttributes(attributes), nameof(PreFilterAttributes)))
        {
            base.PreFilterAttributes(attributes);
        }
    }

    protected override void PreFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PreFilterEvents(events), nameof(PreFilterEvents)))
        {
            base.PreFilterEvents(events);
        }
    }

    protected override void PreFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PreFilterProperties(properties), nameof(PreFilterProperties)))
        {
            base.PreFilterProperties(properties);
        }
    }

    protected override void PostFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PostFilterAttributes(attributes), nameof(PostFilterAttributes)))
        {
            base.PostFilterAttributes(attributes);
        }
    }

    protected override void PostFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PostFilterEvents(events), nameof(PostFilterEvents)))
        {
            base.PostFilterEvents(events);
        }
    }

    protected override void PostFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PostFilterProperties(properties), nameof(PostFilterProperties)))
        {
            base.PostFilterProperties(properties);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _core.Dispose();
        }

        base.Dispose(disposing);
    }

    IReadOnlyCollection<IComponent> IVendorAdapter.BaseAssociatedComponents => AdapterCore.Components(base.AssociatedComponents);

    DesignerVerbCollection IVendorAdapter.BaseVerbs => base.Verbs;

    InheritanceAttribute IVendorAdapter.BaseInheritanceAttribute => base.InheritanceAttribute ?? InheritanceAttribute.NotInherited;

    IComponent? IVendorAdapter.BaseParentComponent => base.ParentComponent;

    ICollection IVendorAdapter.BaseChildren => AdapterCore.Children(this);

    IDesigner? IVendorAdapter.BaseParent => VendorServices.Unwrap(((ITreeDesigner)this).Parent);

    object? IVendorAdapter.BaseGetService(Type serviceType) => GetService(serviceType);

    void IVendorAdapter.BaseInitializeNewComponent(IDictionary? defaultValues) => base.InitializeNewComponent(defaultValues);

    void IVendorAdapter.BaseInitializeExistingComponent(IDictionary? defaultValues) => base.InitializeExistingComponent(defaultValues);

    void IVendorAdapter.BaseDoDefaultAction() => base.DoDefaultAction();

    void IVendorAdapter.BasePreFilterAttributes(IDictionary attributes) => base.PreFilterAttributes(attributes);

    void IVendorAdapter.BasePreFilterEvents(IDictionary events) => base.PreFilterEvents(events);

    void IVendorAdapter.BasePreFilterProperties(IDictionary properties) => base.PreFilterProperties(properties);

    void IVendorAdapter.BasePostFilterAttributes(IDictionary attributes) => base.PostFilterAttributes(attributes);

    void IVendorAdapter.BasePostFilterEvents(IDictionary events) => base.PostFilterEvents(events);

    void IVendorAdapter.BasePostFilterProperties(IDictionary properties) => base.PostFilterProperties(properties);
}

/// <summary>A control's vendor designer, as Windows Forms' <see cref="SwfDesign.ControlDesigner"/>.</summary>
internal sealed class ControlAdapter : SwfDesign.ControlDesigner, IControlAdapter
{
    private readonly AdapterCore _core = new();

    public ComponentDesigner? Vendor => _core.Vendor;

    private ControlDesigner? VendorControl => _core.Vendor as ControlDesigner;

    public override void Initialize(IComponent component)
    {
        _core.Create(this, component);
        base.Initialize(component);
        _core.Initialize(component);
    }

    public override void InitializeNewComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeNewComponent(defaultValues), nameof(InitializeNewComponent)))
        {
            base.InitializeNewComponent(defaultValues);
        }
    }

    public override void InitializeExistingComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeExistingComponent(defaultValues), nameof(InitializeExistingComponent)))
        {
            base.InitializeExistingComponent(defaultValues);
        }
    }

    public override Swf.DesignerActionListCollection ActionLists
    {
        get
        {
            var lists = new Swf.DesignerActionListCollection();
            if (_core.TryGet(v => v.ActionLists, nameof(ActionLists), out var vendor))
            {
                foreach (var list in vendor)
                {
                    lists.Add(list);
                }
            }

            lists.AddRange(base.ActionLists);
            return lists;
        }
    }

    public override ICollection AssociatedComponents => _core.TryGet(v => v.AssociatedComponents, nameof(AssociatedComponents), out var components) ? components.ToArray() : base.AssociatedComponents;

    public override DesignerVerbCollection Verbs => _core.TryGet(v => v.Verbs, nameof(Verbs), out var verbs) ? verbs : base.Verbs;

    public override void DoDefaultAction()
    {
        if (!_core.Run(v => v.DoDefaultAction(), nameof(DoDefaultAction)))
        {
            base.DoDefaultAction();
        }
    }

    protected override InheritanceAttribute? InheritanceAttribute => _core.TryGet(v => v.InheritanceAttribute, nameof(InheritanceAttribute), out var attribute) ? attribute : base.InheritanceAttribute;

    protected override IComponent? ParentComponent => _core.TryGet(v => v.ParentComponent, nameof(ParentComponent), out var parent) ? parent : base.ParentComponent;

    protected override void PreFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PreFilterAttributes(attributes), nameof(PreFilterAttributes)))
        {
            base.PreFilterAttributes(attributes);
        }
    }

    protected override void PreFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PreFilterEvents(events), nameof(PreFilterEvents)))
        {
            base.PreFilterEvents(events);
        }
    }

    protected override void PreFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PreFilterProperties(properties), nameof(PreFilterProperties)))
        {
            base.PreFilterProperties(properties);
        }
    }

    protected override void PostFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PostFilterAttributes(attributes), nameof(PostFilterAttributes)))
        {
            base.PostFilterAttributes(attributes);
        }
    }

    protected override void PostFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PostFilterEvents(events), nameof(PostFilterEvents)))
        {
            base.PostFilterEvents(events);
        }
    }

    protected override void PostFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PostFilterProperties(properties), nameof(PostFilterProperties)))
        {
            base.PostFilterProperties(properties);
        }
    }

    public override bool CanBeParentedTo(IDesigner parentDesigner) => _core.TryGet(v => ((ControlDesigner)v).CanBeParentedTo(VendorServices.Unwrap(parentDesigner)!), nameof(CanBeParentedTo), out var can) ? can : base.CanBeParentedTo(parentDesigner);

    public override SwfDesign.SelectionRules SelectionRules => VendorControl is not null && _core.TryGet(v => ((ControlDesigner)v).SelectionRules, nameof(SelectionRules), out var rules) ? (SwfDesign.SelectionRules)(int)rules : base.SelectionRules;

    public override IList SnapLines => VendorControl is not null && _core.TryGet(v => ((ControlDesigner)v).SnapLines, nameof(SnapLines), out var lines) ? AdapterCore.SnapLinesOf(lines) : base.SnapLines;

    public override bool ParticipatesWithSnapLines => VendorControl is not null && _core.TryGet(v => ((ControlDesigner)v).ParticipatesWithSnapLines, nameof(ParticipatesWithSnapLines), out var participates) ? participates : base.ParticipatesWithSnapLines;

    protected override bool EnableDragRect => VendorControl is not null && _core.TryGet(v => ((ControlDesigner)v).EnableDragRect, nameof(EnableDragRect), out var enable) ? enable : base.EnableDragRect;

    protected override bool GetHitTest(Point point) => VendorControl is not null && _core.TryGet(v => ((ControlDesigner)v).GetHitTest(point), nameof(GetHitTest), out var hit) ? hit : base.GetHitTest(point);

    protected override void OnCreateHandle()
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnCreateHandle(), nameof(OnCreateHandle)))
        {
            base.OnCreateHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (VendorControl is { } vendor)
        {
            try
            {
                vendor.WndProc(ref m);
                return;
            }
            catch (Exception ex) when (VendorLog.Survivable(ex))
            {
                _core.Fail(nameof(WndProc), ex);
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnPaintAdornments(PaintEventArgs pe)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnPaintAdornments(pe), nameof(OnPaintAdornments)))
        {
            base.OnPaintAdornments(pe);
        }
    }

    protected override void OnDragDrop(DragEventArgs de)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnDragDrop(de), nameof(OnDragDrop)))
        {
            base.OnDragDrop(de);
        }
    }

    protected override void OnDragEnter(DragEventArgs de)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnDragEnter(de), nameof(OnDragEnter)))
        {
            base.OnDragEnter(de);
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnDragLeave(e), nameof(OnDragLeave)))
        {
            base.OnDragLeave(e);
        }
    }

    protected override void OnDragOver(DragEventArgs de)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnDragOver(de), nameof(OnDragOver)))
        {
            base.OnDragOver(de);
        }
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        if (VendorControl is null || !_core.Run(v => ((ControlDesigner)v).OnGiveFeedback(e), nameof(OnGiveFeedback)))
        {
            base.OnGiveFeedback(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _core.Dispose();
        }

        base.Dispose(disposing);
    }

    IReadOnlyCollection<IComponent> IVendorAdapter.BaseAssociatedComponents => AdapterCore.Components(base.AssociatedComponents);

    DesignerVerbCollection IVendorAdapter.BaseVerbs => base.Verbs;

    InheritanceAttribute IVendorAdapter.BaseInheritanceAttribute => base.InheritanceAttribute ?? InheritanceAttribute.NotInherited;

    IComponent? IVendorAdapter.BaseParentComponent => base.ParentComponent;

    ICollection IVendorAdapter.BaseChildren => AdapterCore.Children(this);

    IDesigner? IVendorAdapter.BaseParent => VendorServices.Unwrap(((ITreeDesigner)this).Parent);

    object? IVendorAdapter.BaseGetService(Type serviceType) => GetService(serviceType);

    void IVendorAdapter.BaseInitializeNewComponent(IDictionary? defaultValues) => base.InitializeNewComponent(defaultValues);

    void IVendorAdapter.BaseInitializeExistingComponent(IDictionary? defaultValues) => base.InitializeExistingComponent(defaultValues);

    void IVendorAdapter.BaseDoDefaultAction() => base.DoDefaultAction();

    void IVendorAdapter.BasePreFilterAttributes(IDictionary attributes) => base.PreFilterAttributes(attributes);

    void IVendorAdapter.BasePreFilterEvents(IDictionary events) => base.PreFilterEvents(events);

    void IVendorAdapter.BasePreFilterProperties(IDictionary properties) => base.PreFilterProperties(properties);

    void IVendorAdapter.BasePostFilterAttributes(IDictionary attributes) => base.PostFilterAttributes(attributes);

    void IVendorAdapter.BasePostFilterEvents(IDictionary events) => base.PostFilterEvents(events);

    void IVendorAdapter.BasePostFilterProperties(IDictionary properties) => base.PostFilterProperties(properties);

    bool IControlAdapter.BaseParticipatesWithSnapLines => base.ParticipatesWithSnapLines;

    SwfDesign.SelectionRules IControlAdapter.BaseSelectionRules => base.SelectionRules;

    IList<SnapLine> IControlAdapter.BaseSnapLines => AdapterCore.SnapLinesFrom(base.SnapLines);

    bool IControlAdapter.BaseEnableDragRect => base.EnableDragRect;

    bool IControlAdapter.BaseCanBeParentedTo(IDesigner parentDesigner) => base.CanBeParentedTo(VendorServices.Wrap(parentDesigner)!);

    bool IControlAdapter.BaseGetHitTest(Point point) => base.GetHitTest(point);

    bool IControlAdapter.BaseEnableDesignMode(Control child, string name) => EnableDesignMode(child, name);

    void IControlAdapter.BaseUnhookChildControls(Control firstChild) => UnhookChildControls(firstChild);

    void IControlAdapter.BaseDefWndProc(ref Message m) => DefWndProc(ref m);

    void IControlAdapter.BaseWndProc(ref Message m) => base.WndProc(ref m);

    void IControlAdapter.BaseOnCreateHandle() => base.OnCreateHandle();

    void IControlAdapter.BaseOnPaintAdornments(PaintEventArgs pe) => base.OnPaintAdornments(pe);

    void IControlAdapter.BaseOnDragDrop(DragEventArgs de) => base.OnDragDrop(de);

    void IControlAdapter.BaseOnDragEnter(DragEventArgs de) => base.OnDragEnter(de);

    void IControlAdapter.BaseOnDragLeave(EventArgs e) => base.OnDragLeave(e);

    void IControlAdapter.BaseOnDragOver(DragEventArgs de) => base.OnDragOver(de);

    void IControlAdapter.BaseOnGiveFeedback(GiveFeedbackEventArgs e) => base.OnGiveFeedback(e);
}

/// <summary>A container control's vendor designer, as Windows Forms' <see cref="SwfDesign.ParentControlDesigner"/>.</summary>
internal sealed class ParentAdapter : SwfDesign.ParentControlDesigner, IParentAdapter
{
    private readonly AdapterCore _core = new();

    public ComponentDesigner? Vendor => _core.Vendor;

    private ParentControlDesigner? VendorParent => _core.Vendor as ParentControlDesigner;

    public override void Initialize(IComponent component)
    {
        _core.Create(this, component);
        base.Initialize(component);
        _core.Initialize(component);
    }

    public override void InitializeNewComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeNewComponent(defaultValues), nameof(InitializeNewComponent)))
        {
            base.InitializeNewComponent(defaultValues);
        }
    }

    public override void InitializeExistingComponent(IDictionary? defaultValues)
    {
        if (!_core.Run(v => v.InitializeExistingComponent(defaultValues), nameof(InitializeExistingComponent)))
        {
            base.InitializeExistingComponent(defaultValues);
        }
    }

    public override Swf.DesignerActionListCollection ActionLists
    {
        get
        {
            var lists = new Swf.DesignerActionListCollection();
            if (_core.TryGet(v => v.ActionLists, nameof(ActionLists), out var vendor))
            {
                foreach (var list in vendor)
                {
                    lists.Add(list);
                }
            }

            lists.AddRange(base.ActionLists);
            return lists;
        }
    }

    public override ICollection AssociatedComponents => _core.TryGet(v => v.AssociatedComponents, nameof(AssociatedComponents), out var components) ? components.ToArray() : base.AssociatedComponents;

    public override DesignerVerbCollection Verbs => _core.TryGet(v => v.Verbs, nameof(Verbs), out var verbs) ? verbs : base.Verbs;

    public override void DoDefaultAction()
    {
        if (!_core.Run(v => v.DoDefaultAction(), nameof(DoDefaultAction)))
        {
            base.DoDefaultAction();
        }
    }

    protected override InheritanceAttribute? InheritanceAttribute => _core.TryGet(v => v.InheritanceAttribute, nameof(InheritanceAttribute), out var attribute) ? attribute : base.InheritanceAttribute;

    protected override IComponent? ParentComponent => _core.TryGet(v => v.ParentComponent, nameof(ParentComponent), out var parent) ? parent : base.ParentComponent;

    protected override void PreFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PreFilterAttributes(attributes), nameof(PreFilterAttributes)))
        {
            base.PreFilterAttributes(attributes);
        }
    }

    protected override void PreFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PreFilterEvents(events), nameof(PreFilterEvents)))
        {
            base.PreFilterEvents(events);
        }
    }

    protected override void PreFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PreFilterProperties(properties), nameof(PreFilterProperties)))
        {
            base.PreFilterProperties(properties);
        }
    }

    protected override void PostFilterAttributes(IDictionary attributes)
    {
        if (!_core.Run(v => v.PostFilterAttributes(attributes), nameof(PostFilterAttributes)))
        {
            base.PostFilterAttributes(attributes);
        }
    }

    protected override void PostFilterEvents(IDictionary events)
    {
        if (!_core.Run(v => v.PostFilterEvents(events), nameof(PostFilterEvents)))
        {
            base.PostFilterEvents(events);
        }
    }

    protected override void PostFilterProperties(IDictionary properties)
    {
        if (!_core.Run(v => v.PostFilterProperties(properties), nameof(PostFilterProperties)))
        {
            base.PostFilterProperties(properties);
        }
    }

    public override bool CanBeParentedTo(IDesigner parentDesigner) => _core.TryGet(v => ((ControlDesigner)v).CanBeParentedTo(VendorServices.Unwrap(parentDesigner)!), nameof(CanBeParentedTo), out var can) ? can : base.CanBeParentedTo(parentDesigner);

    public override bool CanParent(Control control) => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).CanParent(control), nameof(CanParent), out var can) ? can : base.CanParent(control);

    public override SwfDesign.SelectionRules SelectionRules => VendorParent is not null && _core.TryGet(v => ((ControlDesigner)v).SelectionRules, nameof(SelectionRules), out var rules) ? (SwfDesign.SelectionRules)(int)rules : base.SelectionRules;

    public override IList SnapLines => VendorParent is not null && _core.TryGet(v => ((ControlDesigner)v).SnapLines, nameof(SnapLines), out var lines) ? AdapterCore.SnapLinesOf(lines) : base.SnapLines;

    public override bool ParticipatesWithSnapLines => VendorParent is not null && _core.TryGet(v => ((ControlDesigner)v).ParticipatesWithSnapLines, nameof(ParticipatesWithSnapLines), out var participates) ? participates : base.ParticipatesWithSnapLines;

    protected override bool AllowControlLasso => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).AllowControlLasso, nameof(AllowControlLasso), out var allow) ? allow : base.AllowControlLasso;

    protected override bool AllowGenericDragBox => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).AllowGenericDragBox, nameof(AllowGenericDragBox), out var allow) ? allow : base.AllowGenericDragBox;

    protected override bool AllowSetChildIndexOnDrop => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).AllowSetChildIndexOnDrop, nameof(AllowSetChildIndexOnDrop), out var allow) ? allow : base.AllowSetChildIndexOnDrop;

    protected override bool DrawGrid
    {
        get => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).DrawGrid, nameof(DrawGrid), out var draw) ? draw : base.DrawGrid;
        set => base.DrawGrid = value;
    }

    protected override bool EnableDragRect => VendorParent is not null && _core.TryGet(v => ((ControlDesigner)v).EnableDragRect, nameof(EnableDragRect), out var enable) ? enable : base.EnableDragRect;

    protected override bool GetHitTest(Point point) => VendorParent is not null && _core.TryGet(v => ((ControlDesigner)v).GetHitTest(point), nameof(GetHitTest), out var hit) ? hit : base.GetHitTest(point);

    protected override Control? GetParentForComponent(IComponent component) => VendorParent is not null && _core.TryGet(v => ((ParentControlDesigner)v).GetParentForComponent(component), nameof(GetParentForComponent), out var parent) && parent is not null ? parent : base.GetParentForComponent(component);

    protected override void OnCreateHandle()
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnCreateHandle(), nameof(OnCreateHandle)))
        {
            base.OnCreateHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (VendorParent is { } vendor)
        {
            try
            {
                vendor.WndProc(ref m);
                return;
            }
            catch (Exception ex) when (VendorLog.Survivable(ex))
            {
                _core.Fail(nameof(WndProc), ex);
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnPaintAdornments(PaintEventArgs pe)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnPaintAdornments(pe), nameof(OnPaintAdornments)))
        {
            base.OnPaintAdornments(pe);
        }
    }

    protected override void OnDragComplete(DragEventArgs de)
    {
        if (VendorParent is null || !_core.Run(v => ((ParentControlDesigner)v).OnDragComplete(de), nameof(OnDragComplete)))
        {
            base.OnDragComplete(de);
        }
    }

    protected override void OnDragDrop(DragEventArgs de)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnDragDrop(de), nameof(OnDragDrop)))
        {
            base.OnDragDrop(de);
        }
    }

    protected override void OnDragEnter(DragEventArgs de)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnDragEnter(de), nameof(OnDragEnter)))
        {
            base.OnDragEnter(de);
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnDragLeave(e), nameof(OnDragLeave)))
        {
            base.OnDragLeave(e);
        }
    }

    protected override void OnDragOver(DragEventArgs de)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnDragOver(de), nameof(OnDragOver)))
        {
            base.OnDragOver(de);
        }
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        if (VendorParent is null || !_core.Run(v => ((ControlDesigner)v).OnGiveFeedback(e), nameof(OnGiveFeedback)))
        {
            base.OnGiveFeedback(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _core.Dispose();
        }

        base.Dispose(disposing);
    }

    IReadOnlyCollection<IComponent> IVendorAdapter.BaseAssociatedComponents => AdapterCore.Components(base.AssociatedComponents);

    DesignerVerbCollection IVendorAdapter.BaseVerbs => base.Verbs;

    InheritanceAttribute IVendorAdapter.BaseInheritanceAttribute => base.InheritanceAttribute ?? InheritanceAttribute.NotInherited;

    IComponent? IVendorAdapter.BaseParentComponent => base.ParentComponent;

    ICollection IVendorAdapter.BaseChildren => AdapterCore.Children(this);

    IDesigner? IVendorAdapter.BaseParent => VendorServices.Unwrap(((ITreeDesigner)this).Parent);

    object? IVendorAdapter.BaseGetService(Type serviceType) => GetService(serviceType);

    void IVendorAdapter.BaseInitializeNewComponent(IDictionary? defaultValues) => base.InitializeNewComponent(defaultValues);

    void IVendorAdapter.BaseInitializeExistingComponent(IDictionary? defaultValues) => base.InitializeExistingComponent(defaultValues);

    void IVendorAdapter.BaseDoDefaultAction() => base.DoDefaultAction();

    void IVendorAdapter.BasePreFilterAttributes(IDictionary attributes) => base.PreFilterAttributes(attributes);

    void IVendorAdapter.BasePreFilterEvents(IDictionary events) => base.PreFilterEvents(events);

    void IVendorAdapter.BasePreFilterProperties(IDictionary properties) => base.PreFilterProperties(properties);

    void IVendorAdapter.BasePostFilterAttributes(IDictionary attributes) => base.PostFilterAttributes(attributes);

    void IVendorAdapter.BasePostFilterEvents(IDictionary events) => base.PostFilterEvents(events);

    void IVendorAdapter.BasePostFilterProperties(IDictionary properties) => base.PostFilterProperties(properties);

    bool IControlAdapter.BaseParticipatesWithSnapLines => base.ParticipatesWithSnapLines;

    SwfDesign.SelectionRules IControlAdapter.BaseSelectionRules => base.SelectionRules;

    IList<SnapLine> IControlAdapter.BaseSnapLines => AdapterCore.SnapLinesFrom(base.SnapLines);

    bool IControlAdapter.BaseEnableDragRect => base.EnableDragRect;

    bool IControlAdapter.BaseCanBeParentedTo(IDesigner parentDesigner) => base.CanBeParentedTo(VendorServices.Wrap(parentDesigner)!);

    bool IControlAdapter.BaseGetHitTest(Point point) => base.GetHitTest(point);

    bool IControlAdapter.BaseEnableDesignMode(Control child, string name) => EnableDesignMode(child, name);

    void IControlAdapter.BaseUnhookChildControls(Control firstChild) => UnhookChildControls(firstChild);

    void IControlAdapter.BaseDefWndProc(ref Message m) => DefWndProc(ref m);

    void IControlAdapter.BaseWndProc(ref Message m) => base.WndProc(ref m);

    void IControlAdapter.BaseOnCreateHandle() => base.OnCreateHandle();

    void IControlAdapter.BaseOnPaintAdornments(PaintEventArgs pe) => base.OnPaintAdornments(pe);

    void IControlAdapter.BaseOnDragDrop(DragEventArgs de) => base.OnDragDrop(de);

    void IControlAdapter.BaseOnDragEnter(DragEventArgs de) => base.OnDragEnter(de);

    void IControlAdapter.BaseOnDragLeave(EventArgs e) => base.OnDragLeave(e);

    void IControlAdapter.BaseOnDragOver(DragEventArgs de) => base.OnDragOver(de);

    void IControlAdapter.BaseOnGiveFeedback(GiveFeedbackEventArgs e) => base.OnGiveFeedback(e);

    bool IParentAdapter.BaseAllowControlLasso => base.AllowControlLasso;

    bool IParentAdapter.BaseAllowGenericDragBox => base.AllowGenericDragBox;

    bool IParentAdapter.BaseAllowSetChildIndexOnDrop => base.AllowSetChildIndexOnDrop;

    bool IParentAdapter.BaseDrawGrid
    {
        get => base.DrawGrid;
        set => base.DrawGrid = value;
    }

    Size IParentAdapter.GridSize
    {
        get => GridSize;
        set => GridSize = value;
    }

    bool IParentAdapter.BaseCanParent(Control control) => base.CanParent(control);

    Control? IParentAdapter.BaseGetParentForComponent(IComponent component) => base.GetParentForComponent(component);

    void IParentAdapter.BaseOnDragComplete(DragEventArgs de) => base.OnDragComplete(de);
}
