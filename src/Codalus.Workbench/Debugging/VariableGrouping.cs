using Codalus.Debugging;
using Codalus.Debugging.Companion;

namespace Codalus.Workbench.Debugging;

/// <summary>What a row of the data tip or a variable window is (D44): its icon, and where it goes.</summary>
public enum MemberKind
{
    /// <summary>Nothing says (no metadata: a remote program's): the icon is guessed from the name.</summary>
    Unknown,
    Property,
    Field,
    Constant,
    Event,

    /// <summary>An element of an array or a collection view (<c>[0]</c>), or a dictionary's entry.</summary>
    Element,

    /// <summary>A library type's private, protected and internal members.</summary>
    NonPublicGroup,

    /// <summary>netcoredbg's own "Static members" node.</summary>
    StaticGroup,

    /// <summary>A collection view's object as netcoredbg lists it.</summary>
    RawView,

    /// <summary>A range of a long array's elements, <c>[1000..1999]</c> (M56): they're read when it's expanded.</summary>
    Range,
}

/// <summary>
/// A variable as the data tip and the variable windows show it (D44): its kind, whether it's non-public and whose code
/// declares it, whether it can be set, its declared type, and a group's members.
/// </summary>
public sealed record DebugMember(Variable Variable, MemberKind Kind)
{
    /// <summary>Private, protected or internal.</summary>
    public bool IsNonPublic { get; init; }

    /// <summary>Declared by the solution's own code (inline with a lock badge when non-public); false for a library's.</summary>
    public bool IsUserCode { get; init; }

    /// <summary>The access the metadata gives (<c>private</c>, <c>protected internal</c>…), or null.</summary>
    public string? Access { get; init; }

    /// <summary>The type that declares it, or null.</summary>
    public string? DeclaringType { get; init; }

    /// <summary>False for a property without a setter, a read-only field and a constant; null when nothing says.</summary>
    public bool? CanWrite { get; init; }

    /// <summary>Its declared type (a getter that threw shows an exception of another type), or null.</summary>
    public string? DeclaredType { get; init; }

    /// <summary>netcoredbg's "name (Owner)" for a member a derived type hides: it has no evaluate name, so no pin.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>A group's members (Non-Public members, a collection view's Raw View), a dictionary entry's Key and Value.</summary>
    public IReadOnlyList<DebugMember>? Children { get; init; }

    /// <summary>
    /// The adapter's reference the variable is in, for setVariable: null for the object the rows are read from;
    /// <see cref="ByExpression"/> for a row the shell made (a queue's element, a dictionary's entry), set by its evaluate name.
    /// </summary>
    public int? Container { get; init; }

    /// <summary>A <see cref="Container"/>: no container of the adapter's names it; it's set by its evaluate name.</summary>
    public const int ByExpression = -1;

    /// <summary>
    /// A getter that threw: netcoredbg shows the exception as the value. Its type is an exception and the member's
    /// declared type isn't (a property declared as an exception holds one).
    /// </summary>
    public bool Threw => Kind == MemberKind.Property && VariableGrouping.IsException(Variable.Type) && DeclaredType is { } declared && !VariableGrouping.IsException(declared);

    /// <summary>netcoredbg's bare <c>&lt;error&gt;</c>: the getter ran past 5 s, or couldn't be called.</summary>
    public bool CouldNotEvaluate => Variable.Value == VariableGrouping.Error;
}

/// <summary>
/// The rows of an object's members (D44, decision 3): netcoredbg lists every instance member flat, so the companion's
/// member list says each one's kind, access and declaring module. Public members and the non-public members of the
/// solution's own code are inline, in name order; a library type's non-public members go under Non-Public members, and
/// netcoredbg's Static members come last. Without the list (a remote program, no companion) nothing is grouped and the
/// kinds are guessed from the names (decision 15).
/// </summary>
/// <remarks>TODO(issue): reading a remote program's metadata (icons, grouping and [DebuggerDisplay] in SSH, container and WSL sessions) is a later step of the remote server.</remarks>
public static class VariableGrouping
{
    /// <summary>netcoredbg's node for the static members (its type and value are empty).</summary>
    public const string StaticMembers = "Static members";

    /// <summary>netcoredbg's value for a member it couldn't evaluate.</summary>
    public const string Error = "<error>";

