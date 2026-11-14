using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static Codalus.Previewer.Agent.PreviewAgent;

namespace Codalus.Previewer.Agent;

/// <summary>A binding that failed in the running app (Avalonia's binding log), and its element while it lives.</summary>
internal sealed class BindingFailure
{
    public string Property { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    /// <summary>The element's hash code, which Avalonia's log names it by when it has no name.</summary>
    public int Hash { get; set; }

    /// <summary>The element's name, which Avalonia's log names it by when it has one.</summary>
    public string ElementName { get; set; } = string.Empty;
}

/// <summary>
/// Failed bindings in a running Avalonia app (M64, ADR 0103): the live agent hooks Avalonia's logger before the app's
/// first binding. Avalonia logs a binding that fails at run time as a <c>Binding</c> warning through
/// <c>Logger.Sink</c>; the app's own sink (<c>LogToTrace</c>, <c>LogToTextWriter</c>, <c>LogToDelegate</c>: Avalonia's
/// <c>StringLogSink</c>) gets its lines as before and the agent reads them too, and without one the agent installs a
/// sink for binding warnings only. A sink of the app's own class is left alone (no failures are reported then). The
/// failures wait for the IDE's <c>bindings</c> request; the agent says <c>event bindings</c> when new ones come.
/// </summary>
internal static class LiveBindings
{
    private const int MaxFailures = 500;
    private static readonly object s_gate = new();
    private static readonly List<BindingFailure> s_failures = [];
    private static readonly HashSet<string> s_seen = new(StringComparer.Ordinal);
    private static Action<string>? s_send;
    private static object? s_hooked;
    private static bool s_notified;
    private static int s_installed;

    /// <summary>Whether the app's logger is hooked: false when the app's own sink isn't Avalonia's (nothing is reported then).</summary>
    public static bool Hooked => s_hooked is not null;

    /// <summary>
    /// Starts watching for Avalonia's logger (from the startup hook, before <c>Main</c>): once Avalonia.Base loads, and
    /// again while the app sets up (its <c>LogToTrace</c> replaces the sink), until the app's <c>Application</c> exists
    /// and a minute has passed. Only reads and writes <c>Logger.Sink</c>, which touches nothing of Avalonia's UI.
    /// </summary>
    public static void Install(Action<string>? send)
    {
        Action<string>? pending = null;
        lock (s_gate)
        {
            s_send = send ?? s_send;
            if (s_failures.Count > 0)
            {
                pending = Notify(); // failures from before the IDE's connection
            }
        }

        Tell(pending);
        if (Interlocked.Exchange(ref s_installed, 1) == 1)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromMinutes(1))
            {
                try
                {
                    Hook();
                }
                catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or MemberAccessException)
                {
                }

