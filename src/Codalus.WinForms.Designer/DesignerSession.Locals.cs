using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// A control library's collections of objects that aren't components (#461, ADR 0106: DevExpress's grid levels, a
/// <c>GridControl</c>'s <c>LevelTree.Nodes</c> of <c>GridLevelNode</c>s), as the classic IDE writes them: each object a
/// local declared at the top of <c>InitializeComponent</c> (<c>GridLevelNode gridLevelNode1 = new GridLevelNode();</c>),
/// its properties set and its own collections filled in the owner's section, then the owner's collection's statement.
/// </summary>
internal sealed partial class DesignerSession
{
    /// <summary>How deep a library's locals nest (a grid's levels within levels).</summary>
    private const int MaxLocalDepth = 16;

    /// <summary>
    /// The collection's state, its locals and their statements (children before their parents); a new object gets
    /// a name as the classic IDE names it (<c>gridLevelNode1</c>). Null when the code can't say an object or a value it
    /// holds (another library object, an image): then the code stays as it is.
    /// </summary>
    private CollectionState? LibraryLocals(DesignerCollectionInfo info, object collection)
    {
        var items = new List<DesignerValue>();
        var locals = new List<DesignerLocal>();
        var statements = new List<DesignerStatement>();
        foreach (var item in (IEnumerable)collection)
        {
            if (item is null || Item(item, locals, statements, 0) is not { } value)
            {
                return null;
            }

            items.Add(value);
        }

        return new CollectionState(info, [.. items], [.. locals]) { Statements = [.. statements], Rewrite = true };
    }

    /// <summary>
    /// An item as the collection's statement says it: inline when its type converter creates it whole
    /// (<c>new GridGroupSummaryItem(SummaryItemType.Count, "", null, "")</c>, as the classic IDE writes it), else a
    /// reference to its local (<see cref="Local"/>); null when the code can't say it.
    /// </summary>
    private DesignerValue? Item(object item, List<DesignerLocal> locals, List<DesignerStatement> statements, int depth) =>
        item is not IComponent && IsLocalType(item.GetType()) && Creation(item) is (var value, true) ? value
        : Local(item, locals, statements, depth) is { } name ? DesignerValue.ReferenceTo(name) : null;

    /// <summary>Declares <paramref name="item"/> (its name), adding it to <paramref name="locals"/> and what sets it up to <paramref name="statements"/>; null when the code can't say it.</summary>
    private string? Local(object item, List<DesignerLocal> locals, List<DesignerStatement> statements, int depth)
    {
        if (depth > MaxLocalDepth || item is IComponent || !IsLocalType(item.GetType()) || Creation(item) is not var (value, complete))
        {
            return null;
        }

        var name = NameOf(item);
        if (name is null)
        {
            name = NewLocalName(LocalStem(item.GetType()));
            _locals[name] = item;
        }

        if (locals.Any(l => l.Name == name))
        {
            return null; // the same object twice: not what the code can say
        }

        locals.Add(new DesignerLocal(name, value));
        if (complete)
        {
            return name;
        }

        var own = new List<DesignerStatement>();
        PropertyDescriptorCollection properties;
        try
        {
            properties = TypeDescriptor.GetProperties(item, [DesignOnlyAttribute.No]).Sort();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }

        foreach (PropertyDescriptor descriptor in properties)
        {
            if (descriptor.Name == "Name" || descriptor.SerializationVisibility == DesignerSerializationVisibility.Hidden)
            {
                continue;
            }

            try
            {
                if (descriptor.SerializationVisibility == DesignerSerializationVisibility.Content)
                {
                    if (descriptor.GetValue(item) is not IEnumerable nested || nested is string)
                    {
                        return null; // an object's parts: not said here
                    }

                    if (nested.Cast<object?>().Any() && !Nested(name, descriptor.Name, nested, locals, statements, own, depth))
                    {
                        return null;
                    }

                    continue;
                }

                if (descriptor.IsReadOnly || !ShouldSerialize(descriptor, item))
                {
                    continue;
                }

                if (_values.Serialize(descriptor.GetValue(item)) is not { } serialized)
                {
                    return null;
                }

                own.Add(new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = name, Member = descriptor.Name, Value = serialized });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[designer] {name}.{descriptor.Name} couldn't be read: {ex.Message}");
                return null;
            }
        }

        statements.AddRange(own);
        return name;
    }

    /// <summary>An object's own collection of locals (a level's <c>Nodes</c>): its items declared first, then the call that fills it.</summary>
    private bool Nested(string owner, string property, IEnumerable collection, List<DesignerLocal> locals, List<DesignerStatement> statements, List<DesignerStatement> own, int depth)
    {
        if (CollectionMethod(collection.GetType()) is not var (method, element) || typeof(IComponent).IsAssignableFrom(element))
        {
            return false;
        }

        var references = new List<DesignerValue>();
        foreach (var child in collection)
        {
            if (child is null || Item(child, locals, statements, depth + 1) is not { } value)
            {
                return false;
            }

            references.Add(value);
        }

        if (method == "AddRange")
        {
            own.Add(new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = owner, Member = property + ".AddRange", Arguments = [DesignerValue.ArrayOf(DesignerTypes.NameOf(element), [.. references])] });
        }
        else
        {
            own.AddRange(references.Select(r => new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = owner, Member = property + ".Add", Arguments = [r] }));
        }

        return true;
    }

    /// <summary>
    /// How the code creates <paramref name="item"/>: its type converter's constructor (<c>InstanceDescriptor</c>) and
    /// whether that says it all, else <c>new T()</c>; null when neither.
    /// </summary>
    private (DesignerValue Value, bool Complete)? Creation(object item)
    {
        var type = item.GetType();
        try
        {
            var converter = TypeDescriptor.GetConverter(item);
            if (converter.CanConvertTo(typeof(InstanceDescriptor)) && converter.ConvertTo(item, typeof(InstanceDescriptor)) is InstanceDescriptor { MemberInfo: ConstructorInfo constructor } descriptor)
            {
                var arguments = new List<DesignerValue>();
                foreach (var argument in descriptor.Arguments)
                {
                    if (_values.Serialize(argument) is not { } serialized)
                    {
                        return null;
                    }

                    arguments.Add(serialized);
                }

                return (DesignerValue.NewOf(DesignerTypes.NameOf(constructor.DeclaringType!), [.. arguments]), descriptor.IsComplete);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[designer] {type.Name}'s converter failed: {ex.Message}");
        }

        return type.GetConstructor(Type.EmptyTypes) is { IsPublic: true } ? (DesignerValue.NewOf(DesignerTypes.NameOf(type)), false) : null;
    }

    /// <summary>A local's name without its number, as the classic IDE names it: the type's name, camel-cased (<c>gridLevelNode</c>).</summary>
    private static string LocalStem(Type type)
    {
        var name = type.Name;
        if (name.IndexOf('`', StringComparison.Ordinal) is var tick and > 0)
        {
            name = name[..tick];
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Two statements the code would write the same.</summary>
    private static bool SameStatement(DesignerStatement a, DesignerStatement b) =>
        a.Kind == b.Kind && a.Target == b.Target && a.Member == b.Member
        && (a.Value is null ? b.Value is null : a.Value.SameAs(b.Value))
        && (a.Arguments ?? []).Length == (b.Arguments ?? []).Length
        && (a.Arguments ?? []).Zip(b.Arguments ?? []).All(p => p.First.SameAs(p.Second));
}