    /// <param name="variables">netcoredbg's members of the object, in its order.</param>
    /// <param name="members">The companion's member list for the object's type (most derived first), or null.</param>
    /// <param name="isUserModule">Whether a module (a path) is one of the solution's projects' outputs.</param>
    public static IReadOnlyList<DebugMember> Group(IReadOnlyList<Variable> variables, IReadOnlyList<TypeMember>? members, Func<string?, bool> isUserModule)
    {
        if (members is null)
        {
            return [.. variables.Select(v => new DebugMember(v, IsStaticNode(v) ? MemberKind.StaticGroup : Guess(v.Name)))];
        }

        var inline = new List<DebugMember>();
        var hidden = new List<DebugMember>();
        DebugMember? statics = null;
        foreach (var variable in variables)
        {
            if (IsStaticNode(variable))
            {
                statics = new DebugMember(variable, MemberKind.StaticGroup);
                continue;
            }

            var row = Describe(variable, members, isUserModule);
            if (row is null)
            {
                continue; // an indexer: netcoredbg's call without its arguments threw
            }

            if (row.IsNonPublic && !row.IsUserCode)
            {
                hidden.Add(row);
            }
            else
            {
                inline.Add(row);
            }
        }

        var result = new List<DebugMember>(inline.OrderBy(r => r.Variable.Name, StringComparer.OrdinalIgnoreCase));
        if (hidden.Count > 0)
        {
            result.Add(new DebugMember(new Variable("Non-Public members", string.Empty), MemberKind.NonPublicGroup)
            {
                Children = [.. hidden.OrderBy(r => r.Variable.Name, StringComparer.OrdinalIgnoreCase)],
            });
        }

        if (statics is not null)
        {
            result.Add(statics);
        }

        return result;
    }

    /// <summary>The static members' rows: what the list says of each, none of them grouped (they're all the type's own).</summary>
    public static IReadOnlyList<DebugMember> Statics(IReadOnlyList<Variable> variables, IReadOnlyList<TypeMember>? members, Func<string?, bool> isUserModule) =>
        members is null
            ? [.. variables.Select(v => new DebugMember(v, Guess(v.Name)))]
            : [.. variables.Select(v => Describe(v, members, isUserModule)).OfType<DebugMember>().OrderBy(r => r.Variable.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>One member's row, by the list's entry for its name (a hidden member's "name (Owner)" by its owner's); null for an indexer.</summary>
    private static DebugMember? Describe(Variable variable, IReadOnlyList<TypeMember> members, Func<string?, bool> isUserModule)
    {
        var name = variable.Name;
        string? owner = null;
        var duplicate = false;
        if (name.EndsWith(')') && name.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0)
        {
            owner = name[(open + 2)..^1];
            name = name[..open];
            duplicate = true;
        }

        var member = members.FirstOrDefault(m => m.Name == name && (owner is null || ShortName(m.DeclaringType) == ShortName(owner) || m.DeclaringType == owner))
                     ?? members.FirstOrDefault(m => m.Name == name);
        if (member is null)
        {
            return new DebugMember(variable, Guess(variable.Name)) { IsDuplicate = duplicate };
        }

        if (member.Kind == "indexer")
        {
            return null;
        }

        var nonPublic = member.Access != "public";
        return new DebugMember(variable, member.Kind switch
        {
            "property" => MemberKind.Property,
            "constant" => MemberKind.Constant,
            "event" => MemberKind.Event,
            _ => MemberKind.Field,
        })
        {
            IsNonPublic = nonPublic,
            IsUserCode = isUserModule(member.Module),
            Access = member.Access,
            DeclaringType = member.DeclaringType,
            CanWrite = member.CanWrite,
            DeclaredType = member.Type,
            IsDuplicate = duplicate,
        };
    }

    /// <summary>netcoredbg's "Static members": that name, no type and no value.</summary>
    public static bool IsStaticNode(Variable variable) =>
        variable.Name == StaticMembers && string.IsNullOrEmpty(variable.Type) && string.IsNullOrEmpty(variable.Value);

    /// <summary>A kind from a name alone (a remote program's): <c>[0]</c> an element; <c>_name</c> and <c>name</c> fields; <c>Name</c> a property.</summary>
    public static MemberKind Guess(string name) =>
        name.StartsWith('[') ? MemberKind.Element
        : name.StartsWith('_') || name.StartsWith('<') || (name.Length > 0 && char.IsLower(name[0])) ? MemberKind.Field
        : MemberKind.Property;

    /// <summary>Whether a type name is an exception's (<c>System.InvalidOperationException</c>, <c>Exception</c>).</summary>
    public static bool IsException(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return false;
        }

        var generic = type.IndexOf('<', StringComparison.Ordinal);
        var name = generic >= 0 ? type[..generic] : type;
        return name.EndsWith("Exception", StringComparison.Ordinal);
    }

    /// <summary><c>System.Collections.Generic.List&lt;int&gt;</c> → <c>List&lt;int&gt;</c>; <c>Outer.Inner</c> → <c>Inner</c>; <c>Collection`1</c> → <c>Collection</c>.</summary>
    public static string ShortName(string type)
    {
        var generic = type.IndexOf('<', StringComparison.Ordinal);
        var head = generic >= 0 ? type[..generic] : type;
        var dot = head.LastIndexOfAny(['.', '+', '/']);
        var name = dot >= 0 ? type[(dot + 1)..] : type;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }
}
