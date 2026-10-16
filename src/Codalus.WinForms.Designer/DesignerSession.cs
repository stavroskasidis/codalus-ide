using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms.Design;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// One form on Windows Forms' own design surface (M41a, ADR 0073). <see cref="Load"/> creates the root (an instance of
/// the class's base type, as Visual Studio's designer does) and the components, and runs InitializeComponent's statements
/// against them through the designers (so a designed control is in design mode). A change sets properties through
/// their property descriptors, and what it changed comes back as code: every component's serializable properties are
/// compared before and after, so a docking or a tab order that moves its siblings is written too.
/// </summary>
internal sealed class DesignerSession : IDisposable
{
    private readonly HostWindow _window;
    private readonly DesignerTypes _types = new();
    private readonly Dictionary<string, object?> _locals = new(StringComparer.Ordinal);
    private readonly System.ComponentModel.Container _components = new();
    private readonly DesignerValues _values;
    private DesignSurface? _surface;
    private IDesignerHost? _host;
    private Control? _root;
    private Control? _view;
    private ComponentResourceManager? _resources;
    private string _className = string.Empty;
    private string[] _searchFolders = [];

    public DesignerSession(HostWindow window)
    {
        _window = window;
        _values = new DesignerValues(_types, Target, NameOf, Resource);
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
        _surface = new DesignSurface();
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

        if (parameters.ClassFullName is { } className && project?.GetType(className, throwOnError: false) is { } classType)
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
        _locals.Clear();
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
                    .ApplyResources(Target(statement.Target) ?? throw Unknown(statement.Target), statement.Value?.Value ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture);
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

    private string? NameOf(object value)
    {
        if (ReferenceEquals(value, _root))
        {
            return string.Empty;
        }

        return value is IComponent { Site: { } site } && _host is not null && ReferenceEquals(site.Container, _host.Container) ? site.Name : null;
    }

    private object? Resource(string key) =>
        (_resources ?? throw new InvalidOperationException("The form's resources aren't available (build the project).")).GetObject(key, System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>The root, its controls (front first, parents before children; a SplitContainer's panels as parts), then the other components.</summary>
    private IEnumerable<DesignerComponentView> Views()
    {
        var root = Root;
        var origin = WindowOrigin(root);
        DesignerComponentView View(Control control, string name, string? parent, bool isPart)
        {
            var at = WindowOrigin(control);
            var client = control.PointToScreen(Point.Empty);
            return new DesignerComponentView(name, DesignerTypes.NameOf(control.GetType()), parent, at.X - origin.X, at.Y - origin.Y, control.Width, control.Height)
            {
                IsControl = true,
                IsContainer = isPart || Host.GetDesigner(control) is ParentControlDesigner,
                ClientX = client.X - origin.X,
                ClientY = client.Y - origin.Y,
                Visible = true,
                DefaultEvent = isPart ? null : TypeDescriptor.GetDefaultEvent(control)?.Name,
                IsPart = isPart,
            };
        }

        yield return View(root, _className, null, isPart: false);
        var queue = new List<DesignerComponentView>();
        void Children(Control parent, string parentName)
        {
            foreach (Control child in parent.Controls)
            {
                string? name = NameOf(child);
                var isPart = false;
                if (name is null && child is SplitterPanel && parent is SplitContainer split && NameOf(split) is { } owner)
                {
                    name = owner + "." + (ReferenceEquals(split.Panel1, child) ? "Panel1" : "Panel2");
                    isPart = true;
                }

                if (name is null)
                {
                    continue;
                }

                queue.Add(View(child, name, parentName.Length == 0 ? _className : parentName, isPart));
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
            if (component is not Control && !ReferenceEquals(component, root) && component.Site is { } site)
            {
                yield return new DesignerComponentView(site.Name ?? string.Empty, DesignerTypes.NameOf(component.GetType()), null, 0, 0, 0, 0)
                {
                    DefaultEvent = TypeDescriptor.GetDefaultEvent(component)?.Name,
                };
            }
        }
    }

    // ------------------------------------------------------------------ serializing

    /// <summary>Every component's properties as Visual Studio's serializers would write them, by component and property.</summary>
    private Dictionary<(string Component, string Property), DesignerValue> Snapshot()
    {
        var snapshot = new Dictionary<(string, string), DesignerValue>();
        foreach (IComponent component in Host.Container.Components)
        {
            var name = ReferenceEquals(component, _root) ? string.Empty : component.Site?.Name ?? string.Empty;
            foreach (var (property, value) in Serialize(component))
            {
                snapshot[(name, property)] = value;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// A component's properties that the code sets: visible to serialization, not design-only or extended by another
    /// component, different from their defaults (<c>ShouldSerializeValue</c>); content properties one level down
    /// (<c>FlatAppearance.BorderSize</c>, <c>Panel1.BackColor</c>); collections are left to the code's calls.
    /// </summary>
    private List<(string Property, DesignerValue Value)> Serialize(IComponent component)
    {
        var result = new List<(string, DesignerValue)>();
        void Properties(object owner, string prefix, int depth)
        {
            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(owner, [DesignOnlyAttribute.No]))
            {
                if (descriptor.Name == "Name" || descriptor.SerializationVisibility == DesignerSerializationVisibility.Hidden
                    || descriptor.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is ExtenderProvidedPropertyAttribute { Provider: not null })
                {
                    continue;
                }

                try
                {
                    if (descriptor.SerializationVisibility == DesignerSerializationVisibility.Content)
                    {
                        if (depth == 0 && !typeof(IEnumerable).IsAssignableFrom(descriptor.PropertyType) && descriptor.GetValue(owner) is { } content)
                        {
                            Properties(content, prefix + descriptor.Name + ".", depth + 1);
                        }

                        continue;
                    }

                    if (descriptor.IsReadOnly || !descriptor.ShouldSerializeValue(owner) || _values.Serialize(descriptor.GetValue(owner)) is not { } value)
                    {
                        continue;
                    }

                    result.Add((prefix + descriptor.Name, value));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"{prefix}{descriptor.Name} couldn't be read: {ex.Message}");
                }
            }
        }

        Properties(component, string.Empty, 0);
        if (component is Control && component.Site?.Name is { } name)
        {
            result.Add(("Name", DesignerValue.String(name)));
        }

        return result;
    }

    private static DesignerPropertyChange[] Diff(Dictionary<(string Component, string Property), DesignerValue> before, Dictionary<(string Component, string Property), DesignerValue> after, ISet<string> skip)
    {
        var changes = new List<DesignerPropertyChange>();
        foreach (var (key, value) in after)
        {
            if (!skip.Contains(key.Component) && (!before.TryGetValue(key, out var old) || !old.SameAs(value)))
            {
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, value));
            }
        }

        foreach (var key in before.Keys)
        {
            if (!skip.Contains(key.Component) && !after.ContainsKey(key))
            {
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, null));
            }
        }

        return [.. changes];
    }

    private DesignerChangeResult Changed(Dictionary<(string Component, string Property), DesignerValue> before, ISet<string> skip) =>
        new(Diff(before, Snapshot(), skip), Render());

    // ------------------------------------------------------------------ changes

    /// <summary>A parent named by the IDE: the root (null or empty), a container component, or a SplitContainer's panel (<c>splitContainer1.Panel1</c>).</summary>
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
        var before = Snapshot();
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

        string? parentComponent = null;
        string? parentPath = null;
        int[]? cell = null;
        if (component is Control control)
        {
            var (parent, owner, path) = Parent(parameters.Parent);
            parentComponent = owner;
            parentPath = path;
            var siblings = parent.Controls.Cast<Control>().Where(c => !ReferenceEquals(c, control)).ToList();
            cell = AddTo(parent, control, ClientPoint(parent, parameters.X, parameters.Y));
            if (parameters is { Width: { } width, Height: { } height } && width >= 1 && height >= 1)
            {
                control.Size = new Size((int)Math.Round(width), (int)Math.Round(height));
            }

            control.TabIndex = siblings.Count == 0 ? 0 : siblings.Max(c => c.TabIndex) + 1;
        }

        var arguments = component is not Control && type.GetConstructor([typeof(IContainer)]) is not null
            ? new[] { DesignerValue.ReferenceTo("components") }
            : [];
        var created = new DesignerCreated(parameters.Name, DesignerTypes.NameOf(type), arguments, parentComponent, parentPath, cell,
            [.. Serialize(component).Select(p => new DesignerPropertyValue(p.Property, p.Value))])
        {
            SupportsInitialize = component is ISupportInitialize,
            IsControl = component is Control,
        };
        return Changed(before, new HashSet<string>(StringComparer.Ordinal) { parameters.Name }) with { Created = created };
    }

