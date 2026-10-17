using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Tests;

/// <summary>
/// A designer host that runs on any OS (M41a, M41b): it lays the model's controls out from their Location and Size,
/// draws a blank frame, and answers changes as Windows Forms' designer would for what the tests use: properties
/// (Text, Location, Size, TabIndex, Dock, Anchor), extender properties (a tool tip), data bindings, images, the
/// z-order, collections (a strip's or menu item's items, a list's items), smart tags, and a localizable form's resources.
/// </summary>
internal sealed class FakeWinFormsDesignerHost : IWinFormsDesignerHost
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private const string Forms = "System.Windows.Forms.";

    private sealed record Control(string Type, string? Parent, double X, double Y, double W, double H, string? Text, int TabIndex, string Dock, string Anchor);

    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Type, string Owner, string? Text)> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _itemOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DesignerValue>> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tray = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Component, string Provider), string> _toolTips = [];
    private readonly Dictionary<(string Component, string Property), string> _bindings = [];
    private string _root = "Form1";

    public int Loads { get; private set; }

    public List<string> Selected { get; } = [];

    public DesignerModel? Model { get; private set; }

    /// <summary>The last load's parameters (its resources and language).</summary>
    public DesignerLoadParams? LastLoad { get; private set; }

    public List<string> Invoked { get; } = [];

    public event EventHandler<string>? Log;

    public event EventHandler? Restarted;

    private static int Int(DesignerValue? value, int index) => int.Parse(value?.Arguments?[index].Value ?? "0", CultureInfo.InvariantCulture);

    public Task<DesignerRenderResult> LoadAsync(DesignerLoadParams parameters, CancellationToken cancellationToken)
    {
        Loads++;
        LastLoad = parameters;
        Model = parameters.Model;
        _root = parameters.Model.ClassName;
        _controls.Clear();
        _children.Clear();
        _items.Clear();
        _itemOrder.Clear();
        _values.Clear();
        _tray.Clear();
        _toolTips.Clear();
        _bindings.Clear();
        var statements = parameters.Model.Statements;
        foreach (var call in statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Member?.EndsWith("Controls.Add", StringComparison.Ordinal) == true))
        {
            Children(call.Target.Length == 0 ? _root : call.Target).Add(call.Arguments![0].Value!);
        }

        foreach (var call in statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Member is "Items.AddRange" or "DropDownItems.AddRange"))
        {
            var array = call.Arguments![0];
            if (array.Arguments!.All(a => a.Kind == DesignerValueKinds.Reference))
            {
                _itemOrder[call.Target] = [.. array.Arguments!.Select(a => a.Value!)];
            }
            else
            {
                _values[call.Target] = [.. array.Arguments!];
            }
        }

        foreach (var call in statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Member == "SetToolTip"))
        {
            _toolTips[(call.Arguments![0].Value!, call.Target)] = call.Arguments[1].Value!;
        }

        foreach (var component in parameters.Model.Components.Where(c => !c.IsRoot && !c.IsLocal))
        {
            DesignerValue? Value(string member) => statements.LastOrDefault(s => s.Kind == DesignerStatementKinds.Assign && s.Target == component.Name && s.Member == member)?.Value;
            var owner = _itemOrder.FirstOrDefault(o => o.Value.Contains(component.Name)).Key;
            if (owner is not null)
            {
                _items[component.Name] = (component.Type, owner, Value("Text")?.Value);
                continue;
            }

            var parent = _children.FirstOrDefault(c => c.Value.Contains(component.Name)).Key;
            if (parent is null)
            {
                // A list view's and a grid's columns aren't in the tray (DesignTimeVisible(false)).
                if (!component.Type.EndsWith("ColumnHeader", StringComparison.Ordinal) && !component.Type.EndsWith("Column", StringComparison.Ordinal))
                {
                    _tray[component.Name] = component.Type;
                }

                continue;
            }

            var location = Value("Location");
            var size = Value("Size");
            _controls[component.Name] = new Control(component.Type, parent, Int(location, 0), Int(location, 1), size is null ? 0 : Int(size, 0), size is null ? 0 : Int(size, 1),
                Value("Text")?.Value, int.Parse(Value("TabIndex")?.Value ?? "0", CultureInfo.InvariantCulture), Value("Dock")?.Members?[0] ?? "None",
                Value("Anchor")?.Members is { } anchors ? string.Join(", ", anchors) : "Top, Left");
        }

        Log?.Invoke(this, $"Loaded {_root}");
        return Task.FromResult(Render());
    }

    private List<string> Children(string parent)
    {
        if (!_children.TryGetValue(parent, out var list))
        {
            _children[parent] = list = [];
        }

        return list;
    }

    private static string[]? CollectionsOf(string type)
    {
        var simple = type[(type.LastIndexOf('.') + 1)..];
        return simple switch
        {
            "MenuStrip" or "ToolStrip" or "StatusStrip" or "ContextMenuStrip" => [Forms + simple + ":Items"],
            "ToolStripMenuItem" or "ToolStripDropDownButton" or "ToolStripSplitButton" => [Forms + "ToolStripDropDownItem:DropDownItems"],
            "ListBox" or "ComboBox" => [Forms + simple + ":Items"],
            "TabControl" => [Forms + "TabControl:TabPages"],
            "DataGridView" => [Forms + "DataGridView:Columns"],
            _ => null,
        };
    }

    private DesignerRenderResult Render()
    {
        var views = new List<DesignerComponentView> { new(_root, Forms + "Form", null, 0, 0, 400, 450) { IsControl = true, IsContainer = true, ClientX = 8, ClientY = 31, DefaultEvent = "Load", Collections = null } };
        void Add(string parentName)
        {
            var parent = views.First(v => v.Name == parentName);
            foreach (var name in Children(parentName).Where(_controls.ContainsKey))
            {
                var control = _controls[name];
                var simple = control.Type[(control.Type.LastIndexOf('.') + 1)..];
                views.Add(new DesignerComponentView(name, control.Type, parentName, parent.ClientX + control.X, parent.ClientY + control.Y, control.W, control.H)
                {
                    IsControl = true,
                    IsContainer = simple.EndsWith("Panel", StringComparison.Ordinal) || simple is "GroupBox" or "TabControl" or "TabPage",
                    ClientX = parent.ClientX + control.X,
                    ClientY = parent.ClientY + control.Y,
                    DefaultEvent = "Click",
                    TabIndex = control.TabIndex,
                    Dock = control.Dock,
                    Anchor = control.Anchor,
                    Baseline = simple is "Label" or "TextBox" or "Button" or "CheckBox" ? 15 : null,
                    HasActions = simple is "ListBox" or "TabControl" or "MenuStrip",
                    Collections = CollectionsOf(control.Type),
                    Text = control.Text,
                    InTray = simple.EndsWith("Strip", StringComparison.Ordinal),
                });
                Add(name);
            }
        }

        Add(_root);

        // The strips' items: the visible strips' top-level ones laid out from their texts.
        foreach (var (owner, names) in _itemOrder)
        {
            var strip = views.FirstOrDefault(v => v.Name == owner && v.IsControl);
            var x = (strip?.X ?? 0) + 2;
            for (var i = 0; i < names.Count; i++)
            {
                if (!_items.TryGetValue(names[i], out var item))
                {
                    continue;
                }

                var width = ((item.Text?.Length ?? 0) * 7) + 16;
                views.Add(new DesignerComponentView(names[i], item.Type, owner, strip is null ? 0 : x, strip is null ? 0 : strip.Y + 2, strip is null ? 0 : width, strip is null ? 0 : Math.Max(4, strip.Height - 4))
                {
                    IsItem = true,
                    Owner = owner,
                    ItemIndex = i,
                    Text = item.Text,
                    Visible = strip is not null,
                    HasDropDown = item.Type.EndsWith("ToolStripMenuItem", StringComparison.Ordinal),
                    Collections = CollectionsOf(item.Type),
                });
                x += width;
            }
        }

        views.AddRange(_tray.Select(t => new DesignerComponentView(t.Key, t.Value, null, 0, 0, 0, 0)
        {
            InTray = true,
            Visible = false,
            IsControl = t.Value.EndsWith("ContextMenuStrip", StringComparison.Ordinal),
            Collections = CollectionsOf(t.Value),
            DefaultEvent = t.Value.EndsWith("Timer", StringComparison.Ordinal) ? "Tick" : null,
        }));
        return new DesignerRenderResult(new DesignerFrame(Png, 400, 450), [.. views], []);
    }

    public Task<DesignerRenderResult> RenderAsync(CancellationToken cancellationToken) => Task.FromResult(Render());

    public Task<DesignerChangeResult> CreateAsync(DesignerCreateParams parameters, CancellationToken cancellationToken)
    {
        var x = (int)parameters.X - 8;
        var y = (int)parameters.Y - 31;
        _controls[parameters.Name] = new Control(parameters.Type, _root, x, y, 75, 23, parameters.Name, 11, "None", "Top, Left");
        Children(_root).Insert(0, parameters.Name);
        var created = new DesignerCreated(parameters.Name, parameters.Type, [], string.Empty, null, null,
        [
            new("Location", DesignerValue.NewOf("System.Drawing.Point", DesignerValue.Int(x), DesignerValue.Int(y))),
            new("Name", DesignerValue.String(parameters.Name)),
            new("Size", DesignerValue.NewOf("System.Drawing.Size", DesignerValue.Int(75), DesignerValue.Int(23))),
            new("TabIndex", DesignerValue.Int(11)),
            new("Text", DesignerValue.String(parameters.Name)),
            new("UseVisualStyleBackColor", DesignerValue.Bool(true)),
        ]) { IsControl = true };
        return Task.FromResult(new DesignerChangeResult([], Render()) { Created = created });
    }

    public Task<DesignerChangeResult> SetPropertiesAsync(DesignerSetPropertiesParams parameters, CancellationToken cancellationToken)
    {
        var changes = new List<DesignerPropertyChange>();
        var resources = new List<DesignerResourceEntry>();
        var localized = new List<DesignerLocalized>();
        foreach (var edit in parameters.Edits)
        {
            if (edit.Component.Length == 0 && edit.Property == "Localizable")
            {
                resources.Add(new DesignerResourceEntry("$this.Localizable", new DesignerResource { Type = "System.Boolean, mscorlib", Text = edit.Text ?? "False" }) { Metadata = true });
                continue;
            }

            if (edit.Provider is { } provider)
            {
                if (edit.Reset || string.IsNullOrEmpty(edit.Text))
                {
                    _toolTips.Remove((edit.Component, provider));
                }
                else
                {
                    _toolTips[(edit.Component, provider)] = edit.Text;
                }

                changes.Add(new DesignerPropertyChange(edit.Component, edit.Property, edit.Reset || string.IsNullOrEmpty(edit.Text) ? null : DesignerValue.String(edit.Text)) { Provider = provider });
                continue;
            }

            if (edit.DataBinding)
            {
                DesignerValue? binding = null;
                if (edit.Text is { } text && text != "(none)" && text.Split(" - ") is [var source, var member])
                {
                    _bindings[(edit.Component, edit.Property)] = text;
                    binding = DesignerValue.NewOf(Forms + "Binding", DesignerValue.String(edit.Property), DesignerValue.ReferenceTo(source), DesignerValue.String(member), DesignerValue.Bool(true));
                }
                else
                {
                    _bindings.Remove((edit.Component, edit.Property));
                }

                changes.Add(new DesignerPropertyChange(edit.Component, edit.Property, binding) { DataBinding = true });
                continue;
            }

            if (edit.Bytes is { } bytes)
            {
                var key = edit.Component + "." + edit.Property;
                resources.Add(new DesignerResourceEntry(key, new DesignerResource { Type = "System.Drawing.Bitmap, System.Drawing", MimeType = "application/x-microsoft.net.object.bytearray.base64", Text = "\n        " + bytes + "\n" }));
                changes.Add(new DesignerPropertyChange(edit.Component, edit.Property, new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = key }));
                continue;
            }

            var control = _controls[edit.Component];
            switch (edit.Property)
            {
                case "Text":
                    _controls[edit.Component] = control with { Text = edit.Text };
                    if (LastLoad?.Localizable == true)
                    {
                        resources.Add(new DesignerResourceEntry(edit.Component + ".Text", edit.Reset ? null : DesignerResource.String(edit.Text ?? string.Empty)) { Culture = LastLoad.Culture ?? string.Empty });
                        localized.Add(new DesignerLocalized(edit.Component, true));
                        break;
                    }

                    changes.Add(new DesignerPropertyChange(edit.Component, "Text", edit.Reset ? null : DesignerValue.String(edit.Text ?? string.Empty)));
                    break;
                case "Location" or "Size":
                    var parts = edit.Text!.Split(',', StringSplitOptions.TrimEntries).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                    _controls[edit.Component] = edit.Property == "Location" ? control with { X = parts[0], Y = parts[1] } : control with { W = parts[0], H = parts[1] };
                    changes.Add(new DesignerPropertyChange(edit.Component, edit.Property, DesignerValue.NewOf("System.Drawing." + (edit.Property == "Location" ? "Point" : "Size"), DesignerValue.Int(parts[0]), DesignerValue.Int(parts[1]))));
                    break;
                case "TabIndex":
                    _controls[edit.Component] = control with { TabIndex = int.Parse(edit.Text!, CultureInfo.InvariantCulture) };
                    changes.Add(new DesignerPropertyChange(edit.Component, "TabIndex", DesignerValue.Int(int.Parse(edit.Text!, CultureInfo.InvariantCulture))));
                    break;
                case "Dock":
                    _controls[edit.Component] = control with { Dock = edit.Text! };
                    changes.Add(new DesignerPropertyChange(edit.Component, "Dock", edit.Text == "None" ? null : DesignerValue.EnumOf(Forms + "DockStyle", edit.Text!)));
                    break;
                case "Anchor":
                    _controls[edit.Component] = control with { Anchor = edit.Text! };
                    changes.Add(new DesignerPropertyChange(edit.Component, "Anchor", edit.Text == "Top, Left" ? null : DesignerValue.EnumOf(Forms + "AnchorStyles", [.. edit.Text!.Split(',', StringSplitOptions.TrimEntries)])));
                    break;
                default:
                    return Task.FromResult(new DesignerChangeResult([], Render()) { Error = $"The fake host doesn't set {edit.Property}." });
            }
        }

        return Task.FromResult(new DesignerChangeResult([.. changes], Render()) { Resources = [.. resources], Localized = [.. localized] });
    }

    public Task<DesignerChangeResult> SetParentAsync(DesignerSetParentParams parameters, CancellationToken cancellationToken)
    {
        var control = _controls[parameters.Component];
        Children(control.Parent!).Remove(parameters.Component);
        var parent = parameters.Parent ?? _root;
        Children(parent).Insert(0, parameters.Component);
        _controls[parameters.Component] = control with { Parent = parent };
        return Task.FromResult(new DesignerChangeResult([], Render()) { Reparented = new DesignerReparented(parameters.Component, parameters.Parent, null, null) });
    }

    public Task<DesignerChangeResult> DeleteAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        var removed = parameters.Components.Concat(_controls.Where(c => parameters.Components.Contains(c.Value.Parent)).Select(c => c.Key)).ToList();
        var collections = new List<DesignerCollectionChange>();
        foreach (var name in removed)
        {
            if (_controls.Remove(name, out var control))
            {
                Children(control.Parent!).Remove(name);
            }

            _tray.Remove(name);
            if (_items.Remove(name, out var item))
            {
                _itemOrder[item.Owner].Remove(name);
                collections.Add(ItemsChange(item.Owner));
            }
        }

        return Task.FromResult(new DesignerChangeResult([], Render()) { Removed = [.. removed], Collections = [.. collections] });
    }

    private DesignerCollectionChange ItemsChange(string owner) =>
        new(owner, _items.ContainsKey(owner) ? "DropDownItems" : "Items", "AddRange", Forms + "ToolStripItem", [.. _itemOrder[owner].Select(DesignerValue.ReferenceTo)]);

    public Task<DesignerPropertiesResult> PropertiesAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        var name = parameters.Components.FirstOrDefault() ?? string.Empty;
        if (_items.TryGetValue(name, out var item))
        {
            return Task.FromResult(new DesignerPropertiesResult([new("Name", "Design", "System.String", name) { IsSet = true }, new("Text", "Appearance", "System.String", item.Text ?? string.Empty) { IsSet = true }]));
        }

        var control = _controls.GetValueOrDefault(name);
        var rows = new List<DesignerPropertyInfo>
        {
            new("Name", "Design", "System.String", name.Length == 0 ? _root : name) { IsSet = true, IsReadOnly = name.Length == 0 },
        };
        if (name.Length == 0)
        {
            rows.Add(new("Localizable", "Design", "System.Boolean", LastLoad?.Localizable == true ? "True" : "False") { Editor = DesignerEditors.Boolean, StandardValues = ["True", "False"] });
        }

        if (control is null)
        {
            return Task.FromResult(new DesignerPropertiesResult([.. rows]));
        }

        rows.AddRange(
        [
            new("Text", "Appearance", "System.String", control.Text ?? string.Empty) { IsSet = true, Localizable = LastLoad?.Localizable == true },
            new("Location", "Layout", "System.Drawing.Point", $"{control.X}, {control.Y}") { IsSet = true },
            new("TabIndex", "Behavior", "System.Int32", control.TabIndex.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
            new("Enabled", "Behavior", "System.Boolean", "True") { Editor = DesignerEditors.Boolean, StandardValues = ["True", "False"] },
            new("Anchor", "Layout", Forms + "AnchorStyles", control.Anchor) { Editor = DesignerEditors.Anchor, StandardValues = ["None", "Top", "Bottom", "Left", "Right"] },
            new("Dock", "Layout", Forms + "DockStyle", control.Dock) { Editor = DesignerEditors.Dock, StandardValues = ["None", "Top", "Bottom", "Left", "Right", "Fill"] },
            new("BackColor", "Appearance", "System.Drawing.Color", "Control") { Editor = DesignerEditors.Color },
        ]);
        if (control.Type.EndsWith("PictureBox", StringComparison.Ordinal) || control.Type.EndsWith("Button", StringComparison.Ordinal))
        {
            rows.Add(new("Image", "Appearance", "System.Drawing.Image", "(none)") { Editor = DesignerEditors.Image });
        }

        if (CollectionsOf(control.Type) is [var collection, ..])
        {
            rows.Add(new(DesignerCollections.PropertyOf(collection), "Data", "System.Object", "(Collection)") { Editor = DesignerEditors.Collection, Collection = collection });
        }

        foreach (var (tip, _) in _tray.Where(t => t.Value.EndsWith("ToolTip", StringComparison.Ordinal)))
        {
            rows.Add(new("ToolTip", "Misc", "System.String", _toolTips.GetValueOrDefault((name, tip)) ?? string.Empty) { Provider = tip, IsSet = _toolTips.ContainsKey((name, tip)) });
        }

        var sources = _tray.Where(t => t.Value.EndsWith("BindingSource", StringComparison.Ordinal)).Select(t => t.Key).ToList();
        if (sources.Count > 0)
        {
            string[] choices = ["(none)", .. sources.SelectMany(s => new[] { s + " - Name", s + " - Quantity" })];
            rows.Add(new("Text", "(DataBindings)", "System.String", _bindings.GetValueOrDefault((name, "Text")) ?? "(none)") { DataBinding = true, Editor = DesignerEditors.Enum, StandardValues = choices });
        }

        return Task.FromResult(new DesignerPropertiesResult([.. rows]));
    }

    public Task<DesignerEventsResult> EventsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new DesignerEventsResult(
        [
            new("Click", "Action", "System.EventHandler", ["System.Object sender", "System.EventArgs e"]),
            new("MouseDown", "Mouse", Forms + "MouseEventHandler", ["System.Object sender", Forms + "MouseEventArgs e"]),
            new("Tick", "Behavior", "System.EventHandler", ["System.Object sender", "System.EventArgs e"]),
        ], "Click"));

    public Task SelectAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        Selected.Clear();
        Selected.AddRange(parameters.Components);
        return Task.CompletedTask;
    }

    public Task<DesignerChangeResult> SetChildIndexAsync(DesignerChildIndexParams parameters, CancellationToken cancellationToken)
    {
        var parent = _controls[parameters.Component].Parent!;
        var children = Children(parent);
        children.Remove(parameters.Component);
        children.Insert(Math.Clamp(parameters.Index, 0, children.Count), parameters.Component);
        return Task.FromResult(new DesignerChangeResult([], Render()) { Reordered = [new DesignerChildOrder(parent == _root ? string.Empty : parent, [.. children])] });
    }

    public Task<DesignerCollectionResult> CollectionAsync(DesignerCollectionParams parameters, CancellationToken cancellationToken)
    {
        var type = _controls.TryGetValue(parameters.Component, out var control) ? control.Type : _items.TryGetValue(parameters.Component, out var item) ? item.Type : _tray.GetValueOrDefault(parameters.Component) ?? string.Empty;
        var key = CollectionsOf(type)?.FirstOrDefault(c => c.EndsWith(":" + parameters.Property, StringComparison.Ordinal)) ?? string.Empty;
        if (_itemOrder.TryGetValue(parameters.Component, out var names))
        {
            return Task.FromResult(new DesignerCollectionResult(key, [.. names.Select(n => new DesignerCollectionItem { Name = n, Type = _items[n].Type, Text = _items[n].Text, Display = n })]));
        }

        return Task.FromResult(new DesignerCollectionResult(key, [.. (_values.GetValueOrDefault(parameters.Component) ?? []).Select(v => new DesignerCollectionItem { Value = v, Text = v.Value, Display = v.Value })]));
    }

    public Task<DesignerChangeResult> SetCollectionAsync(DesignerSetCollectionParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.Property is "Items" or "DropDownItems" && parameters.Items.All(i => i.Value is null))
        {
            var old = _itemOrder.GetValueOrDefault(parameters.Component) ?? [];
            var added = new List<DesignerCreated>();
            var order = new List<string>();
            foreach (var item in parameters.Items)
            {
                var name = item.Name!;
                if (!_items.ContainsKey(name))
                {
                    var type = item.Type ?? Forms + "ToolStripMenuItem";
                    _items[name] = (type, parameters.Component, item.Text);
                    var size = type.EndsWith("Separator", StringComparison.Ordinal) ? DesignerValue.NewOf("System.Drawing.Size", DesignerValue.Int(6), DesignerValue.Int(25)) : DesignerValue.NewOf("System.Drawing.Size", DesignerValue.Int(((item.Text?.Length ?? 0) * 7) + 16), DesignerValue.Int(20));
                    DesignerPropertyValue[] properties = item.Text is null
                        ? [new("Name", DesignerValue.String(name)), new("Size", size)]
                        : [new("Name", DesignerValue.String(name)), new("Size", size), new("Text", DesignerValue.String(item.Text))];
                    added.Add(new DesignerCreated(name, type, [], null, null, null, properties));
                }

                order.Add(name);
            }

            var removed = old.Where(n => !order.Contains(n)).ToList();
            foreach (var name in removed)
            {
                _items.Remove(name);
            }

            _itemOrder[parameters.Component] = order;
            return Task.FromResult(new DesignerChangeResult([], Render()) { Added = [.. added], Removed = [.. removed], Collections = [ItemsChange(parameters.Component)] });
        }

        _values[parameters.Component] = [.. parameters.Items.Select(i => i.Value ?? DesignerValue.String(i.Text ?? string.Empty))];
        return Task.FromResult(new DesignerChangeResult([], Render())
        {
            Collections = [new DesignerCollectionChange(parameters.Component, parameters.Property, "AddRange", "System.Object", [.. _values[parameters.Component]])],
        });
    }

    public Task<DesignerActionsResult> ActionsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        var name = parameters.Components.FirstOrDefault() ?? string.Empty;
        var type = _controls.TryGetValue(name, out var control) ? control.Type : string.Empty;
        List<DesignerActionItem> items = [new(DesignerActionKinds.Method, "DockInParent", control?.Dock == "Fill" ? "Undock in Parent Container" : "Dock in Parent Container")];
        if (type.EndsWith("ListBox", StringComparison.Ordinal))
        {
            items.Insert(0, new(DesignerActionKinds.Header, string.Empty, "Data"));
            items.Insert(1, new(DesignerActionKinds.Property, "UseDataBoundItems", "Use Data Bound Items") { Editor = DesignerEditors.Boolean, Value = "False", StandardValues = ["True", "False"] });
            items.Insert(2, new(DesignerActionKinds.Method, "EditItems", "Edit Items...") { Collection = Forms + "ListBox:Items" });
            items.Add(new(DesignerActionKinds.Method, "ChooseDataSource", "Choose Data Source...") { Unavailable = true });
        }

        return Task.FromResult(new DesignerActionsResult([.. items]));
    }

    public Task<DesignerChangeResult> InvokeActionAsync(DesignerInvokeActionParams parameters, CancellationToken cancellationToken)
    {
        Invoked.Add(parameters.Member + (parameters.Text is { } text ? "=" + text : string.Empty));
        if (parameters.Member == "DockInParent")
        {
            var control = _controls[parameters.Component];
            var dock = control.Dock == "Fill" ? "None" : "Fill";
            _controls[parameters.Component] = control with { Dock = dock };
            return Task.FromResult(new DesignerChangeResult([new DesignerPropertyChange(parameters.Component, "Dock", dock == "None" ? null : DesignerValue.EnumOf(Forms + "DockStyle", dock))], Render()));
        }

        return Task.FromResult(new DesignerChangeResult([], Render()));
    }

    public void Restart() => Restarted?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
