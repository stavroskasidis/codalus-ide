using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Microsoft.DotNet.DesignTools.Designers;
using Microsoft.DotNet.DesignTools.TypeRouting;
using ComponentDesigner = Microsoft.DotNet.DesignTools.Designers.ComponentDesigner;

namespace Codalus.WinForms.DesignTools;

/// <summary>
/// The control libraries' designers a designer host can use (#461, ADR 0106): the design folders of the project's
/// packages (<c>lib/&lt;tfm&gt;/Design/WinForms/Server</c>, with their design assemblies in <c>lib/</c> below), the routes
/// their routing assemblies declare, and a type description provider on every component that gives a component whose
/// designer is a vendor's an adapter instead (<see cref="ComponentAdapter"/>, <see cref="ControlAdapter"/>,
/// <see cref="ParentAdapter"/>). A vendor's root designer, and one no adapter fits, gives way to the base type's designer
/// of the same kind, as a designer that isn't there does (#454).
/// </summary>
internal sealed class VendorDesignerCatalog
{
    private static readonly object s_gate = new();
    private static bool s_providerAdded;
    private static VendorDesignerCatalog? s_current;

    private readonly string[] _folders;
    private readonly Dictionary<string, string> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Type?> _designers = new(StringComparer.Ordinal);
    private Dictionary<string, Type>? _routes;
    private Dictionary<string, (string File, string Type)>? _handlers;

