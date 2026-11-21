using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Microsoft.DotNet.DesignTools.Designers;
using Microsoft.DotNet.DesignTools.Designers.Behaviors;
using Microsoft.DotNet.DesignTools.Protocol.Endpoints;
using Microsoft.DotNet.DesignTools.Protocol.Notifications;
using Microsoft.DotNet.DesignTools.Services;
using UndoEngine = Microsoft.DotNet.DesignTools.Undo.UndoEngine;
using ComponentDesigner = Microsoft.DotNet.DesignTools.Designers.ComponentDesigner;
using Swf = System.ComponentModel.Design;
using SwfBehavior = System.Windows.Forms.Design.Behavior;
using SwfDesign = System.Windows.Forms.Design;

// Codalus's bridge between a control library's designers (built on the replacement's designers) and Windows Forms'
// design surface (#461, ADR 0106): what an adapter gives the vendor's designer (its base class's behavior), the services a
// vendor's designer asks for, and the designer host it sees.

namespace Codalus.WinForms.DesignTools;

/// <summary>What a vendor's designer gets from the Windows Forms designer that gives it to the surface: its base class's behavior.</summary>
internal interface IVendorAdapter
{
    /// <summary>The vendor's designer, or null when it failed (the adapter is then its base designer alone).</summary>
    ComponentDesigner? Vendor { get; }

    IReadOnlyCollection<IComponent> BaseAssociatedComponents { get; }

    DesignerVerbCollection BaseVerbs { get; }

    InheritanceAttribute BaseInheritanceAttribute { get; }

    IComponent? BaseParentComponent { get; }

    ICollection BaseChildren { get; }

    IDesigner? BaseParent { get; }

    object? BaseGetService(Type serviceType);

    void BaseInitializeNewComponent(IDictionary? defaultValues);

    void BaseInitializeExistingComponent(IDictionary? defaultValues);

    void BaseDoDefaultAction();

    void BasePreFilterAttributes(IDictionary attributes);

    void BasePreFilterEvents(IDictionary events);

    void BasePreFilterProperties(IDictionary properties);

    void BasePostFilterAttributes(IDictionary attributes);

    void BasePostFilterEvents(IDictionary events);

    void BasePostFilterProperties(IDictionary properties);
}

/// <summary>What a vendor's control designer gets from Windows Forms' <c>ControlDesigner</c>.</summary>
internal interface IControlAdapter : IVendorAdapter
{
    bool AutoResizeHandles { get; set; }

    bool BaseParticipatesWithSnapLines { get; }

    SwfDesign.SelectionRules BaseSelectionRules { get; }

    IList<SnapLine> BaseSnapLines { get; }

    bool BaseEnableDragRect { get; }

    bool BaseCanBeParentedTo(IDesigner parentDesigner);

    bool BaseGetHitTest(Point point);

    bool BaseEnableDesignMode(Control child, string name);

    void BaseUnhookChildControls(Control firstChild);

    void BaseDefWndProc(ref Message m);

    void BaseWndProc(ref Message m);

    void BaseOnCreateHandle();

    void BaseOnPaintAdornments(PaintEventArgs pe);

    void BaseOnDragDrop(DragEventArgs de);

    void BaseOnDragEnter(DragEventArgs de);

    void BaseOnDragLeave(EventArgs e);

    void BaseOnDragOver(DragEventArgs de);

    void BaseOnGiveFeedback(GiveFeedbackEventArgs e);
}

/// <summary>What a vendor's container designer gets from Windows Forms' <c>ParentControlDesigner</c>.</summary>
internal interface IParentAdapter : IControlAdapter
{
    bool BaseAllowControlLasso { get; }

    bool BaseAllowGenericDragBox { get; }

    bool BaseAllowSetChildIndexOnDrop { get; }

    bool BaseDrawGrid { get; set; }

    Size GridSize { get; set; }

    bool BaseCanParent(Control control);

    Control? BaseGetParentForComponent(IComponent component);

    void BaseOnDragComplete(DragEventArgs de);
}

/// <summary>The host's log (the IDE's Windows Forms Designer output).</summary>
internal static class VendorLog
{
    private static readonly ConcurrentDictionary<string, bool> s_once = new(StringComparer.Ordinal);

    public static void Write(string message) => Console.Error.WriteLine("[designer] " + message);

    /// <summary>Writes <paramref name="message"/> the first time only.</summary>
    public static void Once(string message)
    {
        if (s_once.TryAdd(message, true))
        {
            Write(message);
        }
    }

    public static string Describe(Exception ex)
    {
        var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
        return $"{cause.GetType().Name}: {cause.Message}";
    }

    /// <summary>Whether an exception is one the designer survives (a vendor's designer failing), not the process's end.</summary>
    public static bool Survivable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

/// <summary>The vendor's designer type each component type is designed with (the catalog registers them as it finds them).</summary>
internal static class VendorDesignerRegistry
{
    private static readonly ConcurrentDictionary<Type, Type> s_designers = new();

    public static void Register(Type componentType, Type designerType) => s_designers[componentType] = designerType;

    public static void Clear() => s_designers.Clear();

