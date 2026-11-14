using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static Codalus.Previewer.Agent.PreviewAgent;

namespace Codalus.Previewer.Agent;

/// <summary>A XAML breakpoint the IDE set (M64b): on an element's start tag, or on one of its attributes.</summary>
internal sealed class XamlBreakpoint
{
    public string Id { get; set; } = string.Empty;

    /// <summary><c>element</c> (stops when the element is created) or <c>attribute</c> (when XAML sets its property).</summary>
    public string Kind { get; set; } = "element";

    public string File { get; set; } = string.Empty;

    /// <summary>The element's line and the column of its name (1-based; 0: any element starting on the line).</summary>
    public int Line { get; set; }

    public int Column { get; set; }

    /// <summary>The attribute's property: <c>Text</c>, or an attached one's <c>DockPanel.Dock</c>.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>The attribute's value is a binding or a dynamic resource: it stops at each value that sets, not only at creation.</summary>
    public bool Binding { get; set; }

    /// <summary>Why it can't stop, once the agent knows (an attribute that names no property of the element).</summary>
    public string? Problem { get; set; }
}

/// <summary>Where and why the app stopped (sent to the IDE just before the break): the agent frame's locals show it too.</summary>
internal sealed class XamlStop
{
    public string Kind { get; set; } = string.Empty;

    public string BreakpointId { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public int Line { get; set; }

    public int Column { get; set; }

    public string ElementType { get; set; } = string.Empty;

    public string ElementName { get; set; } = string.Empty;

    public string Property { get; set; } = string.Empty;

    public string OldValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>A failed binding's path, and the classes its element sits in (nearest first).</summary>
    public string BindingPath { get; set; } = string.Empty;

    public string Ancestors { get; set; } = string.Empty;

    public string Encode() => string.Join(LiveAgent.Field.ToString(), new[]
    {
        Kind, BreakpointId, File, Line.ToString(CultureInfo.InvariantCulture), Column.ToString(CultureInfo.InvariantCulture), ElementType, ElementName,
        Property, OldValue, NewValue, Message, BindingPath, Ancestors,
    }.Select(LiveBreak.Clean));
}

/// <summary>
/// XAML breakpoints in a running Avalonia app (M64b, ADR 0103), under a debugger. Avalonia 12's XAML compiler gives
/// every object it creates its XAML file, line and column (<c>XamlSourceInfo</c>, an attached property set right
/// after the constructor, before XAML sets anything else, in builds with <c>AvaloniaXamlCreateSourceInfo</c>: Debug
/// builds by default). The agent watches that property from the moment Avalonia's XAML assembly loads (the
/// breakpoints come with the app's environment, so the first window's elements are caught):
/// <list type="bullet">
/// <item>an <b>element breakpoint</b> stops when an object of that line is created (its constructor has run; XAML
/// hasn't set its properties yet);</item>
/// <item>an <b>attribute breakpoint</b> watches each object of its element's line and stops when its property changes
/// while XAML initializes it, and, for a binding or a dynamic resource, at each value that sets later;</item>
/// <item><b>Break When Value Changes</b> (the Live Property Explorer) stops when a live element's property changes,
/// wherever from;</item>
/// <item>with <c>debug.breakOnFailedBinding</c>, a binding that fails stops the app as an exception would.</item>
/// </list>
/// A stop sends <c>event xstop …</c> (<see cref="XamlStop"/>) to the IDE, then calls <see cref="Debugger.Break"/> on
/// the thread that created or changed the object, so the debugger stops there with its whole stack; the IDE shows the
/// XAML line as the stop's location. Nothing stops without a debugger attached, or before the IDE's connection.
/// </summary>
internal static class LiveBreak
{
    /// <summary>The breakpoints at launch (<see cref="Encode"/>'s text, base64): the agent arms them before the app's first XAML.</summary>
    public const string BreakpointsVariable = "CODALUS_LIVE_AGENT_BREAKPOINTS";

