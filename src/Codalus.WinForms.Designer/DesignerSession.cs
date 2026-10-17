using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
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

    public DesignerSession(HostWindow window)
    {
        _window = window;
        _values = new DesignerValues(_types, Target, NameOf, Resource);
        _services.AddService(typeof(INameCreationService), new NameCreation(this));
    }

    private IDesignerHost Host => _host ?? throw new InvalidOperationException("No form is loaded.");

    private Control Root => _root ?? throw new InvalidOperationException("No form is loaded.");

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
        Unload();
        var errors = new List<string>();
        _searchFolders = [.. parameters.SearchFolders.Append(Path.GetDirectoryName(parameters.ProjectAssembly)!).Distinct(StringComparer.OrdinalIgnoreCase)];
        Assembly? project = null;
        if (File.Exists(parameters.ProjectAssembly))
        {
            project = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => !a.IsDynamic && string.Equals(a.Location, parameters.ProjectAssembly, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(parameters.ProjectAssembly);
            _types.Add(project);
        }
        else
        {
            errors.Add($"{Path.GetFileName(parameters.ProjectAssembly)} isn't built: the project's own types and resources aren't available.");
        }

        var model = parameters.Model;
        var rootType = _types.Find(model.BaseType) ?? throw new InvalidOperationException($"The base type {model.BaseType} wasn't found (build the project).");
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
                Execute(statement, locals);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
                errors.Add($"Line {statement.Line + 1}: {inner.Message}");
            }
        }

        var render = Render();
        return render with { Errors = [.. errors, .. render.Errors] };
    }

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
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            frame = new DesignerFrame(Convert.ToBase64String(png.ToArray()), width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            errors.Add("The form couldn't be drawn: " + ex.Message);
        }

        return new DesignerRenderResult(frame, [.. Views()], [.. errors]);
    }

    private static Point WindowOrigin(Control control) => control.Parent is { } parent ? parent.PointToScreen(control.Location) : control.Location;

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

    public DesignerChangeResult Create(DesignerCreateParams parameters)
    {
        var before = Capture();
        var type = _types.Find(parameters.Type) ?? throw new InvalidOperationException($"The type {parameters.Type} wasn't found (build the project?).");
        if (Host.Container.Components[parameters.Name] is not null)
        {
            throw new InvalidOperationException($"The form already has a component named {parameters.Name}.");
        }

        var component = Host.CreateComponent(type, parameters.Name);
        if (Host.GetDesigner(component) is IComponentInitializer initializer)
        {
            // As the Toolbox's creation does: the designer sets its defaults (a button's Text is its name).
            initializer.InitializeNewComponent(new Hashtable());
        }

        if (component is Control control and not ToolStripDropDown)
        {
            var (parent, _, _) = Parent(parameters.Parent);
            var siblings = parent.Controls.Cast<Control>().Where(c => !ReferenceEquals(c, control)).ToList();
            AddTo(parent, control, ClientPoint(parent, parameters.X, parameters.Y));
            if (parameters is { Width: { } width, Height: { } height } && width >= 1 && height >= 1)
            {
                control.Size = new Size((int)Math.Round(width), (int)Math.Round(height));
            }

            control.TabIndex = siblings.Count == 0 ? 0 : siblings.Max(c => c.TabIndex) + 1;
        }

        return Result(before, created: parameters.Name);
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
            if (edit.DataBinding)
            {
                if (SetDataBinding(target, edit) is { } bindingError)
                {
                    return new DesignerChangeResult([], Render()) { Error = bindingError };
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
                value = edit.Bytes is { } bytes ? ImageOf(descriptor.PropertyType, Convert.FromBase64String(bytes))
                    : edit.Value is { } structured ? _values.Evaluate(structured, descriptor.PropertyType)
                    : descriptor.Converter.ConvertFromInvariantString(edit.Text ?? string.Empty);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or InvalidCastException or OverflowException or System.Runtime.InteropServices.ExternalException)
            {
                return new DesignerChangeResult([], Render()) { Error = $"'{edit.Text ?? edit.Property}' isn't a valid {edit.Property}: {ex.Message}" };
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

    // ------------------------------------------------------------------ the Properties window

    public DesignerPropertiesResult Properties(DesignerComponentsParams parameters)
    {
        var targets = parameters.Components.Select(n =>
        {
            var dot = n.IndexOf('.', StringComparison.Ordinal);
            var target = Target(dot < 0 ? n : n[..dot]) ?? throw Unknown(n);
            return dot < 0 ? target : Walk(target, n[(dot + 1)..] + ".x").Owner;
        }).ToList();
        if (targets.Count == 0)
        {
            return new DesignerPropertiesResult([]);
        }

        List<DesignerPropertyInfo>? merged = null;
        foreach (var target in targets)
        {
            var infos = Describe(target, NameOf(target)).ToList();
            merged = merged is null
                ? infos
                : [.. merged.Join(infos, a => (a.Name, a.Type, a.Provider, a.DataBinding), b => (b.Name, b.Type, b.Provider, b.DataBinding),
                    (a, b) => a.Value == b.Value ? a with { IsSet = a.IsSet || b.IsSet } : a with { Value = null, Mixed = true, IsSet = a.IsSet || b.IsSet })];
        }

        return new DesignerPropertiesResult([.. merged!]);
    }

    private IEnumerable<DesignerPropertyInfo> Describe(object target, string? name)
    {
        if (target is IComponent && name is not null)
        {
            yield return new DesignerPropertyInfo("Name", "Design", "System.String", name.Length == 0 ? _className : name)
            {
                Description = "The name of the component in the code.",
                IsSet = true,
                IsReadOnly = name.Length == 0,
            };
        }

        if (ReferenceEquals(target, _root))
        {
            // Visual Studio's design-time property of a form: its localizable values go to its resources, per language.
            yield return new DesignerPropertyInfo("Localizable", "Design", "System.Boolean", _localizable ? "True" : "False")
            {
                Description = "The form's localizable properties (texts, sizes, locations, images…) are in its resources, a .resx per language.",
                IsSet = _localizable,
                Editor = DesignerEditors.Boolean,
                StandardValues = ["True", "False"],
            };
        }

        var collections = CollectionsOf(target).ToDictionary(c => c.Info.Property, c => c.Info.Key, StringComparer.Ordinal);
        foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(target, [BrowsableAttribute.Yes]))
        {
            if (descriptor.Name == "Name")
            {
                continue;
            }

            string? provider = null;
            if (descriptor.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is ExtenderProvidedPropertyAttribute { Provider: { } extender })
            {
                if (NameOf(extender) is not { Length: > 0 } providerName)
                {
                    continue;
                }

                provider = providerName;
            }

            DesignerPropertyInfo info;
            try
            {
                info = Describe(target, descriptor);
                if (collections.TryGetValue(descriptor.Name, out var key) && provider is null)
                {
                    info = info with { Editor = DesignerEditors.Collection, Collection = key, Value = "(Collection)", IsReadOnly = false };
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                info = new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(descriptor.PropertyType), ex.Message) { IsReadOnly = true, Editor = DesignerEditors.ReadOnly };
            }

            yield return info with { Provider = provider, Localizable = _localizable && descriptor.IsLocalizable };
        }

        // (DataBindings): the bindable properties, bound to a binding source's member.
        if (target is Control control && NameOf(control) is { Length: > 0 })
        {
            string[]? choices = null;
            foreach (PropertyDescriptor bindable in TypeDescriptor.GetProperties(control, [BindableAttribute.Yes]))
            {
                choices ??= BindingChoices();
                var binding = control.DataBindings[bindable.Name];
                var current = binding?.DataSource is IComponent source && NameOf(source) is { } sourceName
                    ? sourceName + BindingSeparator + binding.BindingMemberInfo.BindingMember
                    : NoBinding;
                yield return new DesignerPropertyInfo(bindable.Name, "(DataBindings)", DesignerTypes.NameOf(bindable.PropertyType), current)
                {
                    Description = $"Binds {bindable.Name} to a member of one of the form's binding sources.",
                    IsSet = binding is not null,
                    Editor = DesignerEditors.Enum,
                    StandardValues = choices.Contains(current) ? choices : [.. choices, current],
                    DataBinding = true,
                };
            }
        }
    }

    private static DesignerPropertyInfo Describe(object target, PropertyDescriptor descriptor)
    {
        var type = Nullable.GetUnderlyingType(descriptor.PropertyType) ?? descriptor.PropertyType;
        var converter = descriptor.Converter;
        var value = descriptor.GetValue(target);
        string? text;
        try
        {
            text = value is null ? string.Empty : converter.ConvertToInvariantString(value);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            text = value?.ToString();
        }

        if (!descriptor.IsReadOnly && (typeof(Image).IsAssignableFrom(type) || typeof(Icon).IsAssignableFrom(type)))
        {
            // An image: imported from a file into the resources (the editor shows its type, as Visual Studio does).
            return new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(type), value is null ? "(none)" : DesignerTypes.NameOf(value.GetType()))
            {
                Description = descriptor.Description,
                IsSet = descriptor.ShouldSerializeValue(target),
                Editor = DesignerEditors.Image,
            };
        }

        var settable = !descriptor.IsReadOnly && converter.CanConvertFrom(typeof(string)) && descriptor.SerializationVisibility != DesignerSerializationVisibility.Content;
        var editor = !settable ? DesignerEditors.ReadOnly
            : type == typeof(bool) ? DesignerEditors.Boolean
            : type == typeof(AnchorStyles) ? DesignerEditors.Anchor
            : type == typeof(DockStyle) ? DesignerEditors.Dock
            : type.IsEnum && type.IsDefined(typeof(FlagsAttribute), inherit: false) ? DesignerEditors.Flags
            : type.IsEnum ? DesignerEditors.Enum
            : type == typeof(Color) ? DesignerEditors.Color
            : type == typeof(Font) ? DesignerEditors.Font
            : DesignerEditors.Text;
        string[]? standard = null;
        if (settable && converter.GetStandardValuesSupported(null) && converter.GetStandardValues(null) is { Count: > 0 and <= 400 } values)
        {
            standard = [.. values.Cast<object?>().Select(v => v is null ? string.Empty : converter.ConvertToInvariantString(v) ?? string.Empty)];
        }

        if (type.IsEnum && editor is DesignerEditors.Flags or DesignerEditors.Anchor)
        {
            standard = System.Enum.GetNames(type);
        }

        return new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(type), text)
        {
            Description = descriptor.Description,
            IsReadOnly = !settable,
            IsSet = ShouldSerialize(descriptor, target),
            Editor = editor,
            StandardValues = standard,
        };
    }

    public DesignerEventsResult Events(DesignerComponentsParams parameters)
    {
        var target = Target(parameters.Components.FirstOrDefault() ?? string.Empty) ?? throw new InvalidOperationException("No component.");
        var events = new List<DesignerEventInfo>();
        foreach (EventDescriptor descriptor in TypeDescriptor.GetEvents(target, [BrowsableAttribute.Yes]))
        {
            var invoke = descriptor.EventType.GetMethod("Invoke");
            var parametersList = invoke?.GetParameters().Select(p => $"{DesignerTypes.NameOf(p.ParameterType)} {p.Name}").ToArray() ?? [];
            events.Add(new DesignerEventInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(descriptor.EventType), parametersList) { Description = descriptor.Description });
        }

        return new DesignerEventsResult([.. events.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)], TypeDescriptor.GetDefaultEvent(target)?.Name);
    }
}
