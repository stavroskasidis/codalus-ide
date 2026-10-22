using System.Collections;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The Properties window's half of the session (M41a, M41b, #68): the selected components' properties as the classic
/// IDE's grid shows them, each with its editor (from its type and its UITypeEditor), its choices (read with the design
/// surface's context, so a component reference lists the form's components), whether Reset can put it back, and its
/// parts (TypeConverter.GetProperties, two levels deep); a part is set through its parent's CreateInstance, so the code
/// gets one assignment of the whole value, or on a parent that is a reference type.
/// </summary>
internal sealed partial class DesignerSession
{
    /// <summary>The most standard values a property lists (a computer's font families fit).</summary>
    private const int MaxStandardValues = 2000;

    /// <summary>How deep parts go: a property's parts and theirs, sent with the properties (D41 question 1).</summary>
    // TODO(issue): parts deeper than two levels, read when a row is expanded (a request per expansion) rather than sent
    // with the properties (#68, D41 question 1); a third level's row has no parts meanwhile.
    private const int MaxPartDepth = 2;

    /// <summary>A converter's context for <paramref name="instance"/>'s <paramref name="descriptor"/> on this surface.</summary>
    private DesignerTypeDescriptorContext Context(object? instance, PropertyDescriptor? descriptor) => new(_surface, _host?.Container, instance, descriptor);

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

        DesignerPropertyInfo[]? merged = null;
        foreach (var target in targets)
        {
            DesignerPropertyInfo[] infos = [.. Describe(target, NameOf(target))];
            merged = merged is null ? infos : DesignerPropertyMerge.Merge(merged, infos);
        }

        return new DesignerPropertiesResult(merged!);
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
            // The classic IDE's design-time property of a form: its localizable values go to its resources, per language.
            yield return new DesignerPropertyInfo("Localizable", "Design", "System.Boolean", _localizable ? "True" : "False")
            {
                Description = "The form's localizable properties (texts, sizes, locations, images…) are in its resources, a .resx per language.",
                IsSet = _localizable,
                CanReset = _localizable,
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
                info = Describe(target, descriptor, collections.ContainsKey(descriptor.Name) && provider is null);
                if (collections.TryGetValue(descriptor.Name, out var key) && provider is null)
                {
                    info = info with { Editor = DesignerEditors.Collection, Collection = key, Value = "(Collection)", IsReadOnly = false, SubProperties = null, StandardValues = null };
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                info = new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(descriptor.PropertyType), ex.Message) { IsReadOnly = true, Editor = DesignerEditors.ReadOnly };
            }

