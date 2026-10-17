using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The surface's state as the code says it (M41b, ADR 0073), captured before and after every change; their
/// difference is what the change writes: properties (and the values only the resources can hold, an image), extender
/// properties, data bindings, the components that came and went, the controls' parents, their order in each parent
/// (the z-order, a tab control's pages), the collections, and a localizable form's resources.
/// </summary>
internal sealed partial class DesignerSession
{
    private readonly Dictionary<ImageList, int> _imageListVersions = [];

    /// <summary>A property as the code would set it: its value as code (null when only the resources can hold it), the object, whether it's localizable, its declared type.</summary>
    private sealed record SerializedProperty(string Name, DesignerValue? Value, object? Raw, bool Localizable, Type Type);

    private sealed record ParentInfo(string Component, string? Path, int[]? Cell)
    {
        /// <summary>The parent as the IDE names it: empty for the root, <c>panel1</c>, <c>splitContainer1.Panel1</c>.</summary>
        public string Key => Path is null ? Component : Component + "." + Path;
    }

    private sealed record CollectionState(DesignerCollectionInfo Info, DesignerValue[] Items, DesignerLocal[] Locals);

    private sealed class SurfaceState
    {
        public List<string> Order { get; } = [];

        public Dictionary<string, IComponent> Components { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Component, string Property), SerializedProperty> Properties { get; } = [];

        /// <summary>What a resource-only value is compared by: the object, or an image list's version.</summary>
        public Dictionary<(string Component, string Property), object?> Tokens { get; } = [];

        public Dictionary<(string Component, string Property, string Provider), (DesignerValue Value, object? Raw, bool Localizable)> Extenders { get; } = [];

        public Dictionary<(string Component, string Property), DesignerValue> Bindings { get; } = [];

        public Dictionary<string, ParentInfo> Parents { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Component, string Property), CollectionState> Collections { get; } = [];

        public List<string> ChildrenOf(string key)
        {
            if (!Children.TryGetValue(key, out var list))
            {
                Children[key] = list = [];
            }

            return list;
        }
    }

    // ------------------------------------------------------------------ capturing

    private SurfaceState Capture()
    {
        var state = new SurfaceState();
        foreach (IComponent component in Host.Container.Components)
        {
            if (NameOf(component) is not { } name)
            {
                continue;
            }

            state.Order.Add(name);
            state.Components[name] = component;
            foreach (var property in Serialize(component))
            {
                state.Properties[(name, property.Name)] = property;
                if (property.Value is null)
                {
                    state.Tokens[(name, property.Name)] = property.Raw is ImageListStreamer && component is ImageList list ? _imageListVersions.GetValueOrDefault(list) : property.Raw;
                }
            }

            foreach (var (property, provider, value, raw, localizable) in Extenders(component))
            {
                state.Extenders[(name, property, provider)] = (value, raw, localizable);
            }

            if (component is Control control)
            {
                foreach (Binding binding in control.DataBindings)
                {
                    if (BindingValue(binding) is { } value)
                    {
                        state.Bindings[(name, binding.PropertyName)] = value;
                    }
                }
            }

            foreach (var (info, collection) in CollectionsOf(component))
            {
                if (CollectionStateOf(info, collection) is { } items)
                {
                    state.Collections[(name, info.Property)] = items;
                }
            }
        }

        void Walk(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (NameOf(child) is { Length: > 0 } childName && ParentOf(child) is { } info)
                {
                    state.Parents[childName] = info;
                    state.ChildrenOf(info.Key).Add(childName);
                }

                Walk(child);
            }
        }

