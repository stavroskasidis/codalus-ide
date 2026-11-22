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

    private static HostWindow? s_window;

    /// <summary>A control library's dialogs run their modal loop on <paramref name="window"/> (#461, <c>VendorModal</c>).</summary>
    public static void Attach(HostWindow window)
    {
        if (s_active && Interlocked.Exchange(ref s_window, window) is null)
        {
            Subscribe(window);
        }
    }

    /// <summary>
    /// After an action, the request a control library's designer waits for from the classic IDE's process before it
    /// shows its dialog (#461, <c>VendorModal</c>), sent as that process would; the dialog runs in it, on this thread.
    /// </summary>
    public static void Continue(System.ComponentModel.Design.IDesignerHost host)
    {
        if (s_active)
        {
            ContinueCore(host);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ContinueCore(System.ComponentModel.Design.IDesignerHost host)
    {
        if (VendorModal.TakeContinuation() is { } endpoint && VendorDesignerCatalog.Current is { } catalog)
        {
            Console.Error.WriteLine($"[designer] Asking the control library's designer to continue ({endpoint}), as the classic IDE does.");
            _ = catalog.Send(endpoint, host);
        }
    }

    private static readonly MethodInfo s_getHitTest = typeof(System.Windows.Forms.Design.ControlDesigner).GetMethod("GetHitTest", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Point)])
        ?? throw new MissingMethodException(nameof(System.Windows.Forms.Design.ControlDesigner), "GetHitTest");

    /// <summary>
    /// The designer may take clicks itself (#461): a control library's designer, behind its adapter, with glyphs of its
    /// own or a hit test (DevExpress's grid, for its level designer's buttons). Windows Forms' own designers' glyphs and
    /// hit tests (a tab control's tabs) stay the surface's.
    /// </summary>
    public static bool TakesClicks(System.ComponentModel.Design.IDesigner? designer) => s_active && designer is not null && TakesClicksCore(designer);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TakesClicksCore(System.ComponentModel.Design.IDesigner designer) => VendorGlyphs.MayTakeClicks(designer);

    /// <summary>A click for the library designers' glyphs, the front ones first (<c>VendorGlyphs.Click</c>); false when none took it.</summary>
    public static bool ClickGlyphs(System.ComponentModel.Design.IDesignerHost host, IEnumerable<System.ComponentModel.Design.IDesigner> designers, Point screen) =>
        s_active && ClickGlyphsCore(host, designers, screen);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ClickGlyphsCore(System.ComponentModel.Design.IDesignerHost host, IEnumerable<System.ComponentModel.Design.IDesigner> designers, Point screen) =>
        VendorGlyphs.Click(host, designers, screen);

    /// <summary>The designer's <c>GetHitTest</c> at a point of the screen (its adapter asks the library's designer); false when it throws.</summary>
    public static bool HitTest(System.Windows.Forms.Design.ControlDesigner designer, Point screen)
    {
        try
        {
            return s_getHitTest.Invoke(designer, [screen]) is true;
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine($"[designer] {designer.GetType().Name}.GetHitTest failed: {ex.InnerException?.Message}");
            return false;
        }
    }

    private const int WmNcHitTest = 0x0084;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;

    /// <summary>
    /// A left click at a point of the screen, as the classic IDE's surface lets it through to a control: the hit test
    /// first (Windows Forms' <c>ControlDesigner</c> asks its <c>GetHitTest</c> then, and passes the mouse messages that
    /// follow to the control when it says yes), then a move, a press and a release, to the control's window, or to the
    /// window of a part of it (a child no designer sited) under the point.
    /// </summary>
    public static void SendClick(Control control, Point screen)
    {
        var target = control;
        while (target.GetChildAtPoint(target.PointToClient(screen), GetChildAtPointSkip.Invisible) is { Site: null } part)
        {
            target = part;
        }

        Console.Error.WriteLine($"[designer] A click at {screen.X},{screen.Y} goes to {control.Site?.Name ?? control.Name} ({target.GetType().Name}): its designer takes it.");
        var handle = target.Handle;
        var client = target.PointToClient(screen);
        _ = NativeMethods.SendMessage(handle, WmNcHitTest, IntPtr.Zero, Coordinates(screen));
        _ = NativeMethods.SendMessage(handle, WmMouseMove, IntPtr.Zero, Coordinates(client));
        _ = NativeMethods.SendMessage(handle, WmNcHitTest, IntPtr.Zero, Coordinates(screen));
        _ = NativeMethods.SendMessage(handle, WmLButtonDown, new IntPtr(MkLButton), Coordinates(client));
        _ = NativeMethods.SendMessage(handle, WmNcHitTest, IntPtr.Zero, Coordinates(screen));
        _ = NativeMethods.SendMessage(handle, WmLButtonUp, IntPtr.Zero, Coordinates(client));
    }

    private static readonly MethodInfo s_onMouseMove = typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(MouseEventArgs)])
        ?? throw new MissingMethodException(nameof(Control), "OnMouseMove");

    private static readonly MethodInfo s_onMouseLeave = typeof(Control).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(EventArgs)])
        ?? throw new MissingMethodException(nameof(Control), "OnMouseLeave");

    /// <summary>
    /// The pointer leaves the control under the click and its parts (the controls of <paramref name="path"/>, root first,
    /// from the deepest up to the first a designer sited): a move off them, then the leave, raised on the controls as
    /// DevExpress's own behaviors raise them (the surface's designer takes a mouse message to a part's window). Else a
    /// part keeps its hover in the next frame (DevExpress's level designer's link stays underlined).
    /// </summary>
    public static void Leave(IReadOnlyList<Control> path)
    {
        for (var i = path.Count - 1; i > 0; i--)
        {
            var control = path[i];
            try
            {
                _ = s_onMouseMove.Invoke(control, [new MouseEventArgs(MouseButtons.None, 0, -1, -1, 0)]);
                _ = s_onMouseLeave.Invoke(control, [EventArgs.Empty]);
            }
            catch (TargetInvocationException ex)
            {
                Console.Error.WriteLine($"[designer] {control.GetType().Name}'s mouse leave failed: {ex.InnerException?.Message}");
            }

            if (control.Site is not null)
            {
                return;
            }
        }
    }

    /// <summary>A point as a mouse message's <c>lParam</c>: x in the low word, y in the high one, each signed.</summary>
    private static IntPtr Coordinates(Point point) => new((point.Y << 16) | (point.X & 0xFFFF));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Subscribe(HostWindow window)
    {
        VendorModal.Started += window.BeginVendorModal;
        VendorModal.Stopped += window.EndVendorModal;
    }

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
