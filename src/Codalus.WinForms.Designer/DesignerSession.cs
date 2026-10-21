using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms.Design;
using System.Windows.Forms.Design.Behavior;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// One form on Windows Forms' own design surface (M41a and M41b, ADR 0073). <see cref="Load"/> creates the root (an
/// instance of the class's base type, as Visual Studio's designer does) and the components, and runs InitializeComponent's
/// statements against them through the designers (so a designed control is in design mode). A change sets properties
/// through their property descriptors (or runs a smart tag's action, edits a collection, moves a control in the
/// z-order), and what it changed comes back as code: the surface's state as the code would say it (properties,
/// extender properties, data bindings, collections, the controls' parents and order) is captured before and after,
/// and the difference is the change (DesignerSession.State.cs); a localizable form's localizable values go to its
/// resources.
/// </summary>
internal sealed partial class DesignerSession : IDisposable
{
    private readonly HostWindow _window;
    private readonly DesignerTypes _types = new();
    private readonly Dictionary<string, object?> _locals = new(StringComparer.Ordinal);
    private readonly System.ComponentModel.Container _components = new();
    private readonly ServiceContainer _services = new();
    private readonly DesignerValues _values;
    private DesignSurface? _surface;
    private IDesignerHost? _host;
    private Control? _root;
    private Control? _view;
    private ComponentResourceManager? _resources;
    private TextResourceManager? _textResources;
    private CultureInfo _culture = CultureInfo.InvariantCulture;
    private bool _localizable;
    private string _className = string.Empty;
    private string[] _searchFolders = [];
    private string? _phase;
    private readonly DesignerUIService _ui;

    /// <summary>The name the IDE chose for the component a Toolbox item creates next (#66), with its type: the name service gives it once.</summary>
    private (string Name, Type Type)? _pendingName;

    public DesignerSession(HostWindow window)
    {
        _window = window;
        _values = new DesignerValues(_types, Target, NameOf, Resource);
        _ui = new DesignerUIService(window);
        _services.AddService(typeof(INameCreationService), new NameCreation(this));
        _services.AddService(typeof(IUIService), _ui);
        _services.AddService(typeof(ITypeResolutionService), new DesignerTypeResolution(_types));
    }

    private IDesignerHost Host => _host ?? throw new InvalidOperationException("No form is loaded.");

    private Control Root => _root ?? throw new InvalidOperationException("No form is loaded.");

    /// <summary>Where a load or a drawing is (read off the UI thread by <c>designer/status</c>), or null.</summary>
    public string? Phase => Volatile.Read(ref _phase);

    private void Enter(string? phase) => Volatile.Write(ref _phase, phase);

