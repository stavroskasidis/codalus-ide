using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Codalus.WinForms.DesignTools;

namespace Codalus.WinForms.Designer;

/// <summary>
/// Control libraries' own designers (#461, ADR 0106): the replacements of the classic IDE's designer-server API they're
/// built against (<c>designtools/</c> next to the host), and the design folders of the project's packages. The host's
/// resolver asks here first. <see cref="Check"/> is the host's <c>--check-vendor-designers</c> mode: what a folder of
/// design assemblies references that the replacements don't have.
/// </summary>
internal static class VendorDesigners
{
    /// <summary>The replacements' folder, and MEF's attributes, which the vendors' routing types carry.</summary>
    // The host's own folder: AppContext.BaseDirectory is the app's when the host runs on the app's deps.json.
    private static readonly string s_designTools = Path.Combine(Path.GetDirectoryName(typeof(VendorDesigners).Assembly.Location) ?? AppContext.BaseDirectory, "designtools");

    private static readonly HashSet<string> s_replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.DotNet.DesignTools.Server",
        "Microsoft.DotNet.DesignTools.Protocol",
        "Microsoft.WinForms.Utilities.Shared",
        "System.Composition.AttributedModel",
    };

    private static volatile bool s_active;

    /// <summary>
    /// The assembly <paramref name="name"/> from the replacements' folder, or (vendors' designers on) a design folder.
    /// It names no type of the replacements, so resolving the replacements themselves doesn't need them.
    /// </summary>
    public static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is { } simple && s_replacements.Contains(simple))
        {
            var path = Path.Combine(s_designTools, simple + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        return s_active ? FromDesignFolders(context, name) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? FromDesignFolders(AssemblyLoadContext context, AssemblyName name) =>
        VendorDesignerCatalog.Current?.PathOf(name) is { } path ? context.LoadFromAssemblyPath(path) : null;

    /// <summary>Whether <paramref name="type"/> is a control library's design-time code (an assembly of a design folder).</summary>
    public static bool IsVendorCode(Type type) => s_active && IsInDesignFolder(type);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsInDesignFolder(Type type) =>
        VendorDesignerCatalog.Current?.PathOf(type.Assembly.GetName()) is { } path && string.Equals(path, type.Assembly.Location, StringComparison.OrdinalIgnoreCase);

    /// <summary>Uses the design folders <paramref name="folders"/> (none, or vendors' designers off: the #454 fallback alone).</summary>
    public static void Use(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0 && !s_active)
        {
            return;
        }

        Activate(folders);
        s_active = folders.Count > 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Activate(IReadOnlyList<string> folders) => VendorDesignerCatalog.Activate(folders);

    // ------------------------------------------------------------------ --check-vendor-designers

    /// <summary>
    /// <c>--check-vendor-designers [--search folder]… folder…</c>: loads every design assembly of the design folders
    /// (each a package's <c>lib/&lt;tfm&gt;/Design/WinForms/Server</c>; <c>--search</c> folders have the control
    /// library's own assemblies, an app's build output) and writes what they reference of the designer server that the
    /// replacements don't have: types, members, and overrides that override nothing. Exit code 0 when nothing is missing.
    /// </summary>
    public static int Check(IReadOnlyList<string> args)
    {
        var folders = new List<string>();
        var search = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--search" && i + 1 < args.Count)
            {
                search.Add(Path.GetFullPath(args[++i]));
            }
            else
            {
                folders.Add(Path.GetFullPath(args[i]));
            }
        }

        AssemblyLoadContext.Default.Resolving += (context, name) => Resolve(context, name) ?? FromFolders(context, name, search);
        Use(folders);
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        var checkedAssemblies = 0;
        foreach (var file in folders.SelectMany(f => new[] { f, Path.Combine(f, "lib") }).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (CheckAssembly(file, gaps))
            {
                checkedAssemblies++;
            }
        }

        foreach (var gap in gaps)
        {
            Console.WriteLine(gap);
        }

        Console.WriteLine($"{checkedAssemblies} design assemblies checked: {gaps.Count} gap(s).");
        return gaps.Count == 0 ? 0 : 1;
    }

    private static Assembly? FromFolders(AssemblyLoadContext context, AssemblyName name, List<string> folders)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(path))
            {
                return context.LoadFromAssemblyPath(path);
            }
        }

        return null;
    }

    /// <summary>Checks one assembly that references the designer server; false when it doesn't (not a design assembly).</summary>
    private static bool CheckAssembly(string file, SortedSet<string> gaps)
    {
        var fileName = Path.GetFileName(file);
        List<(int Token, bool IsType, string What)> references;
        try
        {
            using var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly)
            {
                return false;
            }

            references = ServerReferences(pe.GetMetadataReader());
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }

        if (references.Count == 0)
        {
            return false;
        }

        Assembly assembly;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(file));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            gaps.Add($"{fileName}: doesn't load: {ex.Message}");
            return true;
        }

        var module = assembly.ManifestModule;
        foreach (var (token, isType, what) in references)
        {
            try
            {
                if (isType)
                {
                    module.ResolveType(token);
                }
                else
                {
                    module.ResolveMember(token);
                }
            }
            catch (ArgumentException)
            {
                // A member of an open generic instantiation: resolved only in a generic context (checked through its uses).
            }
            catch (Exception ex) when (ex is TypeLoadException or MissingMemberException or FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                gaps.Add($"missing: {what} ({ex.GetType().Name}: {ex.Message}) [{fileName}]");
            }
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loader in ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct(StringComparer.Ordinal))
            {
                gaps.Add($"type load: {loader} [{fileName}]");
            }

            types = [.. ex.Types.OfType<Type>()];
        }

        var server = typeof(Microsoft.DotNet.DesignTools.Designers.ComponentDesigner).Assembly;
        foreach (var type in types)
        {
            if (!DerivesFrom(type, server))
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                // An override (virtual, not a new slot) whose base is itself overrides nothing: the replacement lacks that virtual.
                if (method.IsVirtual && (method.Attributes & MethodAttributes.NewSlot) == 0 && method.GetBaseDefinition() == method)
                {
                    gaps.Add($"overrides nothing: {method.ReturnType.Name} {OwnerOf(type, server)}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) [{fileName}]");
                }
            }
        }

        return true;
    }

    private static bool DerivesFrom(Type type, Assembly server)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Assembly == server || current.Assembly.GetName().Name is "Microsoft.DotNet.DesignTools.Protocol" or "Microsoft.WinForms.Utilities.Shared")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The replacement's type <paramref name="type"/> derives from, by name, for the report.</summary>
    private static string OwnerOf(Type type, Assembly server)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Assembly == server || current.Assembly.GetName().Name is "Microsoft.DotNet.DesignTools.Protocol" or "Microsoft.WinForms.Utilities.Shared")
            {
                return (current.IsGenericType ? current.GetGenericTypeDefinition() : current).FullName ?? current.Name;
            }
        }

        return type.FullName ?? type.Name;
    }

    /// <summary>The tokens of the type and member references into the designer server's assemblies, with what they name.</summary>
    private static List<(int Token, bool IsType, string What)> ServerReferences(MetadataReader reader)
    {
        var result = new List<(int, bool, string)>();
        foreach (var handle in reader.TypeReferences)
        {
            if (IsServer(reader, handle))
            {
                result.Add((MetadataTokens.GetToken(handle), true, "type " + Name(reader, handle)));
            }
        }

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            var parent = member.Parent;
            var owner = parent.Kind switch
            {
                HandleKind.TypeReference when IsServer(reader, (TypeReferenceHandle)parent) => Name(reader, (TypeReferenceHandle)parent),
                HandleKind.TypeSpecification when GenericServerType(reader, (TypeSpecificationHandle)parent) is { } generic => generic,
                _ => null,
            };
            if (owner is not null)
            {
                result.Add((MetadataTokens.GetToken(handle), false, $"{owner}.{reader.GetString(member.Name)}"));
            }
        }

        return result;
    }

    private static string? GenericServerType(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return null;
        }

        _ = blob.ReadSignatureTypeCode();
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference && IsServer(reader, (TypeReferenceHandle)generic) ? Name(reader, (TypeReferenceHandle)generic) : null;
    }

    private static bool IsServer(MetadataReader reader, TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        return scope.Kind == HandleKind.AssemblyReference
            && reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) is "Microsoft.DotNet.DesignTools.Server" or "Microsoft.DotNet.DesignTools.Protocol" or "Microsoft.WinForms.Utilities.Shared";
    }

    private static string Name(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference ? Name(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name
            : reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + name : name;
    }
}
