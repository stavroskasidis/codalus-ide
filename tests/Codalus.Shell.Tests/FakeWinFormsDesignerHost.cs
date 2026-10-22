using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Tests;

/// <summary>
/// A designer host that runs on any OS (M41a, M41b): it lays the model's controls out from their Location and Size,
/// draws a blank frame, and answers changes as Windows Forms' designer would for what the tests use: properties
/// (Text, Location, Size, TabIndex, Dock, Anchor), extender properties (a tool tip), data bindings, images, the
/// z-order, collections (a strip's or menu item's items, a list's items), smart tags and verbs, a localizable form's
/// resources, a split container's panels, and the control designers' Locked as the default .resx's metadata (#67).
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
    private readonly Dictionary<string, List<string>> _parts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _locked = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Component, string Property), DesignerValue> _assigned = [];
    private string _root = "Form1";

    /// <summary>The properties the fake keeps as the code says them (#68), besides Text, Location, Size, TabIndex, Dock and Anchor.</summary>
    private static readonly HashSet<string> s_kept = new(StringComparer.Ordinal) { "Font", "ForeColor", "BackColor", "TextAlign", "Cursor", "Enabled", "Lines", "FlatAppearance.BorderSize", "AcceptButton", "CancelButton", "ShortcutKeys" };

    /// <summary>The edits of the last designer/setProperties (#68: a font editor's parts come in one request).</summary>
    public List<DesignerPropertyEdit> LastEdits { get; } = [];

    /// <summary>How many designer/setProperties requests came (#68: a Cancel sends none).</summary>
    public int SetPropertiesCalls { get; private set; }

    /// <summary>A Locked metadata entry as the classic IDE writes it (and the real host, through ResXResourceWriter).</summary>
    public const string BooleanType = "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

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
        _parts.Clear();
        _locked.Clear();
        _assigned.Clear();
        foreach (var assign in parameters.Model.Statements.Where(s => s.Kind == DesignerStatementKinds.Assign && s.Member is { } member && s_kept.Contains(member) && s.Value is not null))
        {
            _assigned[(assign.Target.Length == 0 ? parameters.Model.ClassName : assign.Target, assign.Member!)] = assign.Value!;
        }

        if (parameters.Resources.FirstOrDefault(r => r.Culture.Length == 0) is { } resources)
        {
            // The control designers' Locked, as the classic IDE keeps it: name.Locked ($this.Locked) metadata.
            foreach (System.Text.RegularExpressions.Match locked in System.Text.RegularExpressions.Regex.Matches(resources.Text, "<metadata name=\"([^\"]+)\\.Locked\"[^>]*>\\s*<value>True</value>"))
            {
                _locked.Add(locked.Groups[1].Value == "$this" ? _root : locked.Groups[1].Value);
            }
        }

        var statements = parameters.Model.Statements;
        foreach (var call in statements.Where(s => s.Kind == DesignerStatementKinds.Call && s.Member?.EndsWith("Controls.Add", StringComparison.Ordinal) == true))
        {
            // A split container's panel holds its controls as the real host has it (#67): notesSplit.Panel1, a part.
            var target = call.Target.Length == 0 ? _root : call.Target;
            if (call.Member!.Length > "Controls.Add".Length && call.Member[..^".Controls.Add".Length] is { Length: > 0 } part)
            {
                if (!_parts.TryGetValue(target, out var parts))
                {
                    _parts[target] = parts = [];
                }

                if (!parts.Contains(part))
                {
                    parts.Add(part);
                }

                target += "." + part;
            }

            Children(target).Add(call.Arguments![0].Value!);
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

    /// <summary>Whether <paramref name="name"/> is a split container's panel (<c>notesSplit.Panel1</c>).</summary>
    private bool IsPartName(string name) =>
        name.LastIndexOf('.') is var dot and > 0 && _parts.TryGetValue(name[..dot], out var parts) && parts.Contains(name[(dot + 1)..]);

    /// <summary>A parent as the code says it: <c>notesSplit.Panel1</c> is notesSplit's Panel1.</summary>
    private (string? Parent, string? Path) Split(string? parent) =>
        parent is not null && IsPartName(parent) ? (parent[..parent.LastIndexOf('.')], parent[(parent.LastIndexOf('.') + 1)..]) : (parent, null);

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
            "ListBox" or "ComboBox" or "DomainUpDown" => [Forms + simple + ":Items"],
            "ListView" => [Forms + "ListView:Columns", Forms + "ListView:Items"],
            "TabControl" => [Forms + "TabControl:TabPages"],
            "DataGridView" => [Forms + "DataGridView:Columns"],
            "ImageList" => [Forms + "ImageList:Images"],
            _ => null,
        };
    }

    private DesignerRenderResult Render()
    {
        var views = new List<DesignerComponentView> { new(_root, Forms + "Form", null, 0, 0, 400, 450) { IsControl = true, IsContainer = true, ClientX = 8, ClientY = 31, DefaultEvent = "Load", Collections = null, Locked = _locked.Contains(_root) } };
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
                    Locked = _locked.Contains(name),
                });
                Add(name);
                AddParts(name);
            }
        }

        // A split container's panels, as the real host reports them: parts that hold controls, the owner's halves, with
        // the owner's client origin (so their controls keep the coordinates the fake gave them before it had panels).
        void AddParts(string owner)
        {
            if (!_parts.TryGetValue(owner, out var parts))
            {
                return;
            }

            var view = views.First(v => v.Name == owner);
            for (var i = 0; i < parts.Count; i++)
            {
                var width = view.Width / parts.Count;
                views.Add(new DesignerComponentView(owner + "." + parts[i], Forms + "SplitterPanel", owner, view.X + (i * width), view.Y, width, view.Height)
                {
                    IsControl = true,
                    IsContainer = true,
                    IsPart = true,
                    ClientX = view.ClientX,
                    ClientY = view.ClientY,
                });
                Add(owner + "." + parts[i]);
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
        return new DesignerRenderResult(new DesignerFrame(Png, 400 + FrameExtra, 450 + FrameExtra), [.. views], []);
    }

    public Task<DesignerRenderResult> RenderAsync(CancellationToken cancellationToken) => Task.FromResult(Render());

    /// <summary>The framework's components that aren't controls (they go to the tray), by simple name.</summary>
    private static readonly HashSet<string> s_components = new(StringComparer.Ordinal) { "Timer", "ToolTip", "BindingSource", "ErrorProvider", "ImageList", "ContextMenuStrip", "BackgroundWorker", "HelpProvider", "NotifyIcon" };

    private static bool IsContainerType(string type)
    {
        var simple = type[(type.LastIndexOf('.') + 1)..];
        return simple.EndsWith("Panel", StringComparison.Ordinal) || simple is "GroupBox" or "TabPage";
    }

    /// <summary>
    /// A Toolbox item as the real host creates it (#66), for what the tests use: a component that isn't a control goes to
    /// the tray; a drop goes into its parent at the point; the Toolbox's double-click or Enter goes into the selection's
    /// container (the selected control's parent, or a container selected itself, else the form), at the container's
    /// corner when it's selected itself, else a grid step (8, 8) below and right of the selected control.
    /// </summary>
    public Task<DesignerChangeResult> CreateAsync(DesignerCreateParams parameters, CancellationToken cancellationToken)
    {
        Creates.Add(parameters);
        var simple = parameters.Type[(parameters.Type.LastIndexOf('.') + 1)..];
        if (s_components.Contains(simple))
        {
            // A tray component has no Locked (a context menu strip's designer isn't a control designer).
            _tray[parameters.Name] = parameters.Type;
            // Not a control on the form (a context menu has no parent): its creation takes the form's components, as the real host's does for an IContainer constructor.
            var component = new DesignerCreated(parameters.Name, parameters.Type, simple is "Timer" or "ToolTip" or "ErrorProvider" or "ImageList" or "BindingSource" or "ContextMenuStrip" ? [DesignerValue.ReferenceTo("components")] : [], null, null, null, []);
            return Task.FromResult(new DesignerChangeResult([], Render()) { Created = component });
        }

        string parent;
        int x, y;
        if (parameters.AtDefaultLocation)
        {
            var selected = parameters.Selected.FirstOrDefault(s => s == _root || _controls.ContainsKey(s));
            parent = selected is null || selected == _root ? _root : IsContainerType(_controls[selected].Type) ? selected : _controls[selected].Parent ?? _root;
            (x, y) = selected is null || selected == parent ? (0, 0) : ((int)_controls[selected].X + 8, (int)_controls[selected].Y + 8);
        }
        else
        {
            parent = parameters.Parent is { } named && (_controls.ContainsKey(named) || IsPartName(named)) ? named : _root;
            var view = Render().Components.First(v => v.Name == parent);
            (x, y) = ((int)(parameters.X - view.ClientX), (int)(parameters.Y - view.ClientY));
        }

        _controls[parameters.Name] = new Control(parameters.Type, parent, x, y, 75, 23, parameters.Name, 11, "None", "Top, Left");
        Children(parent).Insert(0, parameters.Name);
        var (createdParent, createdPath) = Split(parent == _root ? string.Empty : parent);
        var created = new DesignerCreated(parameters.Name, parameters.Type, [], createdParent, createdPath, null,
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

    /// <summary>What puts the fake's state back as it is now (#68: a request with an invalid value sets none of its edits, as the real host's).</summary>
    private Action Snapshot()
    {
        Dictionary<string, Control> controls = new(_controls, StringComparer.Ordinal);
        Dictionary<string, (string Type, string Owner, string? Text)> items = new(_items, StringComparer.Ordinal);
        Dictionary<(string Component, string Provider), string> toolTips = new(_toolTips);
        Dictionary<(string Component, string Property), string> bindings = new(_bindings);
        Dictionary<(string Component, string Property), DesignerValue> assigned = new(_assigned);
        HashSet<string> locked = new(_locked, StringComparer.Ordinal);
        return () =>
        {
            static void Restore<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
                where TKey : notnull
            {
                target.Clear();
                foreach (var (key, value) in source)
                {
                    target[key] = value;
                }
            }

            Restore(_controls, controls);
            Restore(_items, items);
            Restore(_toolTips, toolTips);
            Restore(_bindings, bindings);
            Restore(_assigned, assigned);
            _locked.Clear();
            _locked.UnionWith(locked);
        };
    }

    public Task<DesignerChangeResult> SetPropertiesAsync(DesignerSetPropertiesParams parameters, CancellationToken cancellationToken)
    {
        SetPropertiesCalls++;
        var restore = Snapshot();
        var result = SetProperties(parameters);
        if (result.Error is not null)
        {
            restore();
            result = new DesignerChangeResult([], Render()) { Error = result.Error };
        }

        return Task.FromResult(result);
    }

    private DesignerChangeResult SetProperties(DesignerSetPropertiesParams parameters)
    {
        var changes = new List<DesignerPropertyChange>();
        var resources = new List<DesignerResourceEntry>();
        var localized = new List<DesignerLocalized>();
        LastEdits.Clear();
        LastEdits.AddRange(parameters.Edits);
        var fonts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in parameters.Edits)
        {
            if (edit.Property == "Locked" && edit.Provider is null)
            {
                // The control designers' design-time Locked (#67): metadata in the default resources, never code; a component
                // without one (a tray component, an item, a part) is skipped, as the real host skips it.
                var name = edit.Component.Length == 0 ? _root : edit.Component;
                if (name != _root && !_controls.ContainsKey(name))
                {
                    continue;
                }

                var locked = string.Equals(edit.Text, "True", StringComparison.OrdinalIgnoreCase) && !edit.Reset;
                if (locked != _locked.Contains(name))
                {
                    _ = locked ? _locked.Add(name) : _locked.Remove(name);
                    resources.Add(new DesignerResourceEntry((name == _root ? "$this" : name) + ".Locked", locked ? new DesignerResource { Type = BooleanType, Text = "True" } : null) { Metadata = true });
                }

                continue;
            }

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

            if (edit is { Property: "Image", Reset: true })
            {
                // Select Resource's Clear (#68): no image, so no assignment.
                changes.Add(new DesignerPropertyChange(edit.Component, "Image", null));
                continue;
            }

            if (edit.Bytes is { } bytes)
            {
                var key = edit.Component + "." + edit.Property;
                resources.Add(new DesignerResourceEntry(key, new DesignerResource { Type = "System.Drawing.Bitmap, System.Drawing", MimeType = "application/x-microsoft.net.object.bytearray.base64", Text = "\n        " + bytes + "\n" }));
                changes.Add(new DesignerPropertyChange(edit.Component, edit.Property, new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = key }));
                continue;
            }

            if (SetKept(edit, changes, fonts) is { } keptError)
            {
                return new DesignerChangeResult([], Render()) { Error = keptError };
            }

            if (s_handled.Contains(edit.Property) || edit.Property.StartsWith("Font.", StringComparison.Ordinal))
            {
                continue;
            }

            if (edit.Property == "Text" && _items.TryGetValue(edit.Component, out var menuItem))
            {
                _items[edit.Component] = menuItem with { Text = edit.Reset ? null : edit.Text };
                changes.Add(new DesignerPropertyChange(edit.Component, "Text", edit.Reset ? null : DesignerValue.String(edit.Text ?? string.Empty)));
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
                    return new DesignerChangeResult([], Render()) { Error = $"The fake host doesn't set {edit.Property}." };
            }
        }

        // Several edits of one font in one request make one change (#68: the font editor's OK).
        foreach (var component in fonts)
        {
            changes.Add(new DesignerPropertyChange(component == _root ? string.Empty : component, "Font", Assigned(component, "Font")));
        }

        return new DesignerChangeResult([.. changes], Render()) { Resources = [.. resources], Localized = [.. localized] };
    }

    private static readonly HashSet<string> s_handled = new(StringComparer.Ordinal)
    {
        "Font", "ForeColor", "BackColor", "TextAlign", "Cursor", "Enabled", "Lines", "AcceptButton", "CancelButton", "ShortcutKeys", "FlatAppearance.BorderSize",
        "Location.X", "Location.Y", "Size.Width", "Size.Height",
    };

    /// <summary>
    /// The properties of #68 as the real host answers them: a part's dotted path written as the whole value (one font
    /// change for several of its parts), colors, alignment, cursors, a string array, a component reference, a reset;
    /// <c>Location.X = "abc"</c> is the converter's error.
    /// </summary>
    private string? SetKept(DesignerPropertyEdit edit, List<DesignerPropertyChange> changes, HashSet<string> fonts)
    {
        var component = edit.Component.Length == 0 ? _root : edit.Component;
        var key = (component, edit.Property);
        var text = edit.Text ?? string.Empty;
        void Change(string property, DesignerValue? value)
        {
            if (value is null)
            {
                _assigned.Remove((component, property));
            }
            else
            {
                _assigned[(component, property)] = value;
            }

            changes.RemoveAll(c => c.Component == edit.Component && c.Property == property);
            changes.Add(new DesignerPropertyChange(edit.Component, property, value));
        }

        switch (edit.Property)
        {
            case "Location.X" or "Location.Y" or "Size.Width" or "Size.Height":
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return $"'{text}' is not a valid value for Int32.";
                }

                var control = _controls[component];
                control = edit.Property switch
                {
                    "Location.X" => control with { X = number },
                    "Location.Y" => control with { Y = number },
                    "Size.Width" => control with { W = number },
                    _ => control with { H = number },
                };
                _controls[component] = control;
                var location = edit.Property.StartsWith("Location", StringComparison.Ordinal);
                changes.RemoveAll(c => c.Component == edit.Component && c.Property == (location ? "Location" : "Size"));
                changes.Add(new DesignerPropertyChange(edit.Component, location ? "Location" : "Size", location
                    ? DesignerValue.NewOf("System.Drawing.Point", DesignerValue.Int((int)control.X), DesignerValue.Int((int)control.Y))
                    : DesignerValue.NewOf("System.Drawing.Size", DesignerValue.Int((int)control.W), DesignerValue.Int((int)control.H))));
                return null;
            }

            case "Font":
                if (edit.Reset)
                {
                    _assigned.Remove(key);
                }
                else if (text.Split(',', StringSplitOptions.TrimEntries) is [var family, var points, ..] && float.TryParse(points.Replace("pt", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
                {
                    var styles = text.Contains("style=", StringComparison.Ordinal) ? text[(text.IndexOf("style=", StringComparison.Ordinal) + 6)..].Split(',', StringSplitOptions.TrimEntries) : [];
                    _assigned[key] = FontValue(Assigned(component, "Font"), family, typed, styles);
                }
                else
                {
                    return $"'{text}' is not a valid value for Font.";
                }

                fonts.Add(component);
                return null;

            case ['F', 'o', 'n', 't', '.', ..]:
            {
                var font = Assigned(component, "Font");
                var (name, size, styles) = FontParts(font);
                var set = styles.ToHashSet(StringComparer.Ordinal);
                var part = edit.Property["Font.".Length..];
                switch (part)
                {
                    case "Name":
                        name = edit.Reset ? "Segoe UI" : text;
                        break;
                    case "Size" when edit.Reset:
                        size = 9F;
                        break;
                    case "Size":
                        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out size) || size <= 0)
                        {
                            return $"'{text}' is not a valid value for Single.";
                        }

                        break;
                    case "Bold" or "Italic" or "Underline" or "Strikeout":
                        _ = !edit.Reset && string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) ? set.Add(part) : set.Remove(part);
                        break;
                    case "GdiCharSet":
                        if (!byte.TryParse(edit.Reset ? "0" : text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var charset))
                        {
                            return $"'{text}' is not a valid value for Byte.";
                        }

                        var written = FontValue(font, name, size, set);
                        _assigned[(component, "Font")] = written with { Arguments = [.. written.Arguments![..4], DesignerValue.Int(charset)] };
                        fonts.Add(component);
                        return null;
                    default:
                        return $"Font's {part} can't be set.";
                }

                _assigned[(component, "Font")] = FontValue(font, name, size, set);
                fonts.Add(component);
                return null;
            }

            case "ForeColor" or "BackColor":
            {
                var fallback = edit.Property == "BackColor" ? "Control" : "ControlText";
                if (edit.Reset || text == fallback)
                {
                    Change(edit.Property, null);
                    return null;
                }

                if (ColorValue(text) is not { } color)
                {
                    return $"'{text}' is not a valid value for Color.";
                }

                Change(edit.Property, color);
                return null;
            }

            case "TextAlign":
                Change("TextAlign", edit.Reset || text is "MiddleCenter" or "Left" ? null : DesignerValue.EnumOf(_controls[component].Type.EndsWith("TextBox", StringComparison.Ordinal) ? Forms + "HorizontalAlignment" : "System.Drawing.ContentAlignment", text));
                return null;

            case "Cursor":
                Change("Cursor", edit.Reset || text == "Default" ? null : DesignerValue.MemberOf(Forms + "Cursors", text));
                return null;

            case "Enabled":
                Change("Enabled", edit.Reset || string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) ? null : DesignerValue.Bool(false));
                return null;

            case "Lines":
                Change("Lines", edit.Reset ? null : edit.Value ?? DesignerValue.ArrayOf("System.String", [.. text.Split('\n').Select(l => DesignerValue.String(l.TrimEnd('\r')))]));
                return null;

            case "AcceptButton" or "CancelButton":
                if (!edit.Reset && text != "(none)" && !_controls.ContainsKey(text))
                {
                    return $"'{text}' is not a valid value for IButtonControl.";
                }

                Change(edit.Property, edit.Reset || text == "(none)" ? null : DesignerValue.ReferenceTo(text));
                return null;

            case "ShortcutKeys":
                Change("ShortcutKeys", edit.Reset || text == "None" ? null : DesignerValue.EnumOf(Forms + "Keys", [.. text.Split('+', StringSplitOptions.TrimEntries).Select(k => k == "Ctrl" ? "Control" : k)]));
                return null;

            case "FlatAppearance.BorderSize":
                if (!edit.Reset && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return $"'{text}' is not a valid value for Int32.";
                }

                Change("FlatAppearance.BorderSize", edit.Reset || text == "1" ? null : DesignerValue.Int(int.Parse(text, CultureInfo.InvariantCulture)));
                return null;

            default:
                return null;
        }
    }

    public Task<DesignerChangeResult> SetParentAsync(DesignerSetParentParams parameters, CancellationToken cancellationToken)
    {
        var control = _controls[parameters.Component];
        Children(control.Parent!).Remove(parameters.Component);
        var parent = parameters.Parent ?? _root;
        Children(parent).Insert(0, parameters.Component);
        _controls[parameters.Component] = control with { Parent = parent };
        var (owner, path) = Split(parameters.Parent);
        return Task.FromResult(new DesignerChangeResult([], Render()) { Reparented = new DesignerReparented(parameters.Component, owner, path, null) });
    }

    public Task<DesignerChangeResult> DeleteAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        bool Inside(string? parent) => parent is not null && (parameters.Components.Contains(parent) || parameters.Components.Any(c => parent.StartsWith(c + ".", StringComparison.Ordinal)));
        var removed = parameters.Components.Concat(_controls.Where(c => Inside(c.Value.Parent)).Select(c => c.Key)).ToList();
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

    // ------------------------------------------------------------------ the Properties window (#68: the real host's shapes)

    /// <summary>The font families the real host's FontNameConverter lists (a few of them).</summary>
    public static readonly string[] FontFamilies = ["Arial", "Consolas", "Courier New", "Segoe UI", "Tahoma", "Times New Roman", "Verdana"];

    private static readonly string[] s_cursors = ["AppStarting", "Arrow", "Cross", "Default", "Hand", "Help", "HSplit", "IBeam", "No", "NoMove2D", "SizeAll", "SizeNESW", "SizeNS", "SizeNWSE", "SizeWE", "UpArrow", "VSplit", "WaitCursor"];

    private static readonly string[] s_alignments = ["TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"];

    private static readonly string[] s_units = ["World", "Display", "Pixel", "Point", "Inch", "Document", "Millimeter"];

    private static readonly string[] s_bool = ["True", "False"];

    /// <summary>The ColorConverter's standard values: the known colors, system and web.</summary>
    private static readonly string[] s_colors = [.. Enum.GetNames<System.Drawing.KnownColor>().Where(n => System.Drawing.Color.FromName(n).IsKnownColor)];

    private DesignerValue? Assigned(string component, string property) => _assigned.GetValueOrDefault((component.Length == 0 ? _root : component, property));

    private static (string Name, float Size, string[] Styles) FontParts(DesignerValue? font) =>
        font is { Kind: DesignerValueKinds.New, Arguments: [var name, var size, ..] arguments }
            ? (name.Value ?? "Segoe UI", float.Parse(size.Value ?? "9", CultureInfo.InvariantCulture), arguments.Length > 2 && arguments[2].Kind == DesignerValueKinds.Enum ? [.. arguments[2].Members!.Where(m => m != "Regular")] : [])
            : ("Segoe UI", 9F, []);

    /// <summary>A font as FontConverter writes it: <c>Segoe UI, 9pt, style=Bold, Italic</c>.</summary>
    private static string FontText(DesignerValue? font)
    {
        var (name, size, styles) = FontParts(font);
        return $"{name}, {size.ToString(CultureInfo.InvariantCulture)}pt" + (styles.Length > 0 ? ", style=" + string.Join(", ", styles) : string.Empty);
    }

    /// <summary>A font's code as the classic IDE writes it: <c>new Font("Segoe UI", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0)</c>.</summary>
    private static DesignerValue FontValue(DesignerValue? old, string name, float size, IEnumerable<string> styles)
    {
        string[] order = ["Bold", "Italic", "Underline", "Strikeout"];
        var set = styles.ToHashSet(StringComparer.Ordinal);
        string[] members = [.. order.Where(set.Contains)];
        var charset = old?.Arguments is { Length: > 4 } arguments ? arguments[4] : DesignerValue.Int(0);
        return DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String(name), DesignerValue.Float(size), DesignerValue.EnumOf("System.Drawing.FontStyle", members.Length == 0 ? ["Regular"] : members),
            DesignerValue.EnumOf("System.Drawing.GraphicsUnit", "Point"), charset);
    }

    private static string ColorText(DesignerValue? color, string fallback) => color switch
    {
        { Kind: DesignerValueKinds.Member, Value: { } member } => member,
        { Kind: DesignerValueKinds.Call, Arguments: { } arguments } => string.Join(", ", arguments.Select(a => a.Value)),
        _ => fallback,
    };

    private static DesignerValue? ColorValue(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(p => byte.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return DesignerValue.CallOf("System.Drawing.Color", "FromArgb", [.. parts.Select(p => DesignerValue.Int(int.Parse(p, CultureInfo.InvariantCulture)))]);
        }

        var known = System.Drawing.Color.FromName(text);
        return !known.IsKnownColor ? null : DesignerValue.MemberOf(known.IsSystemColor ? "System.Drawing.SystemColors" : "System.Drawing.Color", known.Name);
    }

    private static DesignerPropertyInfo Bool(string name, string category, string value, bool isSet = false) =>
        new(name, category, "System.Boolean", value) { Editor = DesignerEditors.Boolean, StandardValues = s_bool, IsSet = isSet, CanReset = isSet };

    /// <summary>A font's row with its parts in FontConverter's order (Name, Size, Unit, then the rest by name).</summary>
    private DesignerPropertyInfo FontInfo(string component)
    {
        var font = Assigned(component, "Font");
        var (name, size, styles) = FontParts(font);
        // A font's parts have no default value, so Reset is grayed for them, as on the real host.
        DesignerPropertyInfo Style(string style) => Bool(style, "Misc", styles.Contains(style) ? "True" : "False", font is not null) with { CanReset = false };
        return new DesignerPropertyInfo("Font", "Appearance", "System.Drawing.Font", FontText(font))
        {
            Editor = DesignerEditors.Font,
            IsSet = font is not null,
            CanReset = font is not null,
            Description = "The font used to display text in the control.",
            SubProperties =
            [
                new("Name", "Misc", "System.String", name) { Editor = DesignerEditors.Enum, StandardValues = FontFamilies, IsSet = font is not null },
                new("Size", "Misc", "System.Single", size.ToString(CultureInfo.InvariantCulture)) { IsSet = font is not null },
                new("Unit", "Misc", "System.Drawing.GraphicsUnit", "Point") { Editor = DesignerEditors.Enum, StandardValues = s_units, IsSet = font is not null },
                Style("Bold"),

                // Settable through FontConverter's CreateInstance, as on the real host (WinFormsDesignerPropertiesHostTests).
                new("GdiCharSet", "Misc", "System.Byte", Charset(font).ToString(CultureInfo.InvariantCulture)) { IsSet = font is not null },
                Bool("GdiVerticalFont", "Misc", "False", font is not null) with { CanReset = false },
                Style("Italic"),
                Style("Strikeout"),
                Style("Underline"),
            ],
        };
    }

    /// <summary>A font's GdiCharSet: its code's fifth argument.</summary>
    private static int Charset(DesignerValue? font) => font?.Arguments is { Length: > 4 } arguments && int.TryParse(arguments[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var charset) ? charset : 0;

    private DesignerPropertyInfo ColorInfo(string component, string property, string fallback)
    {
        var value = Assigned(component, property);
        return new DesignerPropertyInfo(property, "Appearance", "System.Drawing.Color", ColorText(value, fallback))
        {
            Editor = DesignerEditors.Color,
            StandardValues = s_colors,
            IsSet = value is not null,
            CanReset = value is not null,
            Description = property == "BackColor" ? "The background color of the component." : "The foreground color of this component, which is used to display text.",
        };
    }

    /// <summary>The selected components' properties merged as the real host merges them (#68: shared properties, mixed values blank, parts merged).</summary>
    public Task<DesignerPropertiesResult> PropertiesAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        DesignerPropertyInfo[]? merged = null;
        foreach (var component in parameters.Components.DefaultIfEmpty(string.Empty))
        {
            var infos = PropertiesOf(component);
            merged = merged is null ? infos : DesignerPropertyMerge.Merge(merged, infos);
        }

        return Task.FromResult(new DesignerPropertiesResult(merged!));
    }

    private DesignerPropertyInfo[] PropertiesOf(string name)
    {
        if (_items.TryGetValue(name, out var item))
        {
            List<DesignerPropertyInfo> itemRows =
            [
                new("Name", "Design", "System.String", name) { IsSet = true },
                new("Text", "Appearance", "System.String", item.Text ?? string.Empty) { IsSet = true, CanReset = true, Editor = DesignerEditors.Multiline },
                FontInfo(name),
            ];
            if (item.Type.EndsWith("ToolStripMenuItem", StringComparison.Ordinal))
            {
                var keys = Assigned(name, "ShortcutKeys");
                itemRows.Add(new("ShortcutKeys", "Misc", Forms + "Keys", keys?.Members is { } members ? string.Join("+", members.Select(m => m == "Control" ? "Ctrl" : m)) : "None")
                {
                    Editor = DesignerEditors.ShortcutKeys,
                    StandardValues = ["None", "A", "B", "C", "N", "O", "S", "F1", "F2", "Delete"],
                    IsSet = keys is not null,
                    CanReset = keys is not null,
                });
            }

            return [.. itemRows];
        }

        var control = _controls.GetValueOrDefault(name);
        var rows = new List<DesignerPropertyInfo>
        {
            new("Name", "Design", "System.String", name.Length == 0 ? _root : name) { IsSet = true, IsReadOnly = name.Length == 0, Description = "The name of the component in the code." },
        };
        if (name.Length == 0)
        {
            rows.Add(new("Localizable", "Design", "System.Boolean", LastLoad?.Localizable == true ? "True" : "False") { Editor = DesignerEditors.Boolean, StandardValues = ["True", "False"] });

            // A component reference (D41 question 17): "(none)", then the form's buttons by name, an exclusive list.
            string[] buttons = ["(none)", .. _controls.Where(c => c.Value.Type == Forms + "Button").Select(c => c.Key).Order(StringComparer.Ordinal)];
            foreach (var reference in new[] { "AcceptButton", "CancelButton" })
            {
                var value = Assigned(name, reference);
                rows.Add(new(reference, "Misc", Forms + "IButtonControl", value?.Value ?? "(none)")
                {
                    Editor = DesignerEditors.Enum,
                    StandardValues = buttons,
                    IsSet = value is not null,
                    CanReset = value is not null,
                    Description = reference == "AcceptButton"
                        ? "The accept button of the form. If this is set, the button is 'clicked' whenever the user presses the 'ENTER' key."
                        : "The cancel button of the form. If this is set, the button is 'clicked' whenever the user presses the 'ESC' key.",
                });
            }
        }

        if (name.Length == 0 || control is not null)
        {
            rows.Add(new("Locked", "Design", "System.Boolean", _locked.Contains(name.Length == 0 ? _root : name) ? "True" : "False") { Editor = DesignerEditors.Boolean, StandardValues = ["True", "False"] });
        }

        if (control is null)
        {
            return [.. rows];
        }

        var isTextBox = control.Type.EndsWith("TextBox", StringComparison.Ordinal);
        var enabled = Assigned(name, "Enabled");
        var cursor = Assigned(name, "Cursor");
        rows.AddRange(
        [
            new("Text", "Appearance", "System.String", control.Text ?? string.Empty)
            {
                IsSet = true,
                CanReset = true,
                Localizable = LastLoad?.Localizable == true,
                Editor = DesignerEditors.Multiline,
                Description = "The text associated with the control.",
            },
            new("Location", "Layout", "System.Drawing.Point", $"{control.X}, {control.Y}")
            {
                IsSet = true,
                Description = "The coordinates of the upper-left corner of the control relative to the upper-left corner of its container.",
                SubProperties =
                [
                    new("X", "Misc", "System.Int32", control.X.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
                    new("Y", "Misc", "System.Int32", control.Y.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
                ],
            },
            new("Size", "Layout", "System.Drawing.Size", $"{control.W}, {control.H}")
            {
                IsSet = true,
                SubProperties =
                [
                    new("Width", "Misc", "System.Int32", control.W.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
                    new("Height", "Misc", "System.Int32", control.H.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
                ],
            },
            new("TabIndex", "Behavior", "System.Int32", control.TabIndex.ToString(CultureInfo.InvariantCulture)) { IsSet = true },
            Bool("Enabled", "Behavior", enabled?.Value == "false" ? "False" : "True", enabled is not null),
            new("Anchor", "Layout", Forms + "AnchorStyles", control.Anchor) { Editor = DesignerEditors.Anchor, StandardValues = ["None", "Top", "Bottom", "Left", "Right"] },
            new("Dock", "Layout", Forms + "DockStyle", control.Dock) { Editor = DesignerEditors.Dock, StandardValues = ["None", "Top", "Bottom", "Left", "Right", "Fill"] },
            ColorInfo(name, "BackColor", "Control"),
            ColorInfo(name, "ForeColor", "ControlText"),
            FontInfo(name),
            new("Cursor", "Appearance", Forms + "Cursor", cursor?.Value ?? "Default")
            {
                Editor = DesignerEditors.Enum,
                StandardValues = s_cursors,
                ChoicesEditable = true,
                IsSet = cursor is not null,
                CanReset = cursor is not null,
                Description = "The cursor that appears when the pointer moves over the control.",
            },
        ]);
        var align = Assigned(name, "TextAlign");
        rows.Add(isTextBox
            ? new("TextAlign", "Appearance", Forms + "HorizontalAlignment", align?.Members?[0] ?? "Left") { Editor = DesignerEditors.Enum, StandardValues = ["Left", "Right", "Center"], IsSet = align is not null, CanReset = align is not null }
            : new("TextAlign", "Appearance", "System.Drawing.ContentAlignment", align?.Members?[0] ?? "MiddleCenter")
            {
                Editor = DesignerEditors.ContentAlignment,
                StandardValues = s_alignments,
                IsSet = align is not null,
                CanReset = align is not null,
                Description = "The alignment of the text that will be displayed on the control.",
            });
        if (isTextBox)
        {
            rows.Add(new("Lines", "Appearance", "System.String[]", "String[] Array") { Editor = DesignerEditors.Strings, IsSet = Assigned(name, "Lines") is not null, Description = "The lines of text in a multiline edit, as an array of String values." });
        }

        if (control.Type == Forms + "Button")
        {
            var border = Assigned(name, "FlatAppearance.BorderSize");
            rows.Add(new("FlatAppearance", "Appearance", Forms + "FlatButtonAppearance", string.Empty)
            {
                IsReadOnly = true,
                Editor = DesignerEditors.ReadOnly,
                SubProperties =
                [
                    new("BorderColor", "Appearance", "System.Drawing.Color", string.Empty) { Editor = DesignerEditors.Color, StandardValues = s_colors },
                    new("BorderSize", "Appearance", "System.Int32", border?.Value ?? "1") { IsSet = border is not null, CanReset = border is not null },
                ],
            });
        }

        if (control.Type.EndsWith("PictureBox", StringComparison.Ordinal) || control.Type.EndsWith("Button", StringComparison.Ordinal))
        {
            rows.Add(new("Image", "Appearance", "System.Drawing.Image", "(none)") { Editor = DesignerEditors.Image });
        }

        // Windows Forms dialogs the IDE doesn't show (D41 question 8): a masked text box's Mask takes text, a numeric
        // up-down's Accelerations doesn't (WinFormsDesignerPropertiesHostTests asserts a data grid view's DefaultCellStyle).
        if (control.Type.EndsWith("MaskedTextBox", StringComparison.Ordinal))
        {
            rows.Add(new("Mask", "Behavior", "System.String", string.Empty) { Editor = DesignerEditors.Modal, Description = "Sets the string governing the input allowed for this control." });
        }

        if (control.Type.EndsWith("NumericUpDown", StringComparison.Ordinal))
        {
            rows.Add(new("Accelerations", "Data", Forms + "NumericUpDownAccelerationCollection", "(Collection)") { Editor = DesignerEditors.Modal, IsReadOnly = true, Description = "The collection of sorted acceleration objects for the NumericUpDown control." });
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

        return [.. rows];
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

    /// <summary>How many collection writes this host answered (#67: a list's items aren't replaced by a stray Type Here).</summary>
    public int SetCollectionCalls { get; private set; }

    public Task<DesignerChangeResult> SetCollectionAsync(DesignerSetCollectionParams parameters, CancellationToken cancellationToken)
    {
        SetCollectionCalls++;
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

    /// <summary>Held, every designer/actions request waits for it (#67: an answer that comes after the selection moved on).</summary>
    public TaskCompletionSource? ActionsGate { get; set; }

    /// <summary>How many designer/actions requests this host answered (#67: the verbs fetched ahead).</summary>
    public int ActionsCalls { get; private set; }

    public async Task<DesignerActionsResult> ActionsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken)
    {
        if (ActionsGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        ActionsCalls++;
        return Actions(parameters);
    }

    private DesignerActionsResult Actions(DesignerComponentsParams parameters)
    {
        var name = parameters.Components.FirstOrDefault() ?? string.Empty;
        var type = _controls.TryGetValue(name, out var control) ? control.Type : _tray.GetValueOrDefault(name) ?? string.Empty;
        List<DesignerActionItem> items = [new(DesignerActionKinds.Method, "DockInParent", control?.Dock == "Fill" ? "Undock in Parent Container" : "Dock in Parent Container")];
        if (type.EndsWith("ListBox", StringComparison.Ordinal))
        {
            items.Insert(0, new(DesignerActionKinds.Header, string.Empty, "Data"));
            items.Insert(1, new(DesignerActionKinds.Property, "UseDataBoundItems", "Use Data Bound Items") { Editor = DesignerEditors.Boolean, Value = "False", StandardValues = ["True", "False"] });
            items.Insert(2, new(DesignerActionKinds.Method, "EditItems", "Edit Items...") { Collection = Forms + "ListBox:Items" });
            items.Add(new(DesignerActionKinds.Method, "ChooseDataSource", "Choose Data Source...") { Unavailable = true });
        }

        // The context menu's verbs, as the real host lists them: a list box's Edit Items... (an action method marked
        // IncludeAsDesignerVerb), a tab control's designer verbs (Remove Tab disabled without a tab page), a menu strip's
        // in the framework's own words, mnemonics included ("&Insert Standard Items"), and an image list's Choose images
        // (which the real host maps to the Images collection editor although its text has no "...").
        List<DesignerActionItem> verbs = [];
        if (type.EndsWith("ListBox", StringComparison.Ordinal))
        {
            verbs.Add(new(DesignerActionKinds.Method, "EditItems", "Edit Items...") { Collection = Forms + "ListBox:Items" });
        }
        else if (type.EndsWith("TabControl", StringComparison.Ordinal))
        {
            verbs.Add(new(DesignerActionKinds.Method, "verb:Add Tab", "Add Tab"));
            verbs.Add(new(DesignerActionKinds.Method, "verb:Remove Tab", "Remove Tab") { Unavailable = Children(name).Count == 0 });
        }
        else if (type == Forms + "MenuStrip")
        {
            verbs.Add(new(DesignerActionKinds.Method, "InvokeEmbedVerb", "Embed in ToolStripContainer"));
            verbs.Add(new(DesignerActionKinds.Method, "InvokeInsertStandardItemsVerb", "&Insert Standard Items"));
            verbs.Add(new(DesignerActionKinds.Method, "InvokeEditItemsVerb", "&Edit Items...") { Collection = Forms + "MenuStrip:Items" });
        }
        else if (type == Forms + "ImageList")
        {
            verbs.Add(new(DesignerActionKinds.Method, "ChooseImages", "Choose images") { Collection = Forms + "ImageList:Images" });
        }

        return new DesignerActionsResult([.. items]) { Verbs = [.. verbs] };
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

    /// <summary>How much wider and taller than the form its frame is drawn (#66: the surface beside the form, where nothing drops).</summary>
    public int FrameExtra { get; set; }

    /// <summary>Whether a build replaced the output this host loaded (#66): the designer starts a new host to show it.</summary>
    public bool Outdated { get; set; }

    /// <summary>Whether the designer ended this host.</summary>
    public bool Disposed { get; private set; }

    /// <summary>The creates this host answered (#66: a gesture made while the designer restarts applies on the new host).</summary>
    public List<DesignerCreateParams> Creates { get; } = [];

    public Task<bool> IsOutdatedAsync(CancellationToken cancellationToken) => Task.FromResult(Outdated);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