                Thread.Sleep(5);
            }
        })
        { IsBackground = true, Name = "Codalus binding log" };
        thread.Start();
    }

    /// <summary>Hooks the logger once Avalonia is loaded (tests call it directly).</summary>
    internal static void Hook()
    {
        var logger = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Avalonia.Base")?.GetType("Avalonia.Logging.Logger", throwOnError: false);
        var property = logger?.GetProperty("Sink", BindingFlags.Public | BindingFlags.Static);
        if (property is null)
        {
            return;
        }

        var sink = property.GetValue(null);
        if (sink is not null && ReferenceEquals(sink, s_hooked))
        {
            return;
        }

        var stringSink = logger!.Assembly.GetType("Avalonia.Logging.StringLogSink", throwOnError: false);
        if (sink is null && stringSink is not null)
        {
            // The app logs nothing: a sink of the agent's own, for binding warnings only.
            var level = Enum.Parse(logger.Assembly.GetType("Avalonia.Logging.LogEventLevel", throwOnError: true)!, "Warning");
            var ours = Activator.CreateInstance(stringSink, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                [new Action<string>(Line), level, new List<string> { "Binding" }], CultureInfo.InvariantCulture);
            property.SetValue(null, ours);
            s_hooked = ours;
            return;
        }

        if (sink is not null && sink.GetType() == stringSink
            && sink.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(f => f.FieldType == typeof(Action<string>)) is { } callback
            && callback.GetValue(sink) is Action<string> previous)
        {
            // The app's sink: its lines go where they went, and to the agent.
            callback.SetValue(sink, new Action<string>(line =>
            {
                previous(line);
                Line(line);
            }));
            s_hooked = sink;
        }
    }

    // [Binding]An error occurred binding 'Text' to 'Missing' at 'Missing': 'Could not find …' (TextBlock #12345)
    private static readonly Regex s_line = new(
        @"^\s*\[Binding\]\s*An error occurred binding '?(?<property>[^' ]+)'? to '?(?<path>.*?)'? at '?(?<at>.*?)'?: (?<message>.*?)\s*(\((?<type>[^()#]+?)\s*#(?<id>[^()#]+)\))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>A binding failure of Avalonia's log, or null for another line.</summary>
    internal static BindingFailure? Parse(string line)
    {
        var match = s_line.Match(line);
        return match.Success
            ? new BindingFailure
            {
                Property = match.Groups["property"].Value,
                Path = match.Groups["path"].Value,
                Message = match.Groups["message"].Value.Trim().Trim('\''),
                TypeName = match.Groups["type"].Value.Trim(),
                Hash = int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ? hash : 0,
                ElementName = match.Groups["id"].Success && !int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? match.Groups["id"].Value.Trim()
                    : string.Empty,
            }
            : null;
    }

    /// <summary>A line of Avalonia's log: a binding failure is kept (once per element, property and path).</summary>
    internal static void Line(string line)
    {
        if (Parse(line) is not { } failure)
        {
            return;
        }

        Action<string>? send = null;
        lock (s_gate)
        {
            if (s_failures.Count >= MaxFailures || !s_seen.Add(failure.Hash + "\u001f" + failure.ElementName + "\u001f" + failure.Property + "\u001f" + failure.Path))
            {
                return;
            }

            s_failures.Add(failure);
            send = Notify();
        }

        Tell(send);

        // With debug.breakOnFailedBinding (M64b): the debugger stops here, at the binding's XAML.
        LiveBreak.OnBindingFailure(failure);
    }

    /// <summary>Says <c>event bindings</c>; a connection that has ended doesn't reach the app's logging.</summary>
    private static void Tell(Action<string>? send)
    {
        try
        {
            send?.Invoke("event\tbindings");
        }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Whom to tell about new failures: the IDE once until it asks for them (under the lock).</summary>
    private static Action<string>? Notify()
    {
        if (s_notified || s_send is null)
        {
            return null;
        }

        s_notified = true;
        return s_send;
    }

    /// <summary>
    /// The failures since the last request (on the UI thread): property, path, message, element type, element name,
    /// and the element's ancestors' classes from the nearest (<c>|</c>-separated), which the IDE matches to the XAML
    /// file whose <c>x:Class</c> declares the binding.
    /// </summary>
    public static string Take(IEnumerable<object> roots)
    {
        List<BindingFailure> failures;
        lock (s_gate)
        {
            failures = [.. s_failures];
            s_failures.Clear();
            s_notified = false;
        }

        if (failures.Count == 0)
        {
            return string.Empty;
        }

        // Avalonia names the element by its Name, or by its hash code when it has none.
        var byHash = new Dictionary<int, object>();
        var byName = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var element in Everything(roots))
        {
            byHash[element.GetHashCode()] = element;
            if (Property(element, "Name") is string { Length: > 0 } name)
            {
                byName[TypeName(element) + "#" + name] = element;
            }
        }

        var records = failures.Select(f =>
        {
            object? element = null;
            if (f.ElementName.Length > 0)
            {
                byName.TryGetValue(f.TypeName + "#" + f.ElementName, out element);
            }
            else if (byHash.TryGetValue(f.Hash, out element) && TypeName(element) != f.TypeName)
            {
                element = null;
            }

            return new[]
            {
                f.Property,
                f.Path,
                f.Message,
                element?.GetType().FullName ?? f.TypeName,
                element is null ? f.ElementName : Property(element, "Name") as string ?? string.Empty,
                element is null ? string.Empty : string.Join("|", Ancestors(element)),
            };
        });
        return string.Join(LiveAgent.Record.ToString(), records.Select(r => string.Join(LiveAgent.Field.ToString(), r.Select(Clean))));
    }

    private static string Clean(string text) =>
        (text ?? string.Empty).Replace(LiveAgent.Record, ' ').Replace(LiveAgent.Field, ' ').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>The classes the element sits in, nearest first (for a stop at a failed binding).</summary>
    internal static IEnumerable<string> AncestorsOf(object element) => Ancestors(element);

    /// <summary>The classes the element sits in, nearest first: templated parents, then logical or visual parents.</summary>
    private static IEnumerable<string> Ancestors(object element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { element };
        for (var current = Parent(element); current is not null && visited.Add(current) && visited.Count < 512; current = Parent(current))
        {
            if (current.GetType().FullName is { } name && seen.Add(name))
            {
                yield return name;
            }
        }
    }

    private static object? Parent(object element) =>
        LogicalParent(element) ?? Property(element, "TemplatedParent") ?? VisualParent(element);

    /// <summary>Every element of the roots' visual and logical trees.</summary>
    internal static IEnumerable<object> Everything(IEnumerable<object> roots)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>(roots);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (!seen.Add(element))
            {
                continue;
            }

            yield return element;
            foreach (var child in LogicalChildren(element).Concat(VisualChildrenOf(element)))
            {
                stack.Push(child);
            }
        }
    }

    private static MethodInfo? s_getVisualChildren;

    private static IEnumerable<object> VisualChildrenOf(object element)
    {
        s_getVisualChildren ??= AvaloniaType("Avalonia.VisualTree.VisualExtensions").GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "GetVisualChildren" && !m.IsGenericMethod && m.GetParameters().Length == 1);
        return s_getVisualChildren?.Invoke(null, [element]) is IEnumerable children ? children.Cast<object>() : [];
    }
}

