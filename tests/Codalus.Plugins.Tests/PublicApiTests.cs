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

    /// <summary>Each public type, then its public members: the API's surface as plugins see it.</summary>
    internal static IReadOnlyList<string> Surface()
    {
        var lines = new List<string>();
        foreach (var type in Api.GetExportedTypes().OrderBy(Name, StringComparer.Ordinal))
        {
            var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : "class";
            lines.Add($"{kind} {Name(type)}");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var member in type.GetMembers(flags).Where(m => !m.Name.StartsWith('<')))
            {
                var line = member switch
                {
                    ConstructorInfo c => $".ctor({Parameters(c)})",
                    MethodInfo m when !m.IsSpecialName => $"{m.Name}{Generic(m)}({Parameters(m)}) -> {Name(m.ReturnType)}",
                    PropertyInfo p => $"{p.Name} {{{(p.GetMethod?.IsPublic == true ? " get;" : string.Empty)}{(p.SetMethod?.IsPublic == true ? (IsInit(p) ? " init;" : " set;") : string.Empty)} }} : {Name(p.PropertyType)}",
                    FieldInfo f when type.IsEnum && f.IsStatic => $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}",
                    FieldInfo f when !type.IsEnum => $"{f.Name} : {Name(f.FieldType)}{(f.IsLiteral ? " = " + f.GetRawConstantValue() : string.Empty)}",
                    EventInfo e => $"{e.Name} event {Name(e.EventHandlerType!)}",
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

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p => (p.GetCustomAttribute<ParamArrayAttribute>() is null ? string.Empty : "params ") + Name(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : string.Empty)));

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsByRef)
        {
            return "ref " + Name(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[]";
        }

        var name = type.IsNested ? Name(type.DeclaringType!) + "." + type.Name : (type.Namespace is { } ns ? ns + "." : string.Empty) + type.Name;
        if (type.IsGenericType)
        {
            name = name[..name.IndexOf('`', StringComparison.Ordinal)] + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">";
        }

        return name;
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