    private static readonly object s_gate = new();
    private static readonly ManualResetEventSlim s_connected = new();
    private static List<XamlBreakpoint> s_breakpoints = [];
    private static bool s_breakOnBindings;
    private static Action<string>? s_send;
    private static int s_installed;
    private static string? s_unsupported;
    private static bool s_sawSourceInfo;
    private static bool s_toldSourceInfo;
    private static PropertyInfo? s_sender;
    private static PropertyInfo? s_newValue;
    private static PropertyInfo? s_oldValue;
    private static PropertyInfo? s_property;
    private static EventInfo? s_propertyChanged;

    /// <summary>The break itself: <see cref="Debugger.Break"/> (tests record the stops instead).</summary>
    internal static Action<XamlStop, object?> Breaker { get; set; } = (_, _) => Debugger.Break();

    /// <summary>Whether a debugger is attached (tests say yes).</summary>
    internal static Func<bool> Attached { get; set; } = () => Debugger.IsAttached;

    /// <summary>How long a stop waits for the IDE's connection (the first window can be made before it's there).</summary>
    internal static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether a failed binding stops the app.</summary>
    public static bool BreakOnBindings => Volatile.Read(ref s_breakOnBindings);

    internal static IReadOnlyList<XamlBreakpoint> Breakpoints
    {
        get
        {
            lock (s_gate)
            {
                return s_breakpoints;
            }
        }
    }

    internal static string Clean(string? text) =>
        (text ?? string.Empty).Replace(LiveAgent.Record, ' ').Replace(LiveAgent.Field, ' ').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ------------------------------------------------------------------ the breakpoints

    /// <summary>
    /// The breakpoints' text: <c>bindings 1|0</c>, then a line per breakpoint: id, kind, file, line, column, property,
    /// binding (1 or 0), tab-separated.
    /// </summary>
    internal static void Configure(string text)
    {
        var breakpoints = new List<XamlBreakpoint>();
        var bindings = false;
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields[0] == "bindings" && fields.Length > 1)
            {
                bindings = fields[1] == "1";
            }
            else if (fields[0] == "bp" && fields.Length >= 8)
            {
                breakpoints.Add(new XamlBreakpoint
                {
                    Id = fields[1],
                    Kind = fields[2],
                    File = fields[3],
                    Line = Int(fields[4]),
                    Column = Int(fields[5]),
                    Property = fields[6],
                    Binding = fields[7] == "1",
                });
            }
        }

        lock (s_gate)
        {
            s_breakpoints = breakpoints;
        }

        Volatile.Write(ref s_breakOnBindings, bindings);
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>The breakpoints from the launch's environment, if any (the startup hook, before <c>Main</c>).</summary>
    public static void ConfigureFromEnvironment()
    {
        var encoded = Environment.GetEnvironmentVariable(BreakpointsVariable);
        Environment.SetEnvironmentVariable(BreakpointsVariable, null);
        if (string.IsNullOrEmpty(encoded))
        {
            return;
        }

        try
        {
            Configure(Decode(encoded!));
        }
        catch (FormatException)
        {
        }
    }

    internal static string Decode(string base64) => new UTF8Encoding(false).GetString(Convert.FromBase64String(base64));

    /// <summary>
    /// <c>xbreaks base64</c>: the IDE's breakpoints (all of them: they replace the last list); answers each one's id,
    /// <c>1</c> when it can stop or <c>0</c> and why not, records separated as the other lists.
    /// </summary>
    public static string Set(string base64)
    {
        Configure(Decode(base64));
        return States();
    }

    /// <summary>Each breakpoint's state, as <see cref="Set"/> answers.</summary>
    public static string States()
    {
        var problem = Unbindable();
        return string.Join(LiveAgent.Record.ToString(), Breakpoints.Select(b => string.Join(LiveAgent.Field.ToString(),
            Clean(b.Id), problem is null && b.Problem is null ? "1" : "0", Clean(problem ?? b.Problem))));
    }