    public DesignerChangeResult SetProperties(DesignerSetPropertiesParams parameters)
    {
        var before = Snapshot();
        foreach (var edit in parameters.Edits)
        {
            var target = Target(edit.Component) ?? throw Unknown(edit.Component);
            var (owner, member) = Walk(target, edit.Property);
            var descriptor = TypeDescriptor.GetProperties(owner)[member] ?? throw new InvalidOperationException($"{edit.Property} isn't a property of {edit.Component}.");
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
                value = edit.Value is { } structured
                    ? _values.Evaluate(structured, descriptor.PropertyType)
                    : descriptor.Converter.ConvertFromInvariantString(edit.Text ?? string.Empty);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or InvalidCastException or OverflowException)
            {
                return new DesignerChangeResult([], Render()) { Error = $"'{edit.Text}' isn't a valid {edit.Property}: {ex.Message}" };
            }

            descriptor.SetValue(owner, value);
        }

        return Changed(before, new HashSet<string>(StringComparer.Ordinal));
    }

    public DesignerChangeResult SetParent(DesignerSetParentParams parameters)
    {
        var before = Snapshot();
        var control = Target(parameters.Component) as Control ?? throw new InvalidOperationException($"{parameters.Component} isn't a control.");
        var (parent, owner, path) = Parent(parameters.Parent);
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, control))
            {
                throw new InvalidOperationException($"{parameters.Component} can't go inside itself.");
            }
        }

        control.Parent?.Controls.Remove(control);
        var cell = AddTo(parent, control, ClientPoint(parent, parameters.X, parameters.Y));
        return Changed(before, new HashSet<string>(StringComparer.Ordinal)) with { Reparented = new DesignerReparented(parameters.Component, owner, path, cell) };
    }

    public DesignerChangeResult Delete(DesignerComponentsParams parameters)
    {
        var before = Snapshot();
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
                    else if (child is SplitterPanel panel)
                    {
                        foreach (Control grandchild in panel.Controls)
                        {
                            if (NameOf(grandchild) is { Length: > 0 })
                            {
                                Collect(grandchild);
                            }
                        }
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

        var names = removed.Select(c => c.Site!.Name!).ToList();
        foreach (var component in removed)
        {
            Host.DestroyComponent(component);
        }

        return Changed(before, new HashSet<string>(names, StringComparer.Ordinal)) with { Removed = [.. names] };
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
                : [.. merged.Join(infos, a => (a.Name, a.Type), b => (b.Name, b.Type), (a, b) => a.Value == b.Value ? a with { IsSet = a.IsSet || b.IsSet } : a with { Value = null, Mixed = true, IsSet = a.IsSet || b.IsSet })];
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

        foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(target, [BrowsableAttribute.Yes]))
        {
            if (descriptor.Name == "Name" || descriptor.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is ExtenderProvidedPropertyAttribute { Provider: not null })
            {
                continue;
            }

            DesignerPropertyInfo info;
            try
            {
                info = Describe(target, descriptor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                info = new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(descriptor.PropertyType), ex.Message) { IsReadOnly = true, Editor = DesignerEditors.ReadOnly };
            }

            yield return info;
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
            IsSet = descriptor.ShouldSerializeValue(target),
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