    /// <summary>The project's references, from its output folder (a library's, which has no deps.json of its own).</summary>
    public Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (var folder in _searchFolders)
        {
            var candidate = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ loading

    public DesignerRenderResult Load(DesignerLoadParams parameters)
    {
        try
        {
            return LoadCore(parameters);
        }
        finally
        {
            Enter(null);
        }
    }

    private DesignerRenderResult LoadCore(DesignerLoadParams parameters)
    {
        Enter("unloading the previous form");
        Unload();
        var errors = new List<string>();
        _searchFolders = [.. parameters.SearchFolders.Append(Path.GetDirectoryName(parameters.ProjectAssembly)!).Distinct(StringComparer.OrdinalIgnoreCase)];
        Assembly? project = null;
        if (File.Exists(parameters.ProjectAssembly))
        {
            Enter($"loading {Path.GetFileName(parameters.ProjectAssembly)}");
            project = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => !a.IsDynamic && string.Equals(a.Location, parameters.ProjectAssembly, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(parameters.ProjectAssembly);
            _types.Add(project);

            // The IDE's Windows Forms Designer output: where the project came from (#66: the shell's copy of the build output, not bin/).
            Console.Error.WriteLine($"[designer] {project.GetName().Name} loaded from {project.Location}");
        }
        else
        {
            errors.Add($"{Path.GetFileName(parameters.ProjectAssembly)} isn't built: the project's own types and resources aren't available.");
        }

        var model = parameters.Model;
        var rootType = _types.Find(model.BaseType) ?? throw new InvalidOperationException($"The base type {model.BaseType} wasn't found (build the project).");
        Enter($"loading the design surface for {model.BaseType}");
        _surface = new DesignSurface(_services);
        _surface.BeginLoad(rootType);
        if (_surface.LoadErrors.Count > 0)
        {
            throw new InvalidOperationException("The design surface didn't load: " + string.Join("; ", _surface.LoadErrors.Cast<object>().Select(e => (e as Exception)?.Message ?? e.ToString())));
        }

        _host = (IDesignerHost)_surface.GetService(typeof(IDesignerHost))!;
        _root = (Control)_host.RootComponent;
        _className = model.ClassName;
        try
        {
            _root.Site!.Name = model.ClassName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            errors.Add($"The root can't be named {model.ClassName}: {ex.Message}");
        }

        // The resources: the editor's .resx texts (M41b), else the build's compiled ones.
        _culture = parameters.Culture is { Length: > 0 } culture ? CultureInfo.GetCultureInfo(culture) : CultureInfo.InvariantCulture;
        _localizable = parameters.Localizable;
        if (parameters.Resources.Length > 0)
        {
            _textResources = new TextResourceManager(parameters.Resources, null);
            _resources = _textResources;
        }
        else if (parameters.ClassFullName is { } className && project?.GetType(className, throwOnError: false) is { } classType)
        {
            _resources = new ComponentResourceManager(classType);
        }

        _view = (Control)_surface.View;
        _view.Dock = DockStyle.Fill;
        _window.Controls.Add(_view);

        var locals = model.Components.Where(c => c.IsLocal).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var statement in model.Statements)
        {
            try
            {
                Enter($"running line {statement.Line + 1} of the designer file");
                Execute(statement, locals);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
                errors.Add($"Line {statement.Line + 1}: {inner.Message}");
            }
        }

        ApplyDesignTimeMetadata(errors);
        var render = Render();
        return render with { Errors = [.. errors, .. render.Errors] };
    }

    /// <summary>
    /// The default resources' design-time metadata the designer reads back (#67): <c>name.Locked</c> (<c>$this.Locked</c>
    /// for the root) sets that component's designer's Locked, as the classic IDE's serializers do. Only Locked: the tray's
    /// <c>TrayLocation</c> and <c>$this.Localizable</c> have their own code, and old forms' grid settings are ignored.
    /// </summary>
    private void ApplyDesignTimeMetadata(List<string> errors)
    {
        if (_textResources is null)
        {
            return;
        }

        IReadOnlyDictionary<string, object?> metadata;
        try
        {
            metadata = _textResources.Metadata();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A .resx that isn't XML: the form loads without its locks, and says why (the statements that read it say so too).
            errors.Add($"The form's .resx: {ex.Message}");
            return;
        }

        foreach (var (key, value) in metadata)
        {
            if (!key.EndsWith(".Locked", StringComparison.Ordinal) || value is not bool locked)
            {
                continue;
            }

            var name = key[..^".Locked".Length];
            try
            {
                if (Target(name == "$this" ? string.Empty : name) is IComponent component && LockedOf(component) is { IsReadOnly: false } descriptor)
                {
                    descriptor.SetValue(component, locked);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{key}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A component's design-time <c>Locked</c>: the bool, design-only property a control designer adds
    /// (<c>ControlDesigner.PreFilterProperties</c>), or null when its designer has none (a tray component, a context menu
    /// strip, a tool strip item).
    /// </summary>
    private static PropertyDescriptor? LockedOf(object component) =>
        TypeDescriptor.GetProperties(component)["Locked"] is { } descriptor && descriptor.PropertyType == typeof(bool)
        && descriptor.Attributes[typeof(DesignOnlyAttribute)] is DesignOnlyAttribute { IsDesignOnly: true } ? descriptor : null;

    /// <summary>A component's Locked as the IDE gets it: null when it has none that can be set.</summary>
    private static bool? LockedValue(object component) => LockedOf(component) is { IsReadOnly: false } descriptor ? descriptor.GetValue(component) is true : null;

    private void Unload()
    {
        if (_view is not null)
        {
            _window.Controls.Remove(_view);
        }

        _surface?.Dispose();
        _surface = null;
        _host = null;
        _root = null;
        _view = null;
        _resources = null;
        _textResources = null;
        _locals.Clear();
        _imageListVersions.Clear();
    }

    private void Execute(DesignerStatement statement, HashSet<string> locals)
    {
        switch (statement.Kind)
        {
            case DesignerStatementKinds.Create when statement.Target is "components" or "resources":
                return;
            case DesignerStatementKinds.Create when locals.Contains(statement.Target):
                _locals[statement.Target] = _values.Evaluate(statement.Value!);
                return;
            case DesignerStatementKinds.Create:
                Create(statement.Target, statement.Value!);
                return;
            case DesignerStatementKinds.Assign:
                SetPath(Target(statement.Target) ?? throw Unknown(statement.Target), statement.Member!, statement.Value!);
                return;
            case DesignerStatementKinds.Call:
            {
                var (owner, method) = Walk(Target(statement.Target) ?? throw Unknown(statement.Target), statement.Member!);
                _values.Invoke(owner, method, statement.Arguments ?? []);
                return;
            }

            case DesignerStatementKinds.Layout:
            {
                var target = Target(statement.Target) ?? throw Unknown(statement.Target);
                if (statement.Member is "BeginInit" or "EndInit")
                {
                    if (target is ISupportInitialize initialize)
                    {
                        if (statement.Member == "BeginInit")
                        {
                            initialize.BeginInit();
                        }
                        else
                        {
                            initialize.EndInit();
                        }
                    }

                    return;
                }

                var (owner, method) = Walk(target, statement.Member!);
                _values.Invoke(owner, method, statement.Arguments ?? []);
                return;
            }

            case DesignerStatementKinds.ApplyResources:
                (_resources ?? throw new InvalidOperationException("The form's resources aren't available (build the project)."))
                    .ApplyResources(Target(statement.Target) ?? throw Unknown(statement.Target), statement.Value?.Value ?? string.Empty, _culture);
                return;
            default:
                // Events aren't hooked at design time; section comments are comments.
                return;
        }
    }

    private static InvalidOperationException Unknown(string name) => new($"{name} isn't a component of the form.");

    /// <summary>A field's component: created by the designer host (so it gets its designer and is sited), with its creation's arguments when it has any beyond <c>components</c>.</summary>
    private IComponent Create(string name, DesignerValue creation)
    {
        var type = _types.Find(creation.Type ?? string.Empty) ?? throw new InvalidOperationException($"The type {creation.Type} wasn't found (build the project?).");
        var arguments = creation.Arguments ?? [];
        if (arguments.All(a => a is { Kind: DesignerValueKinds.Reference, Value: "components" }))
        {
            return Host.CreateComponent(type, name);
        }

        if (_values.Evaluate(creation) is not IComponent component)
        {
            throw new InvalidOperationException($"{name} isn't a component.");
        }

        Host.Container.Add(component, name);
        return component;
    }

    /// <summary>What a statement or a reference names: the root (empty), a component, a local, or <c>components</c>.</summary>
    // TODO(issue): resolve a part's name (notesSplit.Panel1), so the Properties window shows a split container's panel
    // (#68); its properties must then reach the state difference too.
    private object? Target(string name)
    {
        if (name.Length == 0 || name == _className)
        {
            return _root;
        }

        if (name == "components")
        {
            return _components;
        }

        if (_locals.TryGetValue(name, out var local))
        {
            return local;
        }

        return _host?.Container.Components[name];
    }

    /// <summary>A component's name (empty for the root), a local's name, or null when it isn't the form's.</summary>
    private string? NameOf(object value)
    {
        if (ReferenceEquals(value, _root))
        {
            return string.Empty;
        }

        if (value is IComponent { Site: { } site } && _host is not null && ReferenceEquals(site.Container, _host.Container))
        {
            return site.Name;
        }

        return value is IComponent ? null : _locals.FirstOrDefault(l => ReferenceEquals(l.Value, value)).Key;
    }

    private object? Resource(string key) =>
        (_resources ?? throw new InvalidOperationException("The form's resources aren't available (build the project).")).GetObject(key, _culture);

    /// <summary>The object a path's last member is on (<c>FlatAppearance</c> of <c>FlatAppearance.BorderSize</c>) and that member's name.</summary>
    private static (object Owner, string Member) Walk(object target, string path)
    {
        var parts = path.Split('.');
        var owner = target;
        foreach (var part in parts[..^1])
        {
            owner = (TypeDescriptor.GetProperties(owner)[part] is { } descriptor ? descriptor.GetValue(owner) : owner.GetType().GetProperty(part)?.GetValue(owner))
                ?? throw new InvalidOperationException($"{part} of {path} is null.");
        }

        return (owner, parts[^1]);
    }

    private void SetPath(object target, string path, DesignerValue value)
    {
        var (owner, member) = Walk(target, path);
        if (TypeDescriptor.GetProperties(owner)[member] is { } descriptor)
        {
            descriptor.SetValue(owner, _values.Evaluate(value, descriptor.PropertyType));
            return;
        }

        var property = owner.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException($"{DesignerTypes.NameOf(owner.GetType())} has no property {member}.");
        property.SetValue(owner, _values.Evaluate(value, property.PropertyType));
    }

    // ------------------------------------------------------------------ drawing

    public DesignerRenderResult Render()
    {
        var phase = Phase;
        Enter("drawing the form");
        try
        {
            return RenderCore();
        }
        finally
        {
            Enter(phase);
        }
    }

    private DesignerRenderResult RenderCore()
    {
        var root = Root;
        var errors = new List<string>();
        root.PerformLayout();
        var width = Math.Max(1, root.Width);
        var height = Math.Max(1, root.Height);
        if (_window.Width < width + 200 || _window.Height < height + 200)
        {
            _window.Size = new Size(Math.Max(_window.Width, width + 200), Math.Max(_window.Height, height + 200));
        }

        DesignerFrame? frame = null;
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            root.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
            var restacked = Restack(bitmap, root);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            frame = new DesignerFrame(Convert.ToBase64String(png.ToArray()), width, height, restacked);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            errors.Add("The form couldn't be drawn: " + ex.Message);
        }

        return new DesignerRenderResult(frame, [.. Views()], [.. errors]);
    }

    private static Point WindowOrigin(Control control) => control.Parent is { } parent ? parent.PointToScreen(control.Location) : control.Location;

    /// <summary>
    /// Draws overlapping controls again, back to front, over the first pass (#78), and says how many it drew.
    /// Control.DrawToBitmap draws a container's controls in reverse order (its documented limitation: "Controls inside
    /// containers are rendered in reverse order"), so of two overlapping siblings the front one came out behind: the
    /// 2026-10-18 smoke picture showed a button dropped on the counter control under the counter's own "+1", and Send
    /// to Back seemed to bring a control to the front. Only siblings that overlap are drawn again, each by its own
    /// DrawToBitmap copied pixel for pixel, at every depth (a group box's, a panel's, a tab page's, a split container
    /// panel's controls), placed as <see cref="Views"/> places them. A form without overlaps draws nothing more (0), so
    /// its frame is the first pass's.
    /// </summary>
    private static int Restack(Bitmap bitmap, Control root)
    {
        var origin = WindowOrigin(root);
        var client = root.PointToScreen(Point.Empty);
        Graphics? graphics = null;
        try
        {
            return Restack(bitmap, root, origin, new Rectangle(client.X - origin.X, client.Y - origin.Y, root.ClientSize.Width, root.ClientSize.Height), ref graphics);
        }
        finally
        {
            graphics?.Dispose();
        }
    }

    /// <summary>
    /// <see cref="Restack(Bitmap, Control)"/> in <paramref name="parent"/>: its visible controls from the back (the last
    /// in Controls) to the front, each one that overlaps a sibling drawn again inside the parent's client area (and
    /// <paramref name="clip"/>, its ancestors'), and each one with controls of its own looked into right after it is
    /// (drawn again or not), before the siblings in front of it. Which are drawn again, and in what order, is
    /// <see cref="Restacking.BackToFront"/>.
    /// </summary>
    private static int Restack(Bitmap bitmap, Control parent, Point origin, Rectangle clip, ref Graphics? graphics)
    {
        var children = new List<Control>();
        var bounds = new List<Rectangle>();
        foreach (Control child in parent.Controls)
        {
            if (child.Visible && child.Width > 0 && child.Height > 0)
            {
                var at = WindowOrigin(child);
                children.Add(child);
                bounds.Add(new Rectangle(at.X - origin.X, at.Y - origin.Y, child.Width, child.Height));
            }
        }

        var client = parent.PointToScreen(Point.Empty);
        var area = Rectangle.Intersect(clip, new Rectangle(client.X - origin.X, client.Y - origin.Y, parent.ClientSize.Width, parent.ClientSize.Height));
        if (children.Count == 0 || area.IsEmpty)
        {
            return 0;
        }

        var restacked = 0;
        foreach (var (index, redraw) in Restacking.BackToFront(bounds, area))
        {
            var child = children[index];
            if (redraw && Redraw(bitmap, child, bounds[index], area, ref graphics))
            {
                restacked++;
            }

            if (child.Controls.Count > 0)
            {
                restacked += Restack(bitmap, child, origin, area, ref graphics);
            }
        }

        return restacked;
    }

    /// <summary>
    /// Draws <paramref name="child"/> again at <paramref name="bounds"/>, inside <paramref name="area"/>, as the first pass
    /// drew it (the child's own WM_PRINT), copied without scaling or blending: a transparent control shows its parent, not
    /// the siblings behind it, as at run time. A child that can't be drawn alone (GDI+ refuses a bitmap that large, or
    /// runs out of memory for it) keeps the first pass's pixels, out of order, and the frame stays; false then.
    /// </summary>
    private static bool Redraw(Bitmap bitmap, Control child, Rectangle bounds, Rectangle area, ref Graphics? graphics)
    {
        try
        {
            using var drawn = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            child.DrawToBitmap(drawn, new Rectangle(0, 0, bounds.Width, bounds.Height));
            graphics ??= Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SetClip(area);
            graphics.DrawImage(drawn, bounds, 0, 0, bounds.Width, bounds.Height, GraphicsUnit.Pixel);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            Console.Error.WriteLine($"[designer] {child.Name} ({bounds.Width}x{bounds.Height}) couldn't be drawn again in its order: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The root, its controls (front first, parents before children; a SplitContainer's panels as parts), the tool
    /// strips' items, then the other components (the tray's).
    /// </summary>
    private IEnumerable<DesignerComponentView> Views()
    {
        var root = Root;
        var origin = WindowOrigin(root);
        DesignerComponentView View(Control control, string name, string? parent, bool isPart)
        {
            var at = WindowOrigin(control);
            var client = control.PointToScreen(Point.Empty);
            var isContainer = isPart || Host.GetDesigner(control) is ParentControlDesigner;
            return new DesignerComponentView(name, DesignerTypes.NameOf(control.GetType()), parent, at.X - origin.X, at.Y - origin.Y, control.Width, control.Height)
            {
                IsControl = true,
                IsContainer = isContainer,
                ClientX = client.X - origin.X,
                ClientY = client.Y - origin.Y,
                Visible = true,
                DefaultEvent = isPart ? null : TypeDescriptor.GetDefaultEvent(control)?.Name,
                IsPart = isPart,
                TabIndex = isPart || ReferenceEquals(control, root) ? null : control.TabIndex,
                Dock = isPart ? null : control.Dock.ToString(),
                Anchor = isPart ? null : TypeDescriptor.GetConverter(typeof(AnchorStyles)).ConvertToInvariantString(control.Anchor),
                Margin = [control.Margin.Left, control.Margin.Top, control.Margin.Right, control.Margin.Bottom],
                Padding = isContainer ? SnapPadding(control) : null,
                Baseline = isPart ? null : Baseline(control),
                HasActions = !isPart && HasActions(control),
                Collections = isPart ? null : CollectionKeys(control),
                Text = isPart ? null : control.Text,
                InTray = control is ToolStrip and not ToolStripDropDown,
                Locked = isPart ? null : LockedValue(control),
            };
        }

        yield return View(root, _className, null, isPart: false);
        var queue = new List<DesignerComponentView>();
        var strips = new List<(ToolStrip Strip, string Name, bool Visible)>();
        void Children(Control parent, string parentName)
        {
            foreach (Control child in parent.Controls)
            {
                string? name = NameOf(child);
                var isPart = false;
                if (name is null && NameOf(parent) is { } owner && PartName(parent, child) is { } path)
                {
                    // A container's own panel (a SplitContainer's, a ToolStripContainer's): a part that holds controls.
                    name = (owner.Length == 0 ? _className : owner) + "." + path;
                    isPart = true;
                }

                if (name is null)
                {
                    continue;
                }

                queue.Add(View(child, name, parentName.Length == 0 ? _className : parentName, isPart));
                if (child is ToolStrip strip && !isPart)
                {
                    strips.Add((strip, name, true));
                }

                Children(child, name);
            }
        }

        Children(root, string.Empty);
        foreach (var view in queue)
        {
            yield return view;
        }

        foreach (IComponent component in Host.Container.Components)
        {
            if (component is ToolStrip { Parent: null } unparented && NameOf(unparented) is { Length: > 0 } menu)
            {
                strips.Add((unparented, menu, false));
            }
        }

        foreach (var (strip, name, visible) in strips)
        {
            foreach (var item in Items(strip.Items, name, visible, origin))
            {
                yield return item;
            }
        }

        foreach (IComponent component in Host.Container.Components)
        {
            if (ReferenceEquals(component, root) || component.Site is not { } site || component is ToolStripItem)
            {
                continue;
            }

            if (component is Control control && control.Parent is not null)
            {
                continue;
            }

            if (component is not Control && !TypeDescriptor.GetAttributes(component).Contains(DesignTimeVisibleAttribute.Yes))
            {
                continue;
            }

            yield return new DesignerComponentView(site.Name ?? string.Empty, DesignerTypes.NameOf(component.GetType()), null, 0, 0, 0, 0)
            {
                IsControl = component is Control,
                DefaultEvent = TypeDescriptor.GetDefaultEvent(component)?.Name,
                HasActions = HasActions(component),
                Collections = CollectionKeys(component),
                InTray = true,
                Visible = false,
                Locked = LockedValue(component),
            };
        }
    }

    /// <summary>A strip's items (and their drop-downs' items, which aren't drawn: the IDE draws them), with their bounds when the strip shows on the form.</summary>
    private IEnumerable<DesignerComponentView> Items(ToolStripItemCollection items, string owner, bool visible, Point origin)
    {
        var index = 0;
        foreach (ToolStripItem item in items)
        {
            if (NameOf(item) is not { Length: > 0 } name)
            {
                continue;
            }

            var shown = visible && item.Owner is { Visible: true } && item.Visible;
            var at = shown ? item.Owner!.PointToScreen(item.Bounds.Location) : Point.Empty;
            yield return new DesignerComponentView(name, DesignerTypes.NameOf(item.GetType()), owner,
                shown ? at.X - origin.X : 0, shown ? at.Y - origin.Y : 0, shown ? item.Bounds.Width : 0, shown ? item.Bounds.Height : 0)
            {
                IsItem = true,
                Owner = owner,
                ItemIndex = index++,
                Text = item.Text,
                Visible = shown,
                DefaultEvent = TypeDescriptor.GetDefaultEvent(item)?.Name,
                HasDropDown = item is ToolStripDropDownItem,
                HasActions = HasActions(item),
                Collections = CollectionKeys(item),
            };
            if (item is ToolStripDropDownItem dropDown)
            {
                foreach (var child in Items(dropDown.DropDownItems, name, visible: false, origin))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>The padding the snap lines keep inside a container: its Padding; a form's empty one is the designer's 9 (with a control's margin of 3, Visual Studio's 12).</summary>
    private int[] SnapPadding(Control control)
    {
        var padding = control.Padding;
        if (ReferenceEquals(control, _root) && padding == Padding.Empty)
        {
            return [9, 9, 9, 9];
        }

        return [padding.Left, padding.Top, padding.Right, padding.Bottom];
    }

    /// <summary>The control's text baseline from its top, from its designer's snap lines (labels, text boxes, buttons).</summary>
    private double? Baseline(Control control)
    {
        try
        {
            if (Host.GetDesigner(control) is ControlDesigner designer)
            {
                foreach (var line in designer.SnapLines.OfType<SnapLine>())
                {
                    if (line.SnapLineType == SnapLineType.Baseline)
                    {
                        return line.Offset;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{control.Name}'s snap lines couldn't be read: {ex.Message}");
        }

        return null;
    }

    // ------------------------------------------------------------------ changes

    /// <summary>A parent named by the IDE: the root (null or empty), a container component, or a container's panel (<c>splitContainer1.Panel1</c>).</summary>
    private (Control Control, string Component, string? Path) Parent(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == _className)
        {
            return (Root, string.Empty, null);
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var component = dot < 0 ? name : name[..dot];
        var path = dot < 0 ? null : name[(dot + 1)..];
        var owner = Target(component) as Control ?? throw new InvalidOperationException($"{component} isn't a control.");
        var control = path is null ? owner : owner.GetType().GetProperty(path)?.GetValue(owner) as Control ?? throw new InvalidOperationException($"{name} isn't a control.");
        if (path is null && Host.GetDesigner(owner) is not ParentControlDesigner)
        {
            throw new InvalidOperationException($"{name} can't contain controls.");
        }

        return (control, component, path);
    }

    /// <summary>A point of the frame in a control's client coordinates.</summary>
    private Point ClientPoint(Control control, double x, double y)
    {
        var origin = WindowOrigin(Root);
        var client = control.PointToScreen(Point.Empty);
        return new Point((int)Math.Round(x) - (client.X - origin.X), (int)Math.Round(y) - (client.Y - origin.Y));
    }

    /// <summary>Adds <paramref name="control"/> to <paramref name="parent"/> at the front, in the TableLayoutPanel cell under the point.</summary>
    private static int[]? AddTo(Control parent, Control control, Point at)
    {
        if (parent is TableLayoutPanel table)
        {
            var cell = CellAt(table, at);
            table.Controls.Add(control, cell[0], cell[1]);
            control.BringToFront();
            return cell;
        }

        parent.Controls.Add(control);
        control.BringToFront();
        if (parent is not FlowLayoutPanel)
        {
            control.Location = at;
        }

        return null;
    }

    private static int[] CellAt(TableLayoutPanel table, Point at)
    {
        static int Index(int[] sizes, int position)
        {
            var sum = 0;
            for (var i = 0; i < sizes.Length; i++)
            {
                sum += sizes[i];
                if (position < sum)
                {
                    return i;
                }
            }

            return Math.Max(0, sizes.Length - 1);
        }

        return [Index(table.GetColumnWidths(), at.X), Index(table.GetRowHeights(), at.Y)];
    }

    /// <summary>
    /// A Toolbox item, created as the classic IDE creates it (#66), for every gesture: the type's Toolbox item, named
    /// as the IDE chose, created by the container designer's <c>CreateToolCore</c>. A drop or a drawing goes to the
    /// designer of the container under the point, at that point (in screen coordinates, as the drag and drop handler
    /// takes it) and drawn size; the Toolbox's double-click or Enter goes to the selection's container (as
    /// DocumentDesigner's tool pick does: the primary selection, else the first selected control, walked up to a
    /// container designer, else the form's) at its default place. The container may route it (a tab control to its
    /// selected page, a split container to its panel), and the designers' defaults apply (a label sizes itself to its
    /// text, a split container fills an empty container, the first menu strip becomes the form's main menu);
    /// components that aren't controls go to the tray. What a designer refused comes back as the change's error.
    /// </summary>
    public DesignerChangeResult Create(DesignerCreateParams parameters)
    {
        var before = Capture();
        var type = _types.Find(parameters.Type) ?? throw new InvalidOperationException($"The type {parameters.Type} wasn't found (build the project?).");
        if (Host.Container.Components[parameters.Name] is not null)
        {
            throw new InvalidOperationException($"The form already has a component named {parameters.Name}.");
        }

        var tool = ToolInvoker.ToolFor(type, _types);
        ParentControlDesigner designer;
        Point screen = default;
        Size size = default;
        var hasSize = false;
        if (parameters.AtDefaultLocation)
        {
            designer = SelectedParentDesigner(parameters.Selected);
        }
        else
        {
            var (parent, _, _) = Parent(parameters.Parent);
            designer = Host.GetDesigner(parent) as ParentControlDesigner ?? throw new InvalidOperationException($"{parameters.Parent} can't contain controls.");
            var origin = WindowOrigin(Root);
            screen = new Point(origin.X + (int)Math.Round(parameters.X), origin.Y + (int)Math.Round(parameters.Y));
            if (parameters is { Width: { } width, Height: { } height } && width >= 1 && height >= 1)
            {
                size = new Size((int)Math.Round(width), (int)Math.Round(height));
                hasSize = true;
            }
        }

        _ui.Clear();
        _pendingName = (parameters.Name, type);
        string? failure = null;
        try
        {
            ToolInvoker.Create(designer, tool, screen.X, screen.Y, size.Width, size.Height, hasLocation: !parameters.AtDefaultLocation, hasSize);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex.Message;
            Console.Error.WriteLine($"[designer] {parameters.Type} couldn't be created: {ex}");
        }
        finally
        {
            _pendingName = null;
        }

        var component = Host.Container.Components[parameters.Name] ?? Host.Container.Components.Cast<IComponent>().FirstOrDefault(c => !before.Components.ContainsKey(c.Site?.Name ?? string.Empty) && c.GetType() == type);
        if (component is null)
        {
            var reason = _ui.Messages.Count > 0 ? string.Join(" ", _ui.Messages) : failure ?? $"The designer didn't create {type.Name}.";
            return Result(before) with { Error = reason };
        }

        if (component.Site is { } site && site.Name != parameters.Name)
        {
            // A Toolbox item that named it itself: the IDE's name, which the code will use.
            site.Name = parameters.Name;
        }

        if (component is Control control and not ToolStripDropDown && control.Parent is { } parentControl)
        {
            if (!parameters.AtDefaultLocation && parentControl is TableLayoutPanel table)
            {
                // A drop on a table: the cell under the point (a double-click takes the table's first free cell).
                var cell = CellAt(table, ClientPoint(table, parameters.X, parameters.Y));
                table.SetCellPosition(control, new TableLayoutPanelCellPosition(cell[0], cell[1]));
            }

            var siblings = parentControl.Controls.Cast<Control>().Where(c => !ReferenceEquals(c, control)).ToList();
            control.TabIndex = siblings.Count == 0 ? 0 : siblings.Max(c => c.TabIndex) + 1;
        }

        // TODO(issue): insert a control dropped on a FlowLayoutPanel at the insertion point under the cursor, as the classic FlowLayoutPanelDesigner does.
        return Result(before, created: parameters.Name);
    }

    /// <summary>
    /// The container designer a Toolbox double-click or Enter creates in, as DocumentDesigner's tool pick finds it: the
    /// primary selection if it is a control, else the first selected control, walked up to the first control whose
    /// designer takes controls (no CanParent filter); else the form's. The design surface's selection becomes the IDE's,
    /// since the container designer places the control by its primary selection.
    /// </summary>
    private ParentControlDesigner SelectedParentDesigner(IReadOnlyList<string> selected)
    {
        var components = new List<IComponent>();
        foreach (var name in selected)
        {
            IComponent? component;
            try
            {
                component = name.Contains('.', StringComparison.Ordinal) ? Parent(name).Control : Target(name) as IComponent;
            }
            catch (InvalidOperationException)
            {
                component = null;
            }

            if (component is not null && !components.Contains(component))
            {
                components.Add(component);
            }
        }

        if (_surface?.GetService(typeof(ISelectionService)) is ISelectionService selection)
        {
            selection.SetSelectedComponents(components.Count > 0 ? components.ToArray() : [Root], SelectionTypes.Replace);
        }

        var start = components.FirstOrDefault() as Control ?? components.OfType<Control>().FirstOrDefault();
        for (var control = start; control is not null; control = ReferenceEquals(control, Root) ? null : control.Parent)
        {
            if (Host.GetDesigner(control) is ParentControlDesigner designer)
            {
                return designer;
            }
        }

        return Host.GetDesigner(Root) as ParentControlDesigner ?? throw new InvalidOperationException("The form's designer can't contain controls.");
    }

    public DesignerChangeResult SetProperties(DesignerSetPropertiesParams parameters)
    {
        var before = Capture();
        foreach (var edit in parameters.Edits)
        {
            if (edit.Component.Length == 0 && edit.Property == "Localizable" && edit.Provider is null)
            {
                return SetLocalizable(before, string.Equals(edit.Text, "True", StringComparison.OrdinalIgnoreCase));
            }

            var target = Target(edit.Component) ?? throw Unknown(edit.Component);
            if (edit.Property == "Locked" && edit.Provider is null && !edit.DataBinding && LockedOf(target) is not { IsReadOnly: false })
            {
                // Lock Controls over a form with a context menu strip or an inherited control (#67): skipped, as the classic
                // IDE's OnMenuLockControls skips a component without a Locked it can set.
                continue;
            }

            if (edit.DataBinding)
            {
                if (SetDataBinding(target, edit) is { } bindingError)
                {
                    return new DesignerChangeResult([], Render()) { Error = bindingError };
                }

                continue;
            }

            if (edit.Provider is null && edit.Bytes is null && edit.Property.Contains('.', StringComparison.Ordinal))
            {
                // A part (#68: Font.Bold, Location.X, Padding.Left, FlatAppearance.BorderSize): the whole value set again.
                if (SetSubProperty(target, edit) is { } subError)
                {
                    return new DesignerChangeResult([], Render()) { Error = subError };
                }

                continue;
            }

            var (owner, member) = Walk(target, edit.Property);
            var descriptor = Descriptor(owner, member, edit.Provider)
                ?? throw new InvalidOperationException(edit.Provider is null ? $"{edit.Property} isn't a property of {edit.Component}." : $"{edit.Provider} doesn't give {edit.Component} a {edit.Property}.");
            if (edit.Reset)
            {
                if (descriptor.CanResetValue(owner))
                {
                    descriptor.ResetValue(owner);
                }

                continue;
            }

            object? value;
            try
            {
                // Converted with the surface's context (#68), so a component's name resolves (AcceptButton = "cancelButton").
                value = edit.Bytes is { } bytes ? ImageOf(descriptor.PropertyType, Convert.FromBase64String(bytes))
                    : edit.Value is { } structured ? _values.Evaluate(structured, descriptor.PropertyType)
                    : descriptor.Converter.ConvertFrom(Context(owner, descriptor), CultureInfo.InvariantCulture, edit.Text ?? string.Empty);
            }
            catch (Exception ex) when (IsConversionError(ex))
            {
                return new DesignerChangeResult([], Render()) { Error = ConversionMessage(ex) };
            }

            descriptor.SetValue(owner, value);
        }

        return Result(before);
    }

    /// <summary>A property of <paramref name="owner"/>, or the extender property <paramref name="provider"/> gives it (<c>ToolTip on toolTip1</c>).</summary>
    private PropertyDescriptor? Descriptor(object owner, string member, string? provider)
    {
        if (provider is null)
        {
            return TypeDescriptor.GetProperties(owner)[member];
        }

        return TypeDescriptor.GetProperties(owner).Cast<PropertyDescriptor>().FirstOrDefault(d => d.Name == member
            && d.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is ExtenderProvidedPropertyAttribute { Provider: { } p } && NameOf(p) == provider);
    }

    /// <summary>An image file's bytes as the property's type: an icon for <c>Icon</c>, else an image (its stream kept, so it keeps its format).</summary>
    private static object ImageOf(Type type, byte[] bytes) =>
        typeof(Icon).IsAssignableFrom(type) ? new Icon(new MemoryStream(bytes)) : Image.FromStream(new MemoryStream(bytes));

    public DesignerChangeResult SetParent(DesignerSetParentParams parameters)
    {
        var before = Capture();
        var control = Target(parameters.Component) as Control ?? throw new InvalidOperationException($"{parameters.Component} isn't a control.");
        var (parent, _, _) = Parent(parameters.Parent);
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, control))
            {
                throw new InvalidOperationException($"{parameters.Component} can't go inside itself.");
            }
        }

        control.Parent?.Controls.Remove(control);
        AddTo(parent, control, ClientPoint(parent, parameters.X, parameters.Y));
        return Result(before, reparented: parameters.Component);
    }

    /// <summary>Moves a control in its parent's controls (M41b): 0 brings it to the front, the last index sends it to the back.</summary>
    public DesignerChangeResult SetChildIndex(DesignerChildIndexParams parameters)
    {
        var before = Capture();
        var control = Target(parameters.Component) as Control ?? throw new InvalidOperationException($"{parameters.Component} isn't a control.");
        var parent = control.Parent ?? throw new InvalidOperationException($"{parameters.Component} has no parent.");
        parent.Controls.SetChildIndex(control, Math.Clamp(parameters.Index, 0, parent.Controls.Count - 1));
        return Result(before);
    }

    public DesignerChangeResult Delete(DesignerComponentsParams parameters)
    {
        var before = Capture();
        var removed = new List<IComponent>();
        void Collect(IComponent component)
        {
            if (removed.Contains(component))
            {
                return;
            }

            if (component is Control control)
            {
                foreach (Control child in control.Controls)
                {
                    if (NameOf(child) is { Length: > 0 })
                    {
                        Collect(child);
                    }
                    else if (PartName(control, child) is not null)
                    {
                        foreach (Control grandchild in child.Controls)
                        {
                            if (NameOf(grandchild) is { Length: > 0 })
                            {
                                Collect(grandchild);
                            }
                        }
                    }
                }
            }

            // A strip's items, a menu item's drop-down's, a list view's columns, a grid's columns.
            foreach (var (_, collection) in CollectionsOf(component))
            {
                foreach (var item in ((IEnumerable)collection).Cast<object>().ToList())
                {
                    if (item is IComponent child && !ReferenceEquals(child, component) && NameOf(child) is { Length: > 0 })
                    {
                        Collect(child);
                    }
                }
            }

            removed.Add(component);
        }

        foreach (var name in parameters.Components)
        {
            if (Target(name) is IComponent component && !ReferenceEquals(component, _root) && NameOf(component) is { Length: > 0 })
            {
                Collect(component);
            }
        }

        foreach (var component in removed)
        {
            if (component.Site is not null)
            {
                Host.DestroyComponent(component);
            }
        }

        return Result(before);
    }

    public void Select(DesignerComponentsParams parameters)
    {
        if (_surface?.GetService(typeof(ISelectionService)) is ISelectionService selection)
        {
            selection.SetSelectedComponents(parameters.Components.Select(n => Target(n.Split('.')[0])).OfType<IComponent>().ToArray(), SelectionTypes.Replace);
        }
    }

    public void Dispose()
    {
        Unload();
        _components.Dispose();
        _services.Dispose();
    }

    // ------------------------------------------------------------------ data bindings (M41b)

    /// <summary>Binds a property to a binding source's member (<c>bindingSource1 - Name</c>), or unbinds it (<c>(none)</c>).</summary>
    private string? SetDataBinding(object target, DesignerPropertyEdit edit)
    {
        if (target is not Control control)
        {
            return $"{edit.Component} isn't a control: it has no data bindings.";
        }

        if (control.DataBindings[edit.Property] is { } old)
        {
            control.DataBindings.Remove(old);
        }

        if (edit.Reset || edit.Text is not { } text || text == NoBinding)
        {
            return null;
        }

        var separator = text.IndexOf(BindingSeparator, StringComparison.Ordinal);
        if (separator < 0 || Target(text[..separator]) is not IComponent source)
        {
            return $"'{text}' isn't a binding source's member.";
        }

        control.DataBindings.Add(new Binding(edit.Property, source, text[(separator + BindingSeparator.Length)..], true));
        return null;
    }

    private const string NoBinding = "(none)";
    private const string BindingSeparator = " - ";

    /// <summary>The choices of a property's binding: none, and every binding source's members (<c>bindingSource1 - Name</c>).</summary>
    private string[] BindingChoices()
    {
        var choices = new List<string> { NoBinding };
        foreach (IComponent component in Host.Container.Components)
        {
            if (component is not (BindingSource or IListSource or IList) || NameOf(component) is not { Length: > 0 } name)
            {
                continue;
            }

            try
            {
                foreach (PropertyDescriptor member in ListBindingHelper.GetListItemProperties(component))
                {
                    choices.Add(name + BindingSeparator + member.Name);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"{name}'s members couldn't be listed: {ex.Message}");
            }
        }

        return [.. choices];
    }
}