/// <summary>
/// XAML Hot Reload in a running Avalonia app (M64, ADR 0103, D56): a saved XAML file's text is loaded again into each
/// live instance of its <c>x:Class</c> by Avalonia's runtime XAML compiler, the one in the app's own Avalonia
/// designer host (<c>Avalonia.Designer.HostApp.dll</c>, its <c>AvaloniaXamlIlRuntimeCompiler</c>, loaded once), on
/// the UI thread. The instance keeps its data context and the state XAML doesn't set; its styles, resources and data
/// templates are emptied first (the XAML adds them again), its name scope is new, and its named fields point at the
/// new elements. Event handlers the XAML names are the running class's methods. What can't be loaded (a parse error,
/// a handler or type the running app doesn't have, an <c>x:Class</c> it doesn't have) leaves the instance as it was
/// and comes back as a failure with its line.
/// </summary>
internal static class LiveReload
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Dictionary<string, MethodInfo> s_loaders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex s_class = new(@"\bx:Class\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>applied count</c>, or <c>failed line column message</c> (line and column 0 when unknown); <c>none</c> when the
    /// file has no <c>x:Class</c> (a styles or resource dictionary file, which a restart applies).
    /// </summary>
    public static string Reload(IEnumerable<object> roots, string loaderPath, string assemblyName, string uri, bool compiledBindings, string xaml, string? path = null)
    {
        var className = s_class.Match(xaml) is { Success: true } match ? match.Groups["name"].Value.Trim() : null;
        if (className is null)
        {
            return "none";
        }

        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, className)).FirstOrDefault(t => t is not null);
        if (type is null)
        {
            return Failed(0, 0, $"The running app has no class {className} (x:Class): restart it to apply this XAML.");
        }

        var local = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName) ?? type.Assembly;
        MethodInfo load;
        try
        {
            load = Loader(loaderPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(0, 0, "Avalonia's runtime XAML loader couldn't be loaded: " + (ex.InnerException ?? ex).Message);
        }

        var instances = Instances(roots, type);
        var count = 0;
        foreach (var instance in instances)
        {
            var failure = Populate(load, instance, local, uri, compiledBindings, xaml, path);
            if (failure is not null)
            {
                return failure;
            }

            count++;
        }

        return "applied\t" + count.ToString(CultureInfo.InvariantCulture);
    }

    private static Type? SafeType(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetType(name, throwOnError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or System.IO.IOException)
        {
            return null;
        }
    }

    private static string Failed(int line, int column, string message) =>
        "failed\t" + line.ToString(CultureInfo.InvariantCulture) + "\t" + column.ToString(CultureInfo.InvariantCulture) + "\t"
        + message.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Avalonia's runtime compiler's <c>Load(RuntimeXamlLoaderDocument, RuntimeXamlLoaderConfiguration)</c>, from the designer host's assembly.</summary>
    private static MethodInfo Loader(string path)
    {
        lock (s_loaders)
        {
            if (s_loaders.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && string.Equals(a.Location, path, StringComparison.OrdinalIgnoreCase))
                ?? Assembly.LoadFrom(path);
            var compiler = assembly.GetType("Avalonia.Markup.Xaml.XamlIl.AvaloniaXamlIlRuntimeCompiler", throwOnError: true)!;
            var document = AvaloniaType("Avalonia.Markup.Xaml.RuntimeXamlLoaderDocument");
            var configuration = AvaloniaType("Avalonia.Markup.Xaml.RuntimeXamlLoaderConfiguration");
            var load = compiler.GetMethod("Load", Any, null, [document, configuration], null)
                ?? throw new InvalidOperationException("The runtime XAML compiler has no Load(document, configuration).");
            s_loaders[path] = load;
            return load;
        }
    }

    /// <summary>The live objects of exactly <paramref name="type"/>: the application, windows, and controls in their trees.</summary>
    private static List<object> Instances(IEnumerable<object> roots, Type type)
    {
        var all = new List<object>();
        if (AvaloniaType("Avalonia.Application").GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is { } application && application.GetType() == type)
        {
            all.Add(application);
        }

        all.AddRange(LiveBindings.Everything(roots).Where(e => e.GetType() == type));
        return all;
    }

    /// <summary>Loads the XAML into one instance; null when it applied, else the failure (the instance put back as it was).</summary>
    private static string? Populate(MethodInfo load, object instance, Assembly local, string uri, bool compiledBindings, string xaml, string? path)
    {
        var restore = new List<Action>();
        try
        {
            Empty(instance, "Styles", restore);
            Empty(instance, "DataTemplates", restore);
            if (Property(instance, "Resources") is { } resources)
            {
                Empty(resources, "MergedDictionaries", restore);
                EmptyDictionary(resources, restore);
            }

            if (instance.GetType().GetProperty("Content") is { CanRead: true, CanWrite: true } content)
            {
                var old = content.GetValue(instance);
                restore.Add(() => content.SetValue(instance, old));
            }

            // A fresh name scope: the old one is complete and holds the old elements' names.
            var nameScopeType = AvaloniaType("Avalonia.Controls.NameScope");
            var getScope = nameScopeType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetNameScope" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(instance));
            var setScope = nameScopeType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "SetNameScope" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsInstanceOfType(instance));
            if (getScope is not null && setScope is not null)
            {
                var oldScope = getScope.Invoke(null, [instance]);
                setScope.Invoke(null, [instance, Activator.CreateInstance(nameScopeType)]);
                restore.Add(() => setScope.Invoke(null, [instance, oldScope]));
            }

            var configuration = Activator.CreateInstance(load.GetParameters()[1].ParameterType)!;
            configuration.GetType().GetProperty("LocalAssembly")?.SetValue(configuration, local);
            configuration.GetType().GetProperty("UseCompiledBindingsByDefault")?.SetValue(configuration, compiledBindings);
            configuration.GetType().GetProperty("DesignMode")?.SetValue(configuration, false);
            var document = Activator.CreateInstance(load.GetParameters()[0].ParameterType, [new Uri(uri), instance, xaml])!;
            if (path is { Length: > 0 } && path != "-" && configuration.GetType().GetProperty("CreateSourceInfo") is { CanWrite: true } sourceInfo)
            {
                // The reloaded elements keep their file and lines (Avalonia 12): XAML breakpoints still stop at them (M64b).
                sourceInfo.SetValue(configuration, true);
                document.GetType().GetProperty("Document")?.SetValue(document, path);
            }

            load.Invoke(null, [document, configuration]);

            // The named fields (Avalonia's name generator's, or the class's own): the new elements.
            if (getScope?.Invoke(null, [instance]) is { } scope && scope.GetType().GetMethod("Find", [typeof(string)]) is { } find)
            {
                for (var type = instance.GetType(); type is not null && type.Assembly == instance.GetType().Assembly; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (find.Invoke(scope, [field.Name]) is { } named && field.FieldType.IsInstanceOfType(named))
                        {
                            field.SetValue(instance, named);
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            for (var i = restore.Count - 1; i >= 0; i--)
            {
                try
                {
                    restore[i]();
                }
                catch (Exception undo) when (undo is not OutOfMemoryException)
                {
                    _ = undo; // as much as can be put back
                }
            }

            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            var (line, column) = Position(error);
            return Failed(line, column, error.Message);
        }
    }

    /// <summary>Empties a list property (<c>Styles</c>, <c>DataTemplates</c>, <c>MergedDictionaries</c>), to be filled again; the undo puts its items back.</summary>
    private static void Empty(object owner, string name, List<Action> restore)
    {
        if (Property(owner, name) is not IEnumerable list || list.GetType().GetMethod("Clear", Type.EmptyTypes) is not { } clear
            || list.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1) is not { } add)
        {
            return;
        }

        var items = list.Cast<object>().ToList();
        clear.Invoke(list, null);
        restore.Add(() =>
        {
            clear.Invoke(list, null);
            foreach (var item in items)
            {
                add.Invoke(list, [item]);
            }
        });
    }

    /// <summary>Empties a resource dictionary's own entries; the undo puts them back.</summary>
    private static void EmptyDictionary(object resources, List<Action> restore)
    {
        if (resources is not IEnumerable entries || resources.GetType().GetMethod("Clear", Type.EmptyTypes) is not { } clear
            || resources.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 2) is not { } add)
        {
            return;
        }

        var items = entries.Cast<object>().Select(e => (Key: Property(e, "Key"), Value: Property(e, "Value"))).Where(e => e.Key is not null).ToList();
        clear.Invoke(resources, null);
        restore.Add(() =>
        {
            clear.Invoke(resources, null);
            foreach (var (key, value) in items)
            {
                add.Invoke(resources, [key, value]);
            }
        });
    }

    /// <summary>The line and column an exception of the XAML loader names (its <c>LineNumber</c> and <c>LinePosition</c>), else 0, 0.</summary>
    private static (int Line, int Column) Position(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            var line = (current.GetType().GetProperty("LineNumber") ?? current.GetType().GetProperty("Line"))?.GetValue(current);
            var column = (current.GetType().GetProperty("LinePosition") ?? current.GetType().GetProperty("Position"))?.GetValue(current);
            if (line is int l && l > 0)
            {
                return (l, column is int c ? c : 0);
            }
        }

        var match = Regex.Match(error.Message, @"[Ll]ine (?<line>\d+),? (?:position|column|col)\s*(?<column>\d+)", RegexOptions.CultureInvariant);
        return match.Success
            ? (int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>The text of a <c>reload</c> request's base64 argument.</summary>
    public static string Decode(string base64) => new UTF8Encoding(false).GetString(Convert.FromBase64String(base64));
}