    /// <summary>Why no XAML breakpoint of this app can stop, or null.</summary>
    private static string? Unbindable()
    {
        if (s_unsupported is { } unsupported)
        {
            return unsupported;
        }

        // Avalonia's XAML (App.axaml, the theme) is loaded with source info at the app's start in a build that has it:
        // an app that has started without any has none.
        if (!Volatile.Read(ref s_sawSourceInfo) && Volatile.Read(ref s_installed) == 1 && ApplicationStarted())
        {
            return "The app's XAML was compiled without source information (AvaloniaXamlCreateSourceInfo, on in Debug builds): build it in Debug to stop at XAML.";
        }

        return null;
    }

    private static bool ApplicationStarted()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Avalonia.Controls")
                .Select(a => a.GetType("Avalonia.Application", throwOnError: false))
                .FirstOrDefault(t => t is not null)?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not null
                && LiveAgent.LifetimeWindowCount() > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ the hook

    /// <summary>The IDE's connection: stops can be told now, and <c>event xbreaks</c> says the states changed.</summary>
    public static void Connect(Action<string> send)
    {
        lock (s_gate)
        {
            s_send = send;
        }

        s_connected.Set();
    }

    /// <summary>
    /// Watches Avalonia's <c>XamlSourceInfo</c> once Avalonia's XAML assembly loads (from the startup hook, before the
    /// app's first XAML), on the thread that loads it; at once when it's loaded already (tests).
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 2) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            if (e.LoadedAssembly.GetName().Name == "Avalonia.Markup.Xaml")
            {
                Hook(e.LoadedAssembly);
            }
        };
        if (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Avalonia.Markup.Xaml") is { } loaded)
        {
            Hook(loaded);
        }
    }

    private static void Hook(Assembly markup)
    {
        lock (s_gate)
        {
            if (Volatile.Read(ref s_installed) != 2)
            {
                return;
            }

            try
            {
                var sourceInfo = markup.GetType("Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo", throwOnError: false);
                if (sourceInfo is null)
                {
                    s_unsupported = "This app's Avalonia gives its XAML no source information at run time (Avalonia 12 or later does): XAML breakpoints can't stop.";
                    Volatile.Write(ref s_installed, 1);
                    return;
                }

                RuntimeHelpers.RunClassConstructor(sourceInfo.TypeHandle);
                var property = sourceInfo.GetField("s_xamlSourceInfo", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? throw new InvalidOperationException("XamlSourceInfo has no attached property.");
                // AvaloniaProperty's own Changed (AvaloniaProperty<T> hides it with a typed one: either is an IObservable<object>).
                var owner = property.GetType();
                while (owner is not null && owner.FullName != "Avalonia.AvaloniaProperty")
                {
                    owner = owner.BaseType;
                }

                if (owner?.GetProperty("Changed", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(property) is not IObservable<object> changed)
                {
                    throw new InvalidOperationException("XamlSourceInfo's property has no Changed observable.");
                }

                changed.Subscribe(new Observer(OnSourceInfo));
                Volatile.Write(ref s_installed, 1);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or MemberAccessException or TypeLoadException)
            {
                s_unsupported = "XAML breakpoints couldn't watch this app's XAML: " + (ex.InnerException ?? ex).Message;
                Volatile.Write(ref s_installed, 1);
            }
        }
    }

    /// <summary>An observer of the property's changes (<c>IObservable&lt;T&gt;</c> is covariant: any change arguments are objects).</summary>
    private sealed class Observer(Action<object> next) : IObserver<object>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(object value) => next(value);
    }

    /// <summary>The non-generic <c>AvaloniaPropertyChangedEventArgs</c>'s properties (the generic one hides them).</summary>
    private static void EnsureArgs(object args)
    {
        if (s_sender is not null)
        {
            return;
        }

        var type = args.GetType();
        while (type is not null && type.FullName != "Avalonia.AvaloniaPropertyChangedEventArgs")
        {
            type = type.BaseType;
        }

        type ??= args.GetType();
        s_newValue = type.GetProperty("NewValue", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? type.GetProperty("NewValue");
        s_oldValue = type.GetProperty("OldValue", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? type.GetProperty("OldValue");
        s_property = type.GetProperty("Property", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? type.GetProperty("Property");
        s_sender = type.GetProperty("Sender", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? type.GetProperty("Sender");
    }

    internal static (object? Sender, object? Property, object? OldValue, object? NewValue) Read(object args)
    {
        EnsureArgs(args);
        return (s_sender?.GetValue(args), s_property?.GetValue(args), s_oldValue?.GetValue(args), s_newValue?.GetValue(args));
    }

    /// <summary>The file, line and column of an object's <c>XamlSourceInfo</c>.</summary>
    internal static (string File, int Line, int Column)? Location(object? info)
    {
        if (info is null || Property(info, "SourceUri") is not Uri uri)
        {
            return null;
        }

        return (uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString, Property(info, "LineNumber") is int line ? line : 0, Property(info, "LinePosition") is int column ? column : 0);
    }

    /// <summary>An element's own <c>XamlSourceInfo</c> (on its UI thread), or null.</summary>
    internal static (string File, int Line, int Column)? LocationOf(object element)
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Avalonia.Markup.Xaml")
                ?.GetType("Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo", throwOnError: false);
            var get = type?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetXamlSourceInfo" && m.GetParameters().Length == 1);
            return get is null ? null : Location(get.Invoke(null, [element]));
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static readonly bool s_caseSensitivePaths = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>The same file (macOS's <c>/private</c> prefix of temporary folders aside).</summary>
    internal static bool SameFile(string a, string b)
    {
        static string Normal(string path)
        {
            var normal = path.Replace('\\', '/');
            return normal.StartsWith("/private/", StringComparison.Ordinal) ? normal.Substring("/private".Length) : normal;
        }

        return string.Equals(Normal(a), Normal(b), s_caseSensitivePaths ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An object of the app's XAML got its source information: right after its constructor, before its properties.</summary>
    private static void OnSourceInfo(object args)
    {
        try
        {
            // Every object of the app's XAML comes here: without breakpoints, nothing is read.
            if (Volatile.Read(ref s_sawSourceInfo) && Breakpoints.Count == 0)
            {
                return;
            }

            var (sender, _, _, value) = Read(args);
            if (!Volatile.Read(ref s_sawSourceInfo) && value is not null)
            {
                Volatile.Write(ref s_sawSourceInfo, true);
                SourceInfoSeen();
            }

            if (sender is null || Location(value) is not { } location)
            {
                return;
            }

            foreach (var breakpoint in Breakpoints)
            {
                if (breakpoint.Line != location.Line || (breakpoint.Column > 0 && breakpoint.Column != location.Column) || !SameFile(breakpoint.File, location.File))
                {
                    continue;
                }

                if (breakpoint.Kind == "attribute")
                {
                    Arm(sender, breakpoint, location);
                }
                else
                {
                    Stop(new XamlStop
                    {
                        Kind = "element",
                        BreakpointId = breakpoint.Id,
                        File = breakpoint.File,
                        Line = location.Line,
                        Column = location.Column,
                        ElementType = sender.GetType().FullName ?? TypeName(sender),
                    }, sender);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or MemberAccessException)
        {
            // A breakpoint never breaks the app's XAML.
        }
    }

    // ------------------------------------------------------------------ attributes and values

    /// <summary>Whether <paramref name="property"/> (an AvaloniaProperty) is the one <paramref name="name"/> names (<c>Text</c>, <c>DockPanel.Dock</c>).</summary>
    internal static bool Names(object property, string name)
    {
        var dot = name.LastIndexOf('.');
        if ((Property(property, "Name") as string) != (dot < 0 ? name : name.Substring(dot + 1)))
        {
            return false;
        }

        return dot < 0 || (Property(property, "OwnerType") as Type)?.Name == name.Substring(0, dot);
    }

    private static readonly Dictionary<string, bool> s_hasProperty = new(StringComparer.Ordinal);

    /// <summary>Whether the element has an Avalonia property of that name (its type's, or an attached one), per type and name.</summary>
    private static bool HasProperty(object element, string name)
    {
        var key = element.GetType().AssemblyQualifiedName + "\u001f" + name;
        lock (s_hasProperty)
        {
            if (s_hasProperty.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        var has = FindProperty(element, name);
        lock (s_hasProperty)
        {
            s_hasProperty[key] = has;
        }

        return has;
    }

    private static bool FindProperty(object element, string name)
    {
        var registryType = AvaloniaType("Avalonia.AvaloniaPropertyRegistry");
        var registry = registryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            var attached = registryType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetRegisteredAttached" && m.GetParameters().Length == 1);
            var owner = name.Substring(0, dot);
            return AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).SelectMany(SafeTypes).Where(t => t.Name == owner)
                .Any(t => attached?.Invoke(registry, [t]) is System.Collections.IEnumerable properties && properties.Cast<object>().Any(p => Names(p, name)));
        }

        var registered = registryType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetRegistered" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
        return registered?.Invoke(registry, [element.GetType()]) is System.Collections.IEnumerable own && own.Cast<object>().Any(p => Names(p, name));
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        if (assembly.GetName().Name?.StartsWith("System.", StringComparison.Ordinal) == true)
        {
            return [];
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>The element of an attribute breakpoint: its property's changes stop while XAML initializes it (and later for a binding).</summary>
    private static void Arm(object element, XamlBreakpoint breakpoint, (string File, int Line, int Column) location)
    {
        if (breakpoint.Problem is null && !HasProperty(element, breakpoint.Property))
        {
            breakpoint.Problem = $"{TypeName(element)} has no property {breakpoint.Property} that XAML sets (an event, a directive or a plain CLR property): put the breakpoint on the element.";
            Tell("event\txbreaks");
            return;
        }

        if (breakpoint.Problem is not null)
        {
            return;
        }

        Watch(element, new PropertyWatch(element, breakpoint.Property, (watch, property, oldValue, newValue) =>
        {
            var initializing = Property(element, "IsInitialized") is false;
            if ((!initializing && !breakpoint.Binding) || !Breakpoints.Any(b => b.Id == breakpoint.Id && b.Kind == breakpoint.Kind && b.Line == breakpoint.Line && b.Property == breakpoint.Property))
            {
                watch.Stop(); // XAML has set it (a later change isn't the attribute's), or the breakpoint is gone
                return;
            }

            Stop(new XamlStop
            {
                Kind = "attribute",
                BreakpointId = breakpoint.Id,
                File = breakpoint.File,
                Line = location.Line,
                Column = location.Column,
                ElementType = element.GetType().FullName ?? TypeName(element),
                ElementName = Property(element, "Name") as string ?? string.Empty,
                Property = breakpoint.Property,
                OldValue = Format(oldValue),
                NewValue = Format(newValue),
            }, element);
        }));
    }

    internal static string Format(object? value) => value switch
    {
        null => "(null)",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ when value.GetType().FullName == "Avalonia.AvaloniaProperty+UnsetValueType" || value.GetType().Name == "UnsetValueType" => "(unset)",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Subscribes the element's <c>PropertyChanged</c> for the watch (on its UI thread); the watch unsubscribes itself.</summary>
    internal static void Watch(object element, PropertyWatch watch)
    {
        s_propertyChanged ??= AvaloniaType("Avalonia.AvaloniaObject").GetEvent("PropertyChanged", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AvaloniaObject has no PropertyChanged event.");
        var handler = Delegate.CreateDelegate(s_propertyChanged.EventHandlerType!, watch, typeof(PropertyWatch).GetMethod(nameof(PropertyWatch.OnChanged))!);
        watch.Unsubscribe = () => s_propertyChanged.RemoveEventHandler(element, handler);
        s_propertyChanged.AddEventHandler(element, handler);
    }

    /// <summary>Break When Value Changes on a live element's property (the Live Property Explorer), or off.</summary>
    public static PropertyWatch ValueWatch(object element, string name) =>
        new(element, name, (_, _, oldValue, newValue) =>
        {
            var location = LocationOf(element);
            Stop(new XamlStop
            {
                Kind = "value",
                File = location?.File ?? string.Empty,
                Line = location?.Line ?? 0,
                Column = location?.Column ?? 0,
                ElementType = element.GetType().FullName ?? TypeName(element),
                ElementName = Property(element, "Name") as string ?? string.Empty,
                Property = name,
                OldValue = Format(oldValue),
                NewValue = Format(newValue),
            }, element);
        });

    // ------------------------------------------------------------------ failed bindings

    /// <summary>A binding that failed (on the thread that logged it): a stop when the setting is on, at the binding's XAML.</summary>
    public static void OnBindingFailure(BindingFailure failure)
    {
        if (!BreakOnBindings || !Attached())
        {
            return;
        }

        object? element = null;
        var ancestors = string.Empty;
        try
        {
            // The element, by the name or hash code the log gives it: in the windows open, on their UI thread.
            if (LiveAgent.OnAvaloniaUiThread())
            {
                element = LiveBindings.Everything(LiveAgent.LifetimeWindowsList()).FirstOrDefault(e => TypeName(e) == failure.TypeName
                    && (failure.ElementName.Length > 0 ? Property(e, "Name") as string == failure.ElementName : e.GetHashCode() == failure.Hash));
                ancestors = element is null ? string.Empty : string.Join("|", LiveBindings.AncestorsOf(element));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
            element = null;
        }

        var location = element is null ? null : LocationOf(element);
        Stop(new XamlStop
        {
            Kind = "binding",
            File = location?.File ?? string.Empty,
            Line = location?.Line ?? 0,
            Column = location?.Column ?? 0,
            ElementType = element?.GetType().FullName ?? failure.TypeName,
            ElementName = element is null ? failure.ElementName : Property(element, "Name") as string ?? string.Empty,
            Property = failure.Property,
            Message = failure.Message,
            BindingPath = failure.Path,
            Ancestors = ancestors,
        }, element);
    }

    // ------------------------------------------------------------------ the stop

    private static void Tell(string line)
    {
        Action<string>? send;
        lock (s_gate)
        {
            send = s_send;
        }

        try
        {
            send?.Invoke(line);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>The first source information the app's XAML gave: the IDE asks the states again (they're bound now).</summary>
    internal static void SourceInfoSeen()
    {
        if (Volatile.Read(ref s_sawSourceInfo) && !Volatile.Read(ref s_toldSourceInfo))
        {
            Volatile.Write(ref s_toldSourceInfo, true);
            Tell("event\txbreaks");
        }
    }

    /// <summary>
    /// Tells the IDE where and why, then breaks into the debugger on this thread. The parameters are what the agent
    /// frame's Locals show: the stop and the object (the element created, or whose property changed).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Stop(XamlStop stop, object? element)
    {
        if (!Attached())
        {
            return;
        }

        // The app's first window can come before the connection: wait for it (the IDE is listening).
        if (!s_connected.Wait(ConnectTimeout))
        {
            return;
        }

        Tell("event\txstop\t" + stop.Encode());
        GC.KeepAlive(element);
        Breaker(stop, element);
    }
}

/// <summary>A watch on one property of one element (its <c>PropertyChanged</c>); <see cref="Stop"/> ends it.</summary>
internal sealed class PropertyWatch(object element, string name, Action<PropertyWatch, object, object?, object?> changed)
{
    private int _reentered;

    public string Name { get; } = name;

    public WeakReference Element { get; } = new(element);

    internal Action? Unsubscribe { get; set; }

    public void Stop()
    {
        var unsubscribe = Unsubscribe;
        Unsubscribe = null;
        unsubscribe?.Invoke();
    }

    // Bound to EventHandler<AvaloniaPropertyChangedEventArgs> (parameters are contravariant).
    public void OnChanged(object? sender, EventArgs e)
    {
        var (_, property, oldValue, newValue) = LiveBreak.Read(e);
        if (property is null || !LiveBreak.Names(property, Name) || Interlocked.Exchange(ref _reentered, 1) == 1)
        {
            return;
        }

        try
        {
            changed(this, property, oldValue, newValue);
        }
        finally
        {
            Volatile.Write(ref _reentered, 0);
        }
    }
}