            yield return info with { Provider = provider, Localizable = _localizable && descriptor.IsLocalizable };
        }

        // (DataBindings): the bindable properties, bound to a binding source's member (flat; the IDE nests them).
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
                    CanReset = binding is not null,
                    Editor = DesignerEditors.Enum,
                    StandardValues = choices.Contains(current) ? choices : [.. choices, current],
                    DataBinding = true,
                };
            }
        }
    }

    /// <summary>
    /// A property of <paramref name="target"/> (or, for a part, of its parent's value): its text, editor, choices,
    /// Reset and parts. <paramref name="partWritable"/> is null for a component's own property, else whether the part
    /// can be set through its parent (CreateInstance, or a parent that is a reference type).
    /// </summary>
    private DesignerPropertyInfo Describe(object target, PropertyDescriptor descriptor, bool hasCollectionEditor = false, int depth = 0, IReadOnlyList<Type>? path = null, bool? partWritable = null, bool? parentIsSet = null)
    {
        var type = Nullable.GetUnderlyingType(descriptor.PropertyType) ?? descriptor.PropertyType;
        var context = Context(target, descriptor);
        var converter = descriptor.Converter;
        var value = descriptor.GetValue(target);
        string? text;
        try
        {
            // A null value as the converter writes it, as its standard values are written (ReferenceConverter's "(none)",
            // StringConverter's ""), so an unset ContextMenuStrip is the "(none)" of its own list.
            text = value is null ? NullText(converter, context) : converter.ConvertToString(context, CultureInfo.InvariantCulture, value);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            text = value?.ToString();
        }

        var isSet = parentIsSet ?? ShouldSerialize(descriptor, target);
        var writable = partWritable ?? !descriptor.IsReadOnly;
        if (partWritable is null && writable && (typeof(Image).IsAssignableFrom(type) || typeof(Icon).IsAssignableFrom(type)))
        {
            // An image: imported into the resources, or picked from the project's (the editor shows its type, as the classic IDE does).
            return new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(type), value is null ? "(none)" : DesignerTypes.NameOf(value.GetType()))
            {
                Description = descriptor.Description,
                IsSet = isSet,
                CanReset = CanReset(descriptor, target),
                Editor = DesignerEditors.Image,
            };
        }

        var takesText = converter.CanConvertFrom(context, typeof(string));
        var settable = writable && takesText && descriptor.SerializationVisibility != DesignerSerializationVisibility.Content;
        var fromEditor = type == typeof(bool) || type == typeof(AnchorStyles) || type == typeof(DockStyle) ? null : EditorOf(descriptor, context, hasCollectionEditor);

        // A string array's editor sets it through a value, not text (a text box's Lines).
        var settableAsValue = !settable && writable && fromEditor == DesignerEditors.Strings && descriptor.SerializationVisibility != DesignerSerializationVisibility.Content;

        string[]? standard = null;
        var choicesEditable = false;
        if (settable || settableAsValue)
        {
            (standard, choicesEditable) = StandardValuesOf(converter, context, descriptor.Name);
        }

        if (value is null && string.IsNullOrEmpty(text) && standard?.Contains(NoneText) == true)
        {
            text = NoneText; // a null standard value the converter writes as nothing is listed as "(none)" (StandardValuesOf)
        }

        var editor = !settable && !settableAsValue ? (fromEditor == DesignerEditors.Modal ? DesignerEditors.Modal : DesignerEditors.ReadOnly)
            : type == typeof(bool) ? DesignerEditors.Boolean
            : type == typeof(AnchorStyles) ? DesignerEditors.Anchor
            : type == typeof(DockStyle) ? DesignerEditors.Dock

            // The UITypeEditor before flags and enums: Keys is a flags enum (ShortcutKeys), ContentAlignment an enum (TextAlign).
            : fromEditor is { } fromType ? fromType
            : type.IsEnum && type.IsDefined(typeof(FlagsAttribute), inherit: false) ? DesignerEditors.Flags
            : type.IsEnum ? DesignerEditors.Enum
            : type == typeof(Color) ? DesignerEditors.Color
            : type == typeof(Font) ? DesignerEditors.Font

            // Any other property with standard values is a choice (AcceptButton, ContextMenuStrip, Cursor, a font's Name).
            : standard is { Length: > 0 } ? DesignerEditors.Enum
            : DesignerEditors.Text;
        if (type.IsEnum && editor is DesignerEditors.Flags or DesignerEditors.Anchor)
        {
            standard = Enum.GetNames(type);
            choicesEditable = false;
        }

        return new DesignerPropertyInfo(descriptor.Name, descriptor.Category, DesignerTypes.NameOf(type), text)
        {
            Description = descriptor.Description,
            IsReadOnly = !settable && !settableAsValue,
            IsSet = isSet,
            CanReset = partWritable is null ? CanReset(descriptor, target) : PartCanReset(descriptor, target, partWritable.Value),
            Editor = editor,
            StandardValues = standard,
            ChoicesEditable = choicesEditable,
            SubProperties = editor == DesignerEditors.Collection || fromEditor == DesignerEditors.Collection ? null : PartsOf(descriptor, context, value, type, settable, isSet, depth, path),
        };
    }

    private static bool CanReset(PropertyDescriptor descriptor, object target)
    {
        try
        {
            return !descriptor.IsReadOnly && descriptor.CanResetValue(target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>A part can be reset when it has a default value, or its parent is a reference type that resets it.</summary>
    private static bool PartCanReset(PropertyDescriptor descriptor, object parent, bool writable) =>
        writable && (descriptor.Attributes[typeof(DefaultValueAttribute)] is DefaultValueAttribute || (!parent.GetType().IsValueType && CanReset(descriptor, parent)));

    /// <summary>The editor kind the property's UITypeEditor stands for, or null (none, or one that failed to load).</summary>
    private static string? EditorOf(PropertyDescriptor descriptor, ITypeDescriptorContext context, bool hasCollectionEditor)
    {
        try
        {
            if (descriptor.GetEditor(typeof(UITypeEditor)) is not UITypeEditor editor)
            {
                return null;
            }

            var names = new List<string>();
            for (var type = editor.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                names.Add(type.FullName ?? type.Name);
            }

            return DesignerEditors.FromEditorType(names, editor.GetEditStyle(context) == UITypeEditorEditStyle.Modal, hasCollectionEditor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A vendor control's editor whose assembly doesn't load: the property keeps the editor its type gives it.
            Console.Error.WriteLine($"[designer] {descriptor.Name}'s editor couldn't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>What a list of standard values calls a null one the converter writes as nothing.</summary>
    private const string NoneText = "(none)";

    /// <summary>A null value's text from its converter ("(none)" for a component reference), or "" when the converter can't write one.</summary>
    private static string NullText(TypeConverter converter, ITypeDescriptorContext context)
    {
        try
        {
            return converter.ConvertToString(context, CultureInfo.InvariantCulture, null) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty; // a vendor converter that doesn't take null
        }
    }

    /// <summary>The converter's standard values as text (a null one as the converter's text, or "(none)"), and whether a value outside them can be typed.</summary>
    private static (string[]? Values, bool Editable) StandardValuesOf(TypeConverter converter, ITypeDescriptorContext context, string name)
    {
        try
        {
            if (!converter.GetStandardValuesSupported(context) || converter.GetStandardValues(context) is not { Count: > 0 } values)
            {
                return (null, false);
            }

            var list = new List<string>();
            foreach (var standard in values)
            {
                if (list.Count == MaxStandardValues)
                {
                    Console.Error.WriteLine($"[designer] {name} lists more than {MaxStandardValues} standard values; the rest are left out.");
                    break;
                }

                string? text;
                try
                {
                    text = converter.ConvertToString(context, CultureInfo.InvariantCulture, standard);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    text = standard?.ToString();
                }

                list.Add(standard is null && string.IsNullOrEmpty(text) ? NoneText : text ?? string.Empty);
            }

            return ([.. list.Distinct(StringComparer.Ordinal)], !converter.GetStandardValuesExclusive(context));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[designer] {name}'s standard values couldn't be read: {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>
    /// A value's parts in its converter's order (Font's Name, Size, Unit, then the rest by name), two levels deep, when
    /// its converter lists them; none for a collection, a string, a null value, or a type already on the way down.
    /// </summary>
    private DesignerPropertyInfo[]? PartsOf(PropertyDescriptor descriptor, ITypeDescriptorContext context, object? value, Type type, bool settable, bool isSet, int depth, IReadOnlyList<Type>? path)
    {
        if (depth >= MaxPartDepth || value is null || value is string || value is IEnumerable || path?.Contains(type) == true)
        {
            return null;
        }

        try
        {
            var converter = descriptor.Converter;
            if (!converter.GetPropertiesSupported(context) || converter.GetProperties(context, value, [BrowsableAttribute.Yes]) is not { Count: > 0 } parts)
            {
                return null;
            }

            var createInstance = settable && converter.GetCreateInstanceSupported(context);
            var reference = !value.GetType().IsValueType;
            var parts2 = new List<DesignerPropertyInfo>();
            IReadOnlyList<Type> down = [.. path ?? [], type];
            foreach (PropertyDescriptor part in parts)
            {
                var writable = createInstance || (reference && !part.IsReadOnly);
                try
                {
                    parts2.Add(Describe(value, part, depth: depth + 1, path: down, partWritable: writable, parentIsSet: isSet));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    parts2.Add(new DesignerPropertyInfo(part.Name, part.Category, DesignerTypes.NameOf(part.PropertyType), ex.Message) { IsReadOnly = true, Editor = DesignerEditors.ReadOnly });
                }
            }

            return [.. parts2];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[designer] {descriptor.Name}'s parts couldn't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A part set (#68: <c>Font.Bold</c>, <c>Location.X</c>, <c>Padding.Left</c>, <c>FlatAppearance.BorderSize</c>): its
    /// new value converted with its own converter (or a structured value, or its default for a reset), then each parent
    /// built again through its converter's CreateInstance from every part's current value (with the parent's context,
    /// so PaddingConverter tells an All edit from a side's), up to the component's property, which is set once; a parent
    /// that is a reference type takes the part itself. Returns the converter's message when the value isn't valid.
    /// </summary>
    private string? SetSubProperty(object component, DesignerPropertyEdit edit)
    {
        var names = edit.Property.Split('.');
        var top = TypeDescriptor.GetProperties(component)[names[0]];
        if (top is null)
        {
            return $"{names[0]} isn't a property of {edit.Component}.";
        }

        // (owner, descriptor, value) at each level, the parts looked up through the parent's converter as they were described.
        var chain = new List<(object Owner, PropertyDescriptor Descriptor, object? Value)> { (component, top, top.GetValue(component)) };
        for (var i = 1; i < names.Length; i++)
        {
            var (owner, descriptor, value) = chain[^1];
            if (value is null)
            {
                return $"{string.Join('.', names[..i])} is null.";
            }

            var parts = descriptor.Converter.GetPropertiesSupported(Context(owner, descriptor))
                ? descriptor.Converter.GetProperties(Context(owner, descriptor), value, [BrowsableAttribute.Yes])
                : TypeDescriptor.GetProperties(value);
            if (parts?[names[i]] is not { } part)
            {
                return $"{string.Join('.', names[..(i + 1)])} isn't a part of {edit.Component}.";
            }

            chain.Add((value, part, part.GetValue(value)));
        }

        var (leafOwner, leaf, _) = chain[^1];
        object? newValue;
        try
        {
            if (edit.Reset)
            {
                if (leaf.Attributes[typeof(DefaultValueAttribute)] is DefaultValueAttribute defaultValue)
                {
                    newValue = defaultValue.Value;
                }
                else if (!leafOwner.GetType().IsValueType && leaf.CanResetValue(leafOwner))
                {
                    leaf.ResetValue(leafOwner);
                    return null;
                }
                else
                {
                    return $"{edit.Property} has no default value.";
                }
            }
            else
            {
                newValue = edit.Value is { } structured
                    ? _values.Evaluate(structured, leaf.PropertyType)
                    : leaf.Converter.ConvertFrom(Context(leafOwner, leaf), CultureInfo.InvariantCulture, edit.Text ?? string.Empty);
            }

            // Bubble up: each parent's new value from its parts.
            for (var i = chain.Count - 1; i >= 1; i--)
            {
                var (holder, part, _) = chain[i];
                var (parentOwner, parentDescriptor, parentValue) = chain[i - 1];
                var parentContext = Context(parentOwner, parentDescriptor);
                var converter = parentDescriptor.Converter;
                if (converter.GetCreateInstanceSupported(parentContext))
                {
                    var values = new Hashtable();
                    var parts = converter.GetProperties(parentContext, parentValue!, [BrowsableAttribute.Yes]) ?? TypeDescriptor.GetProperties(parentValue!);
                    foreach (PropertyDescriptor sibling in parts)
                    {
                        values[sibling.Name] = sibling.GetValue(parentValue);
                    }

                    values[part.Name] = newValue;
                    newValue = converter.CreateInstance(parentContext, values);
                    if (newValue is null)
                    {
                        return $"{edit.Property} can't be set.";
                    }
                }
                else if (!holder.GetType().IsValueType && !part.IsReadOnly)
                {
                    // A reference type's part (FlatAppearance.BorderSize): set on it, nothing above changes.
                    part.SetValue(holder, newValue);
                    return null;
                }
                else
                {
                    return $"{edit.Property} can't be set.";
                }
            }

            top.SetValue(component, newValue);
            return null;
        }
        catch (Exception ex) when (IsConversionError(ex))
        {
            return ConversionMessage(ex);
        }
    }

    /// <summary>A converter's or a setter's refusal of a value (not a bug of the host).</summary>
    private static bool IsConversionError(Exception ex) =>
        ex is FormatException or NotSupportedException or ArgumentException or InvalidCastException or OverflowException or System.Runtime.InteropServices.ExternalException
        || (ex is System.Reflection.TargetInvocationException or InvalidOperationException && ex.InnerException is { } inner && IsConversionError(inner));

    /// <summary>The message the invalid-value dialog shows: the converter's ("abc is not a valid value for Int32.").</summary>
    private static string ConversionMessage(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? ConversionMessage(inner) : ex.Message;

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