        Walk(Root);
        return state;
    }

    /// <summary>A control's parent as the code says it: the root, a container, or a container's panel (with a table's cell).</summary>
    private ParentInfo? ParentOf(Control control)
    {
        if (control.Parent is not { } parent)
        {
            return null;
        }

        int[]? cell = null;
        if (parent is TableLayoutPanel table)
        {
            var position = table.GetPositionFromControl(control);
            cell = [position.Column, position.Row];
        }

        if (NameOf(parent) is { } name)
        {
            return new ParentInfo(name, null, cell);
        }

        return parent.Parent is { } owner && NameOf(owner) is { } ownerName && PartName(owner, parent) is { } path ? new ParentInfo(ownerName, path, cell) : null;
    }

    /// <summary>A container's own panel that its designer exposes (<c>Panel1</c> of a SplitContainer, a ToolStripContainer's panels), or null.</summary>
    private static string? PartName(Control owner, Control part) => part.Site switch
    {
        INestedSite { Name: { Length: > 0 } nested } => nested,
        _ when owner is SplitContainer split => ReferenceEquals(split.Panel1, part) ? "Panel1" : ReferenceEquals(split.Panel2, part) ? "Panel2" : null,
        _ => null,
    };

    /// <summary>
    /// A component's properties that the code sets: visible to serialization, not design-only or extended by another
    /// component, different from their defaults (<c>ShouldSerializeValue</c>); content properties one level down
    /// (<c>FlatAppearance.BorderSize</c>, <c>Panel1.BackColor</c>); collections are left to the code's calls. A value
    /// code can't say (an image) is kept with no code: it goes to the resources.
    /// </summary>
    private List<SerializedProperty> Serialize(IComponent component)
    {
        var result = new List<SerializedProperty>();
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

                    if (descriptor.IsReadOnly || !descriptor.ShouldSerializeValue(owner))
                    {
                        continue;
                    }

                    var raw = descriptor.GetValue(owner);
                    var value = _values.Serialize(raw);
                    if (value is null && !ResourceValues.IsResourceOnly(raw))
                    {
                        continue;
                    }

                    result.Add(new SerializedProperty(prefix + descriptor.Name, value, raw, depth == 0 && descriptor.IsLocalizable, descriptor.PropertyType));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"{prefix}{descriptor.Name} couldn't be read: {ex.Message}");
                }
            }
        }

        Properties(component, string.Empty, 0);

        // An image list's images: its stream, in the resources (its keys are its collection).
        if (component is ImageList { Images.Count: > 0 } list)
        {
            result.Add(new SerializedProperty("ImageStream", null, list.ImageStream, false, typeof(ImageListStreamer)));
        }

        if (component is Control or ToolStripItem or DataGridViewColumn && component.Site?.Name is { } name)
        {
            result.Add(new SerializedProperty("Name", DesignerValue.String(name), name, false, typeof(string)));
        }

        return result;
    }

    /// <summary>The extender properties other components give this one (<c>ToolTip on toolTip1</c>) that the code sets.</summary>
    private IEnumerable<(string Property, string Provider, DesignerValue Value, object? Raw, bool Localizable)> Extenders(IComponent component)
    {
        PropertyDescriptorCollection descriptors;
        try
        {
            descriptors = TypeDescriptor.GetProperties(component);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            yield break;
        }

        foreach (PropertyDescriptor descriptor in descriptors)
        {
            if (descriptor.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is not ExtenderProvidedPropertyAttribute { Provider: { } provider }
                || NameOf(provider) is not { Length: > 0 } providerName || descriptor.SerializationVisibility == DesignerSerializationVisibility.Hidden)
            {
                continue;
            }

            DesignerValue? value = null;
            object? raw = null;
            try
            {
                if (descriptor.ShouldSerializeValue(component))
                {
                    raw = descriptor.GetValue(component);
                    value = _values.Serialize(raw);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"{descriptor.Name} on {providerName} couldn't be read: {ex.Message}");
            }

            if (value is not null)
            {
                yield return (descriptor.Name, providerName, value, raw, descriptor.IsLocalizable);
            }
        }
    }

    /// <summary><c>new Binding("Text", bindingSource1, "Name", true)</c>, as Visual Studio writes a data binding (with its update mode when it isn't the default).</summary>
    private DesignerValue? BindingValue(Binding binding)
    {
        if (binding.DataSource is not { } source || NameOf(source) is not { } sourceName)
        {
            return null;
        }

        DesignerValue[] arguments =
        [
            DesignerValue.String(binding.PropertyName),
            DesignerValue.ReferenceTo(sourceName),
            DesignerValue.String(binding.BindingMemberInfo.BindingMember),
            DesignerValue.Bool(binding.FormattingEnabled),
        ];
        if (binding.DataSourceUpdateMode != DataSourceUpdateMode.OnValidation)
        {
            arguments = [.. arguments, DesignerValue.EnumOf("System.Windows.Forms.DataSourceUpdateMode", binding.DataSourceUpdateMode.ToString())];
        }

        return DesignerValue.NewOf("System.Windows.Forms.Binding", arguments);
    }

    // ------------------------------------------------------------------ the difference

    /// <summary>The change from <paramref name="before"/> to now, as code and resources, and the surface after it.</summary>
    private DesignerChangeResult Result(SurfaceState before, string? created = null, string? reparented = null)
    {
        var after = Capture();
        var changes = new List<DesignerPropertyChange>();
        var resources = new List<DesignerResourceEntry>();
        var localized = new HashSet<string>(StringComparer.Ordinal);
        var culture = _culture.Name;
        var added = after.Order.Where(n => !before.Components.ContainsKey(n)).ToList();
        var removed = before.Order.Where(n => !after.Components.ContainsKey(n)).ToList();
        var skip = new HashSet<string>(added.Concat(removed), StringComparer.Ordinal);

        // A localizable property of a localizable form: the culture's resources (the default one's are with ApplyResources).
        void ToResources(string component, string property, object? raw, bool present)
        {
            var key = ResourceValues.Key(component, property);
            if (culture.Length > 0)
            {
                var inherited = _textResources?.InheritedValue(key, _culture);
                resources.Add(new DesignerResourceEntry(key, !present || Equals(inherited, raw) ? null : ResourceValues.Of(key, raw)) { Culture = culture });
                return;
            }

            resources.Add(new DesignerResourceEntry(key, present ? ResourceValues.Of(key, raw) : null));
            if (present)
            {
                localized.Add(component);
            }
        }

        // A value only the resources hold (an image): GetObject in the code, its bytes in the default resources.
        DesignerValue ResourceReference(string component, SerializedProperty property)
        {
            var key = ResourceValues.Key(component, property.Name);
            resources.Add(new DesignerResourceEntry(key, ResourceValues.Of(key, property.Raw)));
            return new DesignerValue { Kind = DesignerValueKinds.Resource, Type = DesignerTypes.NameOf(property.Type), Value = key };
        }

        foreach (var (key, property) in after.Properties)
        {
            if (skip.Contains(key.Component))
            {
                continue;
            }

            before.Properties.TryGetValue(key, out var old);
            var changed = property.Value is null
                ? old is null || !Equals(before.Tokens.GetValueOrDefault(key), after.Tokens.GetValueOrDefault(key))
                : old?.Value is null || !old.Value.SameAs(property.Value);
            if (!changed)
            {
                continue;
            }

            if (_localizable && property.Localizable)
            {
                ToResources(key.Component, key.Property, property.Raw, present: true);
            }
            else
            {
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, property.Value ?? ResourceReference(key.Component, property)));
            }
        }

        foreach (var (key, old) in before.Properties)
        {
            if (skip.Contains(key.Component) || after.Properties.ContainsKey(key))
            {
                continue;
            }

            if (_localizable && old.Localizable)
            {
                ToResources(key.Component, key.Property, null, present: false);
            }
            else
            {
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, null));
                if (old.Value is null)
                {
                    resources.Add(new DesignerResourceEntry(ResourceValues.Key(key.Component, key.Property), null));
                }
            }
        }

        // Extender properties: provider.SetX(component, value); a localizable one's value from the resources (GetString).
        foreach (var (key, (value, raw, localizable)) in after.Extenders)
        {
            if (skip.Contains(key.Component) || (before.Extenders.TryGetValue(key, out var old) && old.Value.SameAs(value)))
            {
                continue;
            }

            if (_localizable && localizable && raw is string text)
            {
                var resourceKey = ResourceValues.Key(key.Component, key.Property);
                ToResources(key.Component, key.Property, text, present: true);
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.String", Value = resourceKey }) { Provider = key.Provider });
                continue;
            }

            changes.Add(new DesignerPropertyChange(key.Component, key.Property, value) { Provider = key.Provider });
        }

        foreach (var key in before.Extenders.Keys.Where(k => !skip.Contains(k.Component) && !after.Extenders.ContainsKey(k)))
        {
            changes.Add(new DesignerPropertyChange(key.Component, key.Property, null) { Provider = key.Provider });
            if (_localizable && before.Extenders[key].Localizable)
            {
                ToResources(key.Component, key.Property, null, present: false);
            }
        }

        foreach (var (key, value) in after.Bindings)
        {
            if (!skip.Contains(key.Component) && (!before.Bindings.TryGetValue(key, out var old) || !old.SameAs(value)))
            {
                changes.Add(new DesignerPropertyChange(key.Component, key.Property, value) { DataBinding = true });
            }
        }

        foreach (var key in before.Bindings.Keys.Where(k => !skip.Contains(k.Component) && !after.Bindings.ContainsKey(k)))
        {
            changes.Add(new DesignerPropertyChange(key.Component, key.Property, null) { DataBinding = true });
        }

        // The components that came: the one created, and what something else created (a smart tag's, a collection's).
        var createdComponents = new List<DesignerCreated>();
        foreach (var name in added)
        {
            createdComponents.Add(Created(after, name, resources, localized));
        }

        // Controls that moved to another parent, and parents whose controls' order changed.
        var moved = new List<DesignerReparented>();
        foreach (var (name, parent) in after.Parents)
        {
            if (!skip.Contains(name) && before.Parents.TryGetValue(name, out var old) && old.Key != parent.Key)
            {
                moved.Add(new DesignerReparented(name, parent.Component, parent.Path, parent.Cell));
            }
        }

        var reordered = new List<DesignerChildOrder>();
        foreach (var (key, children) in after.Children)
        {
            var old = before.Children.GetValueOrDefault(key) ?? [];
            var fresh = children.Where(c => !old.Contains(c) || before.Parents.GetValueOrDefault(c)?.Key != key).ToList();
            var kept = old.Where(children.Contains).ToList();
            var keptOrderChanged = !kept.SequenceEqual(children.Where(kept.Contains));
            var freshNotFirst = fresh.Count > 0 && children.Count > 1 && !(fresh.Count == 1 && children[0] == fresh[0]);
            if (keptOrderChanged || freshNotFirst)
            {
                reordered.Add(new DesignerChildOrder(key, [.. children]));
            }
        }

        // A localizable form's records of its controls: their parent and position when they moved.
        if (_localizable && culture.Length == 0)
        {
            foreach (var move in moved)
            {
                resources.Add(Record(move.Component, "Parent", ParentRecord(after.Parents[move.Component])));
            }

            foreach (var order in reordered)
            {
                for (var i = 0; i < order.Children.Length; i++)
                {
                    if (!added.Contains(order.Children[i]))
                    {
                        resources.Add(Record(order.Children[i], "ZOrder", i.ToString(CultureInfo.InvariantCulture)));
                    }
                }
            }
        }

        // Collections, and the locals they hold (a list view's items).
        var collections = new List<DesignerCollectionChange>();
        var removedLocals = new List<string>();
        foreach (var (key, collection) in after.Collections)
        {
            if (removed.Contains(key.Component))
            {
                continue;
            }

            before.Collections.TryGetValue(key, out var old);
            var oldItems = old?.Items ?? [];
            var newLocals = collection.Locals.Where(l => old?.Locals.FirstOrDefault(o => o.Name == l.Name) is not { } previous || !previous.Value.SameAs(l.Value)).ToArray();
            if (oldItems.Length == collection.Items.Length && oldItems.Zip(collection.Items).All(p => p.First.SameAs(p.Second)) && newLocals.Length == 0)
            {
                continue;
            }

            collections.Add(new DesignerCollectionChange(key.Component, key.Property, collection.Info.Method, collection.Info.ElementType, collection.Items) { Locals = newLocals });
            removedLocals.AddRange(old?.Locals.Select(l => l.Name).Where(n => collection.Locals.All(l => l.Name != n)) ?? []);
        }

        var result = new DesignerChangeResult([.. changes], Render())
        {
            Removed = [.. removed, .. removedLocals],
            Moved = [.. moved.Where(m => m.Component != reparented)],
            Reordered = [.. reordered],
            Collections = [.. collections],
            Resources = [.. resources],
            Localized = [.. localized.Select(c => new DesignerLocalized(c, true))],
        };
        if (created is not null && createdComponents.FirstOrDefault(c => c.Name == created) is { } main)
        {
            result = result with { Created = main, Added = [.. createdComponents.Where(c => c != main)] };
        }
        else
        {
            result = result with { Added = [.. createdComponents] };
        }

        if (reparented is not null && moved.FirstOrDefault(m => m.Component == reparented) is { } move2)
        {
            result = result with { Reparented = move2 };
        }

        return result;
    }

    /// <summary>A new component as the code must say it; a localizable form's localizable properties (and its records) go to the resources.</summary>
    private DesignerCreated Created(SurfaceState after, string name, List<DesignerResourceEntry> resources, HashSet<string> localized)
    {
        var component = after.Components[name];
        var type = component.GetType();
        var arguments = component is not Control && type.GetConstructor([typeof(IContainer)]) is not null ? new[] { DesignerValue.ReferenceTo("components") } : [];
        var parent = after.Parents.GetValueOrDefault(name);
        var properties = new List<DesignerPropertyValue>();
        foreach (var ((owner, _), property) in after.Properties)
        {
            if (owner != name)
            {
                continue;
            }

            var key = ResourceValues.Key(name, property.Name);
            if (_localizable && property.Localizable)
            {
                // A new component's localizable values are the default resources', whatever the language shown.
                resources.Add(new DesignerResourceEntry(key, ResourceValues.Of(key, property.Raw)));
                localized.Add(name);
                continue;
            }

            if (property.Value is null)
            {
                resources.Add(new DesignerResourceEntry(key, ResourceValues.Of(key, property.Raw)));
                properties.Add(new DesignerPropertyValue(property.Name, new DesignerValue { Kind = DesignerValueKinds.Resource, Type = DesignerTypes.NameOf(property.Type), Value = key }));
                continue;
            }

            properties.Add(new DesignerPropertyValue(property.Name, property.Value));
        }

        if (_localizable && component is Control && parent is not null)
        {
            var index = after.Children.GetValueOrDefault(parent.Key)?.IndexOf(name) ?? 0;
            resources.Add(Record(name, "Name", name));
            resources.Add(Record(name, "Type", ResourceValues.TypeName(type)));
            resources.Add(Record(name, "Parent", ParentRecord(parent)));
            resources.Add(Record(name, "ZOrder", index.ToString(CultureInfo.InvariantCulture)));
        }

        return new DesignerCreated(name, DesignerTypes.NameOf(type), arguments, parent?.Component, parent?.Path, parent?.Cell, [.. properties])
        {
            SupportsInitialize = component is ISupportInitialize,
            IsControl = component is Control && parent is not null,
            Index = parent is null ? 0 : after.Children.GetValueOrDefault(parent.Key)?.IndexOf(name) ?? 0,
        };
    }

    /// <summary>One of Visual Studio's records of a localizable form's control (<c>&gt;&gt;okButton.Parent</c>), in the default resources.</summary>
    private static DesignerResourceEntry Record(string component, string record, string value) =>
        new($">>{(component.Length == 0 ? "$this" : component)}.{record}", ResourceValues.Text(value));

    private string ParentRecord(ParentInfo parent) => parent.Component.Length == 0 && parent.Path is null ? "$this" : parent.Key;

    // ------------------------------------------------------------------ Localizable (M41b)

    /// <summary>
    /// The form's Localizable property, as Visual Studio sets it: on, every localizable value moves from the code to the
    /// default resources (each component's section starting with ApplyResources, Visual Studio's records of its controls,
    /// <c>$this.Localizable</c>); off, they move back to the code (images stay in the resources, read with GetObject).
    /// </summary>
    private DesignerChangeResult SetLocalizable(SurfaceState state, bool localizable)
    {
        if (localizable == _localizable)
        {
            return new DesignerChangeResult([], Render());
        }

        _localizable = localizable;
        var changes = new List<DesignerPropertyChange>();
        var resources = new List<DesignerResourceEntry>();
        var localizedComponents = new List<DesignerLocalized>();
        foreach (var name in state.Order)
        {
            var any = false;
            foreach (var ((owner, _), property) in state.Properties)
            {
                if (owner != name || !property.Localizable)
                {
                    continue;
                }

                var key = ResourceValues.Key(name, property.Name);
                any = true;
                if (localizable)
                {
                    resources.Add(new DesignerResourceEntry(key, ResourceValues.Of(key, property.Raw)));
                    changes.Add(new DesignerPropertyChange(name, property.Name, null));
                }
                else if (property.Value is null)
                {
                    changes.Add(new DesignerPropertyChange(name, property.Name, new DesignerValue { Kind = DesignerValueKinds.Resource, Type = DesignerTypes.NameOf(property.Type), Value = key }));
                }
                else
                {
                    changes.Add(new DesignerPropertyChange(name, property.Name, property.Value));
                    resources.Add(new DesignerResourceEntry(key, null));
                }
            }

            if (any || localizable)
            {
                localizedComponents.Add(new DesignerLocalized(name, localizable));
            }

            if (state.Components[name] is Control && (name.Length == 0 || state.Parents.ContainsKey(name)))
            {
                var records = name.Length == 0
                    ? new[] { ("Name", _className), ("Type", ResourceValues.TypeName(Root.GetType())) }
                    : [("Name", name), ("Type", ResourceValues.TypeName(state.Components[name].GetType())), ("Parent", ParentRecord(state.Parents[name])),
                        ("ZOrder", state.Children[state.Parents[name].Key].IndexOf(name).ToString(CultureInfo.InvariantCulture))];
                foreach (var (record, value) in records)
                {
                    var entry = Record(name, record, value);
                    resources.Add(localizable ? entry : entry with { Resource = null });
                }
            }
        }

        resources.Add(new DesignerResourceEntry("$this.Localizable", localizable ? ResourceValues.Of("$this.Localizable", true) : null) { Metadata = true });
        return new DesignerChangeResult([.. changes], Render()) { Resources = [.. resources], Localized = [.. localizedComponents] };
    }
}