    private VendorDesignerCatalog(string[] folders)
    {
        _folders = folders;
        foreach (var folder in folders)
        {
            foreach (var directory in new[] { folder, Path.Combine(folder, "lib") })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                {
                    _assemblies.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                }
            }
        }
    }

    /// <summary>The catalog in use, or null when vendors' designers are off or the project has none.</summary>
    public static VendorDesignerCatalog? Current => Volatile.Read(ref s_current);

    /// <summary>Uses the design folders <paramref name="folders"/> from now on (none: vendors' designers off).</summary>
    public static void Activate(IReadOnlyList<string> folders)
    {
        lock (s_gate)
        {
            var existing = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (s_current is { } current && current._folders.SequenceEqual(existing, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            VendorDesignerRegistry.Clear();
            Volatile.Write(ref s_current, existing.Length == 0 ? null : new VendorDesignerCatalog(existing));
            if (s_current is not null && !s_providerAdded)
            {
                TypeDescriptor.AddProvider(new Provider(TypeDescriptor.GetProvider(typeof(Component))), typeof(Component));
                s_providerAdded = true;
            }

            TypeDescriptor.Refresh(typeof(Component));
            if (s_current is not null)
            {
                VendorLog.Write($"Control libraries' designers: {existing.Length} design folder(s), {s_current._assemblies.Count} assemblies.");
            }
        }
    }

    /// <summary>The path of the design assembly <paramref name="name"/> in the design folders, for the host's resolver.</summary>
    public string? PathOf(AssemblyName name) => name.Name is { } simple && _assemblies.TryGetValue(simple, out var path) ? path : null;

    /// <summary>The vendor's designer type the designer name <paramref name="designerTypeName"/> stands for, or null (not a vendor's, or it doesn't load).</summary>
    public Type? VendorDesigner(string designerTypeName) => _designers.GetOrAdd(designerTypeName, name =>
    {
        try
        {
            return Resolve(name) is { } type && typeof(ComponentDesigner).IsAssignableFrom(type) ? type : null;
        }
        catch (Exception ex) when (VendorLog.Survivable(ex))
        {
            VendorLog.Write($"The designer {name} didn't load: {VendorLog.Describe(ex)}");
            return null;
        }
    });

    /// <summary>The adapter that gives <paramref name="vendorDesigner"/> to the surface, or null when none fits (a root designer).</summary>
    public static Type? AdapterFor(Type vendorDesigner) =>
        typeof(DocumentDesigner).IsAssignableFrom(vendorDesigner) ? null
        : typeof(ParentControlDesigner).IsAssignableFrom(vendorDesigner) ? typeof(ParentAdapter)
        : typeof(ControlDesigner).IsAssignableFrom(vendorDesigner) ? typeof(ControlAdapter)
        : typeof(ComponentDesigner).IsAssignableFrom(vendorDesigner) ? typeof(ComponentAdapter)
        : null;

    private Type? Resolve(string qualifiedName)
    {
        var comma = qualifiedName.IndexOf(',', StringComparison.Ordinal);
        var typeName = (comma < 0 ? qualifiedName : qualifiedName[..comma]).Trim();
        if (Routes().TryGetValue(typeName, out var routed))
        {
            return routed;
        }

        if (comma < 0)
        {
            return null;
        }

        var assemblyName = new AssemblyName(qualifiedName[(comma + 1)..].Trim());
        return PathOf(assemblyName) is null ? null : Load(assemblyName)?.GetType(typeName, throwOnError: false);
    }

    private static Assembly? Load(AssemblyName name)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name.Name!));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            VendorLog.Write($"{name.Name} didn't load: {ex.Message}");
            return null;
        }
    }

    /// <summary>The designer routes the routing assemblies declare (each folder's own assemblies with an <c>[ExportTypeRoutingDefinitionProvider]</c> type), read once.</summary>
    private Dictionary<string, Type> Routes()
    {
        if (Volatile.Read(ref _routes) is { } routes)
        {
            return routes;
        }

        lock (_designers)
        {
            if (_routes is not null)
            {
                return _routes;
            }

            var found = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var file in _folders.SelectMany(f => Directory.EnumerateFiles(f, "*.dll")))
            {
                foreach (var providerName in RoutingProviders(file))
                {
                    try
                    {
                        if (Load(AssemblyName.GetAssemblyName(file))?.GetType(providerName, throwOnError: false) is { } providerType
                            && Activator.CreateInstance(providerType) is ITypeRoutingDefinitionProvider provider)
                        {
                            foreach (var definition in provider.GetDefinitions())
                            {
                                if (definition.Kind == TypeRoutingKind.Designer)
                                {
                                    found[definition.Name] = definition.Type;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (VendorLog.Survivable(ex))
                    {
                        VendorLog.Write($"The designer routes of {providerName} ({Path.GetFileName(file)}) didn't load: {VendorLog.Describe(ex)}");
                    }
                }
            }

            VendorLog.Write($"Control libraries' designers: {found.Count} routed designer name(s).");
            Volatile.Write(ref _routes, found);
            return found;
        }
    }

    /// <summary>
    /// Sends the designer protocol's request <paramref name="endpoint"/> to the vendor's handler for it, as the classic
    /// IDE's process would (#461: DevExpress's dialogs show when its client asks the server to continue), with a request
    /// of no data, the handler's services those of <paramref name="host"/>; false when no handler declares it.
    /// </summary>
    public bool Send(string endpoint, IDesignerHost host)
    {
        lock (_designers)
        {
            _handlers ??= _folders.SelectMany(f => Directory.EnumerateFiles(f, "*.dll")).SelectMany(file => RequestHandlers(file).Select(h => (h.Endpoint, (file, h.Type))))
                .GroupBy(h => h.Endpoint, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
        }

        if (!_handlers.TryGetValue(endpoint, out var found))
        {
            VendorLog.Write($"No control library's designer handles {endpoint}.");
            return false;
        }

        try
        {
            var type = Load(AssemblyName.GetAssemblyName(found.File))?.GetType(found.Type, throwOnError: true)!;
            var handlerBase = type;
            while (handlerBase is not null && !(handlerBase.IsGenericType && handlerBase.GetGenericTypeDefinition() == typeof(Microsoft.DotNet.DesignTools.Protocol.Endpoints.RequestHandler<,>)))
            {
                handlerBase = handlerBase.BaseType;
            }

            if (handlerBase is null)
            {
                VendorLog.Write($"{found.Type} handles {endpoint} but isn't a request handler.");
                return false;
            }

            var request = Activator.CreateInstance(handlerBase.GetGenericArguments()[0], nonPublic: true);
            var handler = Activator.CreateInstance(type, nonPublic: true)!;
            handlerBase.GetProperty("ServiceProvider")!.SetValue(handler, VendorServices.For(host).Host);
            handlerBase.GetMethod("HandleRequest")!.Invoke(handler, [request]);
            return true;
        }
        catch (Exception ex) when (VendorLog.Survivable(ex))
        {
            VendorLog.Write($"{found.Type} failed on {endpoint}: {VendorLog.Describe(ex)}");
            return false;
        }
    }

    /// <summary>The endpoints <paramref name="file"/>'s <c>[ExportRequestHandler(name)]</c> types handle, read from its metadata.</summary>
    private static List<(string Endpoint, string Type)> RequestHandlers(string file)
    {
        var handlers = new List<(string, string)>();
        try
        {
            using var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata)
            {
                return handlers;
            }

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    if (attribute.Constructor.Kind == HandleKind.MemberReference
                        && reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                        && reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name) == nameof(Microsoft.DotNet.DesignTools.Protocol.Endpoints.ExportRequestHandlerAttribute))
                    {
                        var blob = reader.GetBlobReader(attribute.Value);
                        if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() is { Length: > 0 } endpoint)
                        {
                            var ns = reader.GetString(type.Namespace);
                            handlers.Add((endpoint, ns.Length == 0 ? reader.GetString(type.Name) : ns + "." + reader.GetString(type.Name)));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // Not an assembly, or held: no handlers from it.
        }

        return handlers;
    }

    /// <summary>The types of <paramref name="file"/> marked <c>[ExportTypeRoutingDefinitionProvider]</c>, read from its metadata without loading it.</summary>
    private static List<string> RoutingProviders(string file)
    {
        var providers = new List<string>();
        try
        {
            using var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata)
            {
                return providers;
            }

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var constructor = reader.GetCustomAttribute(attributeHandle).Constructor;
                    if (constructor.Kind == HandleKind.MemberReference
                        && reader.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent
                        && reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name) == nameof(ExportTypeRoutingDefinitionProviderAttribute))
                    {
                        var ns = reader.GetString(type.Namespace);
                        providers.Add(ns.Length == 0 ? reader.GetString(type.Name) : ns + "." + reader.GetString(type.Name));
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // Not an assembly, or held: no routes from it.
        }

        return providers;
    }

    /// <summary>
    /// <paramref name="type"/>'s attributes, a vendor's designers replaced: an adapter for a designer of the kind
    /// <c>IDesigner</c> that one fits, else (a root designer) the base type's designer of the same kind.
    /// </summary>
    private AttributeCollection Map(Type type, AttributeCollection attributes)
    {
        Attribute[]? mapped = null;
        for (var i = 0; i < attributes.Count; i++)
        {
            if (attributes[i] is not DesignerAttribute designer || VendorDesigner(designer.DesignerTypeName) is not { } vendor)
            {
                continue;
            }

            mapped ??= [.. attributes.Cast<Attribute>()];
            if (IsDesignerKind(designer) && AdapterFor(vendor) is { } adapter)
            {
                VendorDesignerRegistry.Register(type, vendor);
                mapped[i] = new DesignerAttribute(adapter.AssemblyQualifiedName!, designer.DesignerBaseTypeName);
            }
            else
            {
                var fallback = type.BaseType is { } baseType ? TypeDescriptor.GetAttributes(baseType).OfType<DesignerAttribute>().FirstOrDefault(a => a.TypeId.Equals(designer.TypeId)) : null;
                VendorLog.Once($"{type.FullName}'s designer {vendor.FullName} isn't supported (a root designer): it's designed as {type.BaseType?.FullName}.");
                mapped[i] = fallback ?? new DesignerAttribute(typeof(ComponentDesigner));
            }
        }

        return mapped is null ? attributes : new AttributeCollection(mapped);
    }

    private static bool IsDesignerKind(DesignerAttribute attribute) =>
        attribute.DesignerBaseTypeName.StartsWith(typeof(IDesigner).FullName + ",", StringComparison.Ordinal) || attribute.DesignerBaseTypeName == typeof(IDesigner).FullName;

    private sealed class Provider(TypeDescriptionProvider parent) : TypeDescriptionProvider(parent)
    {
        public override ICustomTypeDescriptor? GetTypeDescriptor(Type objectType, object? instance)
        {
            var descriptor = base.GetTypeDescriptor(objectType, instance);
            return descriptor is not null && Current is not null ? new Descriptor(descriptor, objectType) : descriptor;
        }
    }

    private sealed class Descriptor(ICustomTypeDescriptor parent, Type type) : CustomTypeDescriptor(parent)
    {
        public override AttributeCollection GetAttributes()
        {
            var attributes = base.GetAttributes();
            return Current is { } catalog ? catalog.Map(type, attributes) : attributes;
        }
    }
}
