using System.Collections.Concurrent;
using System.Xml;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// The Toolbox's items from a project's referenced assemblies (#454): a control library delivered as NuGet packages
/// (a vendor's, whose template's form derives from the library's) or an assembly referenced by its path. Each
/// assembly's public components are listed as the projects' are (<see cref="WinFormsFeature.ToolboxItem"/>), grouped by
/// the package the assembly comes from (its id, as the package's <c>.nuspec</c> spells it), else by the assembly's name.
/// The framework's own assemblies (a reference pack, the .NET Framework's reference assemblies) are left out: the
/// Toolbox has their controls in the classic IDE's categories. An assembly's items are kept while its file is the
/// same: a vendor's library has thousands of types, read again only when a restore replaces it.
/// </summary>
internal static class ToolboxReferences
{
    /// <summary>The framework's public key tokens (ECMA's, Microsoft's for the .NET Framework, netstandard's).</summary>
    private static readonly string[] s_frameworkTokens = ["b77a5c561934e089", "b03f5f7f11d50a3a", "cc7b13ffcd2ddd51"];

    /// <summary>The assemblies a component derives from (Component's: .NET's, the .NET Framework's, .NET Standard's): an assembly that references none has none.</summary>
    private static readonly HashSet<string> s_componentAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.ComponentModel.Primitives", "System.Windows.Forms", "System", "netstandard", "System.ComponentModel.TypeConverter",
    };

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Time, WinFormsToolboxItem[] Items)> s_items = new(Paths.Comparer);
    private static readonly ConcurrentDictionary<string, bool> s_frameworkFolders = new(Paths.Comparer);
    private static readonly ConcurrentDictionary<string, string?> s_packages = new(Paths.Comparer);

    /// <summary>The referenced assemblies' items, by their group and then their name.</summary>
    public static IEnumerable<WinFormsToolboxItem> Items(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var items = new List<WinFormsToolboxItem>();
        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.FilePath is not { } path || IsFramework(path) || compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!file.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var cached = s_items.TryGetValue(path, out var entry) && entry.Length == file.Length && entry.Time == file.LastWriteTimeUtc
                ? entry.Items
                : (s_items[path] = (file.Length, file.LastWriteTimeUtc, Read(assembly, PackageOf(path) ?? assembly.Identity.Name))).Items;
            items.AddRange(cached);
        }

        return items.DistinctBy(i => i.Type).OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static WinFormsToolboxItem[] Read(IAssemblySymbol assembly, string group)
    {
        if (IsFramework(assembly.Identity) || !assembly.Modules.SelectMany(m => m.ReferencedAssemblies).Any(r => s_componentAssemblies.Contains(r.Name)))
        {
            return [];
        }

        var items = new List<WinFormsToolboxItem>();
        foreach (var type in Types(assembly.GlobalNamespace))
        {
            if (WinFormsFeature.ToolboxItem(type, group, internalToo: false) is { } item)
            {
                items.Add(item);
            }
        }

        return [.. items];
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var type in Types(child))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type)
            {
                foreach (var nested in Nested(type))
                {
                    yield return nested;
                }
            }
        }

        static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var member in type.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public).SelectMany(Nested))
            {
                yield return member;
            }
        }
    }

    private static bool IsFramework(AssemblyIdentity identity) =>
        identity.PublicKeyToken is { Length: 8 } token && s_frameworkTokens.Contains(Convert.ToHexString(token.AsSpan()), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is one of the framework's reference assemblies: a reference pack
    /// (<c>packs/Microsoft.WindowsDesktop.App.Ref/11.0.0/ref/net11.0/</c>, or the same pack restored as a package) has
    /// <c>data/FrameworkList.xml</c>, and the .NET Framework's (<c>Reference Assemblies/…/v4.8/</c> and its
    /// <c>Facades/</c>, or the reference assemblies' package) <c>RedistList/FrameworkList.xml</c>.
    /// </summary>
    internal static bool IsFramework(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is not null && s_frameworkFolders.GetOrAdd(folder, static f =>
        {
            var current = f;
            for (var level = 0; level < 4 && current is not null; level++, current = Path.GetDirectoryName(current))
            {
                if (File.Exists(Path.Combine(current, "data", "FrameworkList.xml")) || File.Exists(Path.Combine(current, "RedistList", "FrameworkList.xml")))
                {
                    return true;
                }
            }

            return false;
        });
    }

    /// <summary>
    /// The id of the package <paramref name="path"/> comes from, as its <c>.nuspec</c> spells it: a package in the global
    /// packages folder (<c>&lt;id&gt;/&lt;version&gt;/&lt;id&gt;.nuspec</c>, the assembly under <c>lib/&lt;tfm&gt;/</c> or
    /// <c>ref/&lt;tfm&gt;/</c>); null for an assembly outside a package, or a package folder without its <c>.nuspec</c> (a
    /// <c>packages.config</c> solution's), whose items go by the assembly's name.
    /// </summary>
    internal static string? PackageOf(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is null ? null : s_packages.GetOrAdd(folder, static f =>
        {
            var current = Path.GetDirectoryName(f);
            for (var level = 0; level < 3 && current is not null; level++, current = Path.GetDirectoryName(current))
            {
                string[] nuspecs;
                try
                {
                    nuspecs = Directory.GetFiles(current, "*.nuspec");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }

                if (nuspecs is [var nuspec] && Id(nuspec) is { } id)
                {
                    return id;
                }
            }

            return null;
        });
    }

    private static string? Id(string nuspec)
    {
        try
        {
            using var reader = XmlReader.Create(nuspec, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "id" })
                {
                    return reader.ReadElementContentAsString().Trim() is { Length: > 0 } id ? id : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
        }

        return null;
    }
}