    /// <summary>A new vendor designer for <paramref name="component"/>, or null (none registered, or its constructor failed: said in the log).</summary>
    public static ComponentDesigner? Create(IComponent component)
    {
        for (var type = component.GetType(); type is not null; type = type.BaseType)
        {
            if (!s_designers.TryGetValue(type, out var designerType))
            {
                continue;
            }

            try
            {
                return (ComponentDesigner?)Activator.CreateInstance(designerType);
            }
            catch (Exception ex) when (VendorLog.Survivable(ex))
            {
                VendorLog.Write($"{designerType.FullName} couldn't be created for {component.GetType().FullName}: {VendorLog.Describe(ex)}. It's designed as its base type.");
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// The services a vendor's designer asks for, one set per designer host: the replacement's (designer actions, behaviors,
/// undo, notifications, the UI, requests to the classic IDE's process) over Windows Forms' own, and a designer host that
/// gives the vendor's designers rather than the surface's adapters.
/// </summary>
internal sealed class VendorServices
{
    private static readonly ConditionalWeakTable<IDesignerHost, VendorServices> s_byHost = new();
    private readonly IDesignerHost _inner;
    private readonly Dictionary<Type, object> _services = [];

    private VendorServices(IDesignerHost inner)
    {
        _inner = inner;
        Host = new VendorDesignerHost(inner, this);
    }

    public VendorDesignerHost Host { get; }

    public static VendorServices For(IDesignerHost host) => host is VendorDesignerHost vendor ? vendor.Services : s_byHost.GetValue(host, h => new VendorServices(h));

    /// <summary>
    /// The service <paramref name="serviceType"/> for a vendor's designer of <paramref name="component"/>: the
    /// replacement's own, the vendor's designer host, else <paramref name="fallback"/> (the site's).
    /// </summary>
    public static object? Get(IComponent? component, Type serviceType, Func<object?> fallback)
    {
        if (component?.Site?.GetService(typeof(IDesignerHost)) is IDesignerHost host)
        {
            var services = For(host);
            if (services.TryGet(serviceType, out var service))
            {
                return service;
            }
        }

        return fallback();
    }

    public bool TryGet(Type serviceType, out object? service)
    {
        if (serviceType == typeof(IDesignerHost))
        {
            service = Host;
            return true;
        }

        lock (_services)
        {
            if (_services.TryGetValue(serviceType, out var existing))
            {
                service = existing;
                return true;
            }

            object? created = serviceType == typeof(Microsoft.DotNet.DesignTools.Designers.Actions.DesignerActionService) ? (object)new Microsoft.DotNet.DesignTools.Designers.Actions.DesignerActionService(_inner.GetService(typeof(Swf.DesignerActionService)) as Swf.DesignerActionService)
                : serviceType == typeof(Microsoft.DotNet.DesignTools.Designers.Actions.DesignerActionUIService) ? new Microsoft.DotNet.DesignTools.Designers.Actions.DesignerActionUIService(_inner.GetService(typeof(Swf.DesignerActionUIService)) as Swf.DesignerActionUIService)
                : serviceType == typeof(BehaviorService) ? new BehaviorService(_inner.GetService(typeof(SwfBehavior.BehaviorService)) as SwfBehavior.BehaviorService)
                : serviceType == typeof(UndoEngine) ? new UndoEngine()
                : serviceType == typeof(INotificationService) ? new LoggedNotifications()
                : serviceType == typeof(Microsoft.DotNet.DesignTools.Services.IUIService) ? new UIService(_inner)
                : serviceType == typeof(IRequestSender) ? new NoClient()
                : null;
            if (created is null)
            {
                service = null;
                return false;
            }

            _services[serviceType] = created;
            service = created;
            return true;
        }
    }

    /// <summary>A vendor's designer for the surface's <paramref name="designer"/> (an adapter's), else the designer itself.</summary>
    public static IDesigner? Unwrap(IDesigner? designer) => designer is IVendorAdapter { Vendor: { } vendor } ? vendor : designer;

    /// <summary>The surface's designer for a vendor's <paramref name="designer"/> (its adapter), else the designer itself.</summary>
    public static IDesigner? Wrap(IDesigner? designer) => designer is ComponentDesigner { Adapter: IDesigner adapter } ? adapter : designer;

    private sealed class LoggedNotifications : INotificationService
    {
        public void SendNotification(string name, NotificationData data) => VendorLog.Once($"A control library's designer sent the notification {name} to the classic IDE's process, which Codalus doesn't have (ADR 0106).");

        public void SendObjectNotification(string name, object target, NotificationData data) => SendNotification(name, data);
    }

    private sealed class NoClient : IRequestSender
    {
        public TResponse SendRequest<TRequest, TResponse>(string endpointName, TRequest request)
            where TRequest : Request
            where TResponse : Response
        {
            VendorLog.Once($"A control library's designer asked the classic IDE's process for {endpointName}, which Codalus doesn't have: it gets no answer (ADR 0106).");
            return default!;
        }
    }

    private sealed class UIService(IServiceProvider services) : Microsoft.DotNet.DesignTools.Services.IUIService
    {
        private SwfDesign.IUIService? Inner => services.GetService(typeof(SwfDesign.IUIService)) as SwfDesign.IUIService;

        public IWin32Window? GetDialogOwnerWindow() => Inner?.GetDialogOwnerWindow();

        public DialogResult ShowDialog(Form form) => Inner?.ShowDialog(form) ?? DialogResult.Cancel;

        public void ShowError(string message) => Inner?.ShowError(message);

        public void ShowMessage(string message) => Inner?.ShowMessage(message);
    }
}

/// <summary>
/// The designer host as a vendor's designer sees it: Windows Forms' own, but <see cref="GetDesigner"/> gives the vendor's
/// designers (a vendor's code casts what it gets to its own designer types), and its services are the vendor's.
/// </summary>
internal sealed class VendorDesignerHost(IDesignerHost inner, VendorServices services) : IDesignerHost, IComponentChangeService
{
    internal VendorServices Services => services;

    private IComponentChangeService? Changes => inner.GetService(typeof(IComponentChangeService)) as IComponentChangeService;

    public IContainer Container => inner.Container;

    public bool InTransaction => inner.InTransaction;

    public bool Loading => inner.Loading;

    public IComponent RootComponent => inner.RootComponent;

    public string RootComponentClassName => inner.RootComponentClassName;

    public string TransactionDescription => inner.TransactionDescription;

    public event EventHandler? Activated { add => inner.Activated += value; remove => inner.Activated -= value; }

    public event EventHandler? Deactivated { add => inner.Deactivated += value; remove => inner.Deactivated -= value; }

    public event EventHandler? LoadComplete { add => inner.LoadComplete += value; remove => inner.LoadComplete -= value; }

    public event DesignerTransactionCloseEventHandler? TransactionClosed { add => inner.TransactionClosed += value; remove => inner.TransactionClosed -= value; }

    public event DesignerTransactionCloseEventHandler? TransactionClosing { add => inner.TransactionClosing += value; remove => inner.TransactionClosing -= value; }

    public event EventHandler? TransactionOpened { add => inner.TransactionOpened += value; remove => inner.TransactionOpened -= value; }

    public event EventHandler? TransactionOpening { add => inner.TransactionOpening += value; remove => inner.TransactionOpening -= value; }

    public event ComponentEventHandler? ComponentAdded { add => Changes!.ComponentAdded += value; remove => Changes!.ComponentAdded -= value; }

    public event ComponentEventHandler? ComponentAdding { add => Changes!.ComponentAdding += value; remove => Changes!.ComponentAdding -= value; }

    public event ComponentChangedEventHandler? ComponentChanged { add => Changes!.ComponentChanged += value; remove => Changes!.ComponentChanged -= value; }

    public event ComponentChangingEventHandler? ComponentChanging { add => Changes!.ComponentChanging += value; remove => Changes!.ComponentChanging -= value; }

    public event ComponentEventHandler? ComponentRemoved { add => Changes!.ComponentRemoved += value; remove => Changes!.ComponentRemoved -= value; }

    public event ComponentEventHandler? ComponentRemoving { add => Changes!.ComponentRemoving += value; remove => Changes!.ComponentRemoving -= value; }

    public event ComponentRenameEventHandler? ComponentRename { add => Changes!.ComponentRename += value; remove => Changes!.ComponentRename -= value; }

    public void Activate() => inner.Activate();

    public IComponent CreateComponent(Type componentClass) => inner.CreateComponent(componentClass);

    public IComponent CreateComponent(Type componentClass, string name) => inner.CreateComponent(componentClass, name);

    public DesignerTransaction CreateTransaction() => inner.CreateTransaction();

    public DesignerTransaction CreateTransaction(string description) => inner.CreateTransaction(description);

    public void DestroyComponent(IComponent component) => inner.DestroyComponent(component);

    public IDesigner? GetDesigner(IComponent component) => VendorServices.Unwrap(inner.GetDesigner(component));

    public Type? GetType(string typeName) => inner.GetType(typeName);

    public object? GetService(Type serviceType) => services.TryGet(serviceType, out var service) ? service : inner.GetService(serviceType);

    public void AddService(Type serviceType, object serviceInstance) => inner.AddService(serviceType, serviceInstance);

    public void AddService(Type serviceType, object serviceInstance, bool promote) => inner.AddService(serviceType, serviceInstance, promote);

    public void AddService(Type serviceType, ServiceCreatorCallback callback) => inner.AddService(serviceType, callback);

    public void AddService(Type serviceType, ServiceCreatorCallback callback, bool promote) => inner.AddService(serviceType, callback, promote);

    public void RemoveService(Type serviceType) => inner.RemoveService(serviceType);

    public void RemoveService(Type serviceType, bool promote) => inner.RemoveService(serviceType, promote);

    public void OnComponentChanged(object component, MemberDescriptor? member, object? oldValue, object? newValue) => Changes?.OnComponentChanged(component, member, oldValue, newValue);

    public void OnComponentChanging(object component, MemberDescriptor? member) => Changes?.OnComponentChanging(component, member);
}
