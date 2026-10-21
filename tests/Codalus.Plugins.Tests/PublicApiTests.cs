using System.Reflection;
using System.Text;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The plugin API is frozen at 1.0 (M22a, ADR 0003): its public surface is <c>PublicAPI.txt</c> next to the project.
/// A member that changes or goes away fails here; a new one must be added to the file (with
/// <c>CODALUS_UPDATE_PUBLIC_API=1</c>), which is the reviewed record of a minor version's additions.
/// </summary>
public sealed class PublicApiTests
{
    private static string RepoRoot => typeof(PublicApiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    private static string BaselineFile => Path.Combine(RepoRoot, "src", "Codalus.Plugins.Abstractions", "PublicAPI.txt");

    private static Assembly Api => typeof(ICodalusPlugin).Assembly;

    /// <summary>
    /// Each public type, then its public members: the API's surface as plugins see it, with each parameter's
    /// <c>out</c>/<c>in</c>/<c>ref</c>, nullable reference types (<c>?</c>) and generic constraints, so changing any of
    /// them fails here too.
    /// </summary>
    internal static IReadOnlyList<string> Surface()
    {
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();
        foreach (var type in Api.GetExportedTypes().OrderBy(t => Name(t), StringComparer.Ordinal))
        {
            var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : "class";
            lines.Add($"{kind} {Name(type)}{Constraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])}");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var member in type.GetMembers(flags).Where(m => !m.Name.StartsWith('<')))
            {
                var line = member switch
                {
                    ConstructorInfo c => $".ctor({Parameters(c, nullability)})",
                    MethodInfo m when !m.IsSpecialName => $"{m.Name}{Generic(m)}({Parameters(m, nullability)}) -> {Name(m.ReturnType, nullability.Create(m.ReturnParameter))}{Constraints(m.IsGenericMethodDefinition ? m.GetGenericArguments() : [])}",
                    PropertyInfo p => $"{p.Name} {{{(p.GetMethod?.IsPublic == true ? " get;" : string.Empty)}{(p.SetMethod?.IsPublic == true ? (IsInit(p) ? " init;" : " set;") : string.Empty)} }} : {Name(p.PropertyType, nullability.Create(p))}",
                    FieldInfo f when type.IsEnum && f.IsStatic => $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}",
                    FieldInfo f when !type.IsEnum => $"{f.Name} : {Name(f.FieldType, nullability.Create(f))}{(f.IsLiteral ? " = " + f.GetRawConstantValue() : string.Empty)}",
                    EventInfo e => $"{e.Name} event {Name(e.EventHandlerType!, nullability.Create(e))}",
                    _ => null,
                };
                if (line is not null)
                {
                    members.Add("  " + line);
                }
            }

            lines.AddRange(members);
        }

        return lines;
    }

    private static bool IsInit(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Generic(MethodInfo method) =>
        method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    private static string Parameters(MethodBase method, NullabilityInfoContext nullability) =>
        string.Join(", ", method.GetParameters().Select(p =>
            (p.GetCustomAttribute<ParamArrayAttribute>() is null ? string.Empty : "params ")
            + (!p.ParameterType.IsByRef ? string.Empty : p.IsOut ? "out " : p.IsIn ? "in " : "ref ")
            + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType, nullability.Create(p))
            + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : string.Empty)));

    /// <summary>The constraints of generic parameters, as C# writes them: <c> where T : class, new()</c>.</summary>
    private static string Constraints(Type[] parameters)
    {
        var text = new StringBuilder();
        foreach (var parameter in parameters)
        {
            var attributes = parameter.GenericParameterAttributes;
            var parts = new List<string>();
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("struct");
            }
            else if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add("class");
            }

            parts.AddRange(parameter.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(c => Name(c)));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                text.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", parts));
            }
        }

        return text.ToString();
    }

    /// <param name="nullability">The reference type's annotation: <c>?</c> where it may be null.</param>
    private static string Name(Type type, NullabilityInfo? nullability = null)
    {
        var mark = nullability is { ReadState: NullabilityState.Nullable } && !type.IsValueType ? "?" : string.Empty;
        if (type.IsGenericParameter)
        {
            return type.Name + mark;
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!, nullability?.ElementType) + "[]" + mark;
        }

        var name = type.IsNested ? Name(type.DeclaringType!) + "." + type.Name : (type.Namespace is { } ns ? ns + "." : string.Empty) + type.Name;
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            name = name[..name.IndexOf('`', StringComparison.Ordinal)] + "<"
                + string.Join(", ", arguments.Select((a, i) => Name(a, nullability?.GenericTypeArguments is { } inner && i < inner.Length ? inner[i] : null))) + ">";
        }

        return name + mark;
    }

    [Fact]
    public void The_public_api_matches_its_baseline()
    {
        var surface = Surface();
        if (Environment.GetEnvironmentVariable("CODALUS_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(BaselineFile, string.Join('\n', surface) + "\n", new UTF8Encoding(false));
        }

        var baseline = File.ReadAllLines(BaselineFile).Where(l => l.Length > 0).ToList();
        var removed = baseline.Except(surface, StringComparer.Ordinal).ToList();
        var added = surface.Except(baseline, StringComparer.Ordinal).ToList();
        Assert.True(removed.Count == 0, "Members changed or removed from the frozen plugin API (a major version's change, ADR 0003):\n" + string.Join('\n', removed));
        Assert.True(added.Count == 0, "Members added to the plugin API: record them in PublicAPI.txt (CODALUS_UPDATE_PUBLIC_API=1) and in the changelog of docs/plugins.md:\n" + string.Join('\n', added));
    }

    [Fact]
    public void Deprecations_use_the_api_diagnostic()
    {
        var obsolete = Api.GetExportedTypes()
            .SelectMany(t => new MemberInfo[] { t }.Concat(t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .Select(m => (Member: m, Attribute: m.GetCustomAttribute<ObsoleteAttribute>()))
            .Where(m => m.Attribute is not null)
            .ToList();
        Assert.All(obsolete, m => Assert.True(m.Attribute!.DiagnosticId == PluginApi.DeprecationDiagnostic, $"{m.Member.DeclaringType?.Name}.{m.Member.Name} is deprecated without {PluginApi.DeprecationDiagnostic}."));
        Assert.Equal("1.1.0", PluginApi.Version);
    }
}
