using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using static Codalus.Previewer.Agent.PreviewAgent;

namespace Codalus.Previewer.Agent;

/// <summary>
/// The live agent (M19b, ADR 0052): the Live Visual Tree and Live Property Explorer's view of a running Avalonia app.
/// Lists are records separated by <see cref="Record"/>, their fields by <see cref="Field"/>; elements have ids that
/// stay the same while they live. Commands, run on Avalonia's UI thread through reflection:
/// <list type="bullet">
/// <item><c>windows</c> → id, title, type (the window's class) per open window</item>
/// <item><c>tree id logical|visual</c> → id, parent id, type, name, templated (1 when a template made it) per element,
/// depth first from the element</item>
/// <item><c>props id</c> → name, value, type, source, read-only (1 or 0) per Avalonia property</item>
/// <item><c>set id property value</c> → <c>ok</c>: the value parsed as XAML would for the property's type, as a local value</item>
/// <item><c>highlight id|none</c> → <c>ok</c>: an adorner over the element in the app</item>
/// <item><c>pick on|off</c> → <c>ok</c>: while on, the next click in the app selects its element and sends
/// <c>event picked id logicalId</c> (the element hit, and the nearest one the XAML declares)</item>
/// <item><c>path id</c> → the window's class and the element's logical path from it (<c>Type:ordinal:Name</c> steps
/// joined by <c>/</c>), which the IDE resolves to a line of the window's XAML</item>
/// </list>
/// </summary>
internal sealed class LiveAgent(Action<string> send, Func<IEnumerable<object>>? roots = null)
{
    public const char Record = '\u001e';
    public const char Field = '\u001f';

    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

    private readonly ConditionalWeakTable<object, object> _ids = new();
    private readonly Dictionary<int, WeakReference> _elements = [];
    private readonly HashSet<object> _hooked = new(ReferenceEqualityComparer.Instance);
    private int _next;
    private object? _adorner;
    private object? _adornerLayer;
    private bool _picking;
    private bool _started;

    public string Handle(string[] request)
    {
        var command = request[1];
        string Arg(int index) => request.Length > index ? request[index] : string.Empty;
        if (command != "ping")
        {
            EnsureStarted();
        }

        return command switch
        {
            "ping" => "pong",
            "windows" => OnUiThread(Windows),
            "tree" => OnUiThread(() => Tree(Element(Arg(2)), Arg(3) == "visual")),
            "props" => OnUiThread(() => Props(Element(Arg(2)))),
            "set" => OnUiThread(() => Set(Element(Arg(2)), Arg(3), Arg(4))),
            "highlight" => OnUiThread(() => Highlight(Arg(2) == "none" ? null : Element(Arg(2)))),
            "pick" => OnUiThread(() => Pick(Arg(2) == "on")),
            "path" => OnUiThread(() => Path(Element(Arg(2)))),
            _ => throw new InvalidOperationException("Unknown command " + command),
        };
    }

    /// <summary>
    /// Nothing of Avalonia's until the app has started it. The agent connects before <c>Main</c>, and the first touch of
    /// <c>Dispatcher.UIThread</c> binds Avalonia's UI thread to the caller: from here, the app then failed to start
    /// ("the calling thread cannot access this object"). <c>Application.Current</c> is set after Avalonia's platform
    /// made the dispatcher on the app's thread, and reading it touches nothing. Until then: an error the IDE retries.
    /// </summary>
    private void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        var application = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "Avalonia.Base" || a.GetName().Name == "Avalonia.Controls")
            .Select(a => a.GetType("Avalonia.Application", throwOnError: false))
            .FirstOrDefault(t => t is not null)
            ?.GetProperty("Current", Static)?.GetValue(null);
        _started = application is not null || roots is not null; // tests pass their windows: started
        if (!_started)
        {
            throw new InvalidOperationException("The app hasn't started Avalonia yet.");
        }
    }

    // ------------------------------------------------------------------ elements and ids

    private int IdOf(object element)
    {
        if (_ids.TryGetValue(element, out var boxed))
        {
            return (int)boxed;
        }

        var id = ++_next;
        _ids.Add(element, id);
        _elements[id] = new WeakReference(element);
        return id;
    }

    private object Element(string id)
    {
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && _elements.TryGetValue(number, out var reference) && reference.Target is { } element)
        {
            return element;
        }

        throw new InvalidOperationException("The element is gone (" + id + ").");
    }

    private static string Clean(string? text) =>
        (text ?? string.Empty).Replace(Record, ' ').Replace(Field, ' ').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string Join(IEnumerable<IEnumerable<string>> records) =>
        string.Join(Record.ToString(), records.Select(r => string.Join(Field.ToString(), r.Select(Clean))));

    // ------------------------------------------------------------------ windows and trees

    /// <summary>The app's windows (its desktop lifetime's), or its main view; tests pass their own.</summary>
    private List<object> OpenWindows() => roots is null ? LifetimeWindows() : [.. roots()];

    private static List<object> LifetimeWindows()
    {
        var application = AvaloniaType("Avalonia.Application").GetProperty("Current", Static)?.GetValue(null);
        if (application is null || Property(application, "ApplicationLifetime") is not { } lifetime)
        {
            return [];
        }

        if (Property(lifetime, "Windows") is IEnumerable windows)
        {
            return windows.Cast<object>().ToList();
        }

        // A single-view app (no windows): its main view.
        return Property(lifetime, "MainView") is { } view ? [view] : [];
    }

    /// <summary>Whether <paramref name="method"/> takes parameters of these type names ("*": any type).</summary>
    private static bool Takes(MethodInfo method, params string[] types)
    {
        var parameters = method.GetParameters();
        return parameters.Length == types.Length
            && parameters.Zip(types, (p, t) => t == "*" || p.ParameterType.Name == t).All(match => match);
    }

    private string Windows() =>
        Join(OpenWindows().Select(w => new[]
        {
            IdOf(w).ToString(CultureInfo.InvariantCulture),
            Property(w, "Title") as string ?? string.Empty,
            w.GetType().FullName ?? TypeName(w),
        }));

    private string Tree(object root, bool visual)
    {
        var records = new List<string[]>();
        void Walk(object element, int parent)
        {
            var id = IdOf(element);
            records.Add(
            [
                id.ToString(CultureInfo.InvariantCulture),
                parent.ToString(CultureInfo.InvariantCulture),
                TypeName(element),
                Property(element, "Name") as string ?? string.Empty,
                Property(element, "TemplatedParent") is null ? "0" : "1",
            ]);
            foreach (var child in visual ? VisualChildren(element) : LogicalChildren(element))
            {
                Walk(child, id);
            }
        }

        Walk(root, 0);
        return Join(records);
    }

    private static MethodInfo? s_getVisualChildren;

    private static IEnumerable<object> VisualChildren(object element)
    {
        s_getVisualChildren ??= AvaloniaType("Avalonia.VisualTree.VisualExtensions").GetMethods(Static)
            .FirstOrDefault(m => m.Name == "GetVisualChildren" && !m.IsGenericMethod && m.GetParameters().Length == 1);
        return s_getVisualChildren?.Invoke(null, [element]) is IEnumerable children ? children.Cast<object>() : [];
    }

    // ------------------------------------------------------------------ properties

    /// <summary>The element's Avalonia properties: its type's, and the attached ones that have a value on it.</summary>
    private static IEnumerable<(object Property, string Name)> PropertiesOf(object element)
    {
        var registryType = AvaloniaType("Avalonia.AvaloniaPropertyRegistry");
        var registry = registryType.GetProperty("Instance", Static)!.GetValue(null)!;
        var registered = registryType.GetMethods(Instance)
            .FirstOrDefault(m => m.Name == "GetRegistered" && Takes(m, "*") && m.GetParameters()[0].ParameterType.IsInstanceOfType(element))
            ?? registryType.GetMethods(Instance).First(m => m.Name == "GetRegistered" && Takes(m, "*") && m.GetParameters()[0].ParameterType == typeof(Type));
        var own = registered.Invoke(registry, [registered.GetParameters()[0].ParameterType == typeof(Type) ? element.GetType() : element]) as IEnumerable;
        foreach (var property in own?.Cast<object>() ?? [])
        {
            yield return (property, (string)Property(property, "Name")!);
        }

        var attached = registryType.GetMethods(Instance).FirstOrDefault(m => m.Name == "GetRegisteredAttached" && m.GetParameters().Length == 1);
        if (attached?.Invoke(registry, [element.GetType()]) is IEnumerable attachedProperties)
        {
            foreach (var property in attachedProperties.Cast<object>())
            {
                if (IsSet(element, property))
                {
                    var owner = Property(property, "OwnerType") as Type;
                    yield return (property, (owner is null ? string.Empty : owner.Name + ".") + (string)Property(property, "Name")!);
                }
            }
        }
    }

    private static bool IsSet(object element, object property) =>
        element.GetType().GetMethods(Instance).FirstOrDefault(m => m.Name == "IsSet" && !m.IsGenericMethod && m.GetParameters().Length == 1)
            ?.Invoke(element, [property]) is true;

    private static object? GetValue(object element, object property) =>
        element.GetType().GetMethods(Instance)
            .First(m => m.Name == "GetValue" && !m.IsGenericMethod && Takes(m, "AvaloniaProperty"))
            .Invoke(element, [property]);

    private static MethodInfo? s_getDiagnostic;

    /// <summary>Where the value comes from: local, style, style trigger, template, inherited, animation or default.</summary>
    private static string Source(object element, object property)
    {
        s_getDiagnostic ??= AvaloniaType("Avalonia.AvaloniaObject").Assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Namespace == "Avalonia.Diagnostics")
            .SelectMany(t => t.GetMethods(Static))
            .FirstOrDefault(m => m.Name == "GetDiagnostic" && m.GetParameters().Length == 2);
        try
        {
            if (s_getDiagnostic?.Invoke(null, [element, property]) is { } diagnostic && Property(diagnostic, "Priority") is { } priority)
            {
                return priority.ToString() switch
                {
                    "LocalValue" => "Local",
                    "StyleTrigger" => "Style trigger",
                    "Template" => "Template",
                    "Style" => "Style",
                    "Inherited" => "Inherited",
                    "Animation" => "Animation",
                    "Unset" => "Default",
                    var other => other,
                };
            }
        }
        catch (TargetInvocationException)
        {
        }

        return IsSet(element, property) ? "Local" : "Default";
    }

    private static string Format(object? value) => value switch
    {
        null => "(null)",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Props(object element) =>
        Join(PropertiesOf(element)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new[]
            {
                p.Name,
                Format(SafeValue(element, p.Property)),
                ((Type)Property(p.Property, "PropertyType")!).Name,
                Source(element, p.Property),
                Property(p.Property, "IsReadOnly") is true ? "1" : "0",
            }));

    private static object? SafeValue(object element, object property)
    {
        try
        {
            return GetValue(element, property);
        }
        catch (TargetInvocationException ex)
        {
            return "(" + (ex.InnerException ?? ex).Message + ")";
        }
    }

    private static string Set(object element, string name, string text)
    {
        var (property, _) = PropertiesOf(element).FirstOrDefault(p => p.Name == name);
        if (property is null)
        {
            throw new InvalidOperationException("No property " + name + ".");
        }

        if (Property(property, "IsReadOnly") is true)
        {
            throw new InvalidOperationException(name + " is read-only.");
        }

        var value = Parse((Type)Property(property, "PropertyType")!, text);
        var setValue = element.GetType().GetMethods(Instance)
            .First(m => m.Name == "SetValue" && !m.IsGenericMethod && Takes(m, "AvaloniaProperty", "*", "BindingPriority"));
        var priority = Enum.Parse(setValue.GetParameters()[2].ParameterType, "LocalValue");
        setValue.Invoke(element, [property, value, priority]);
        return "ok";
    }

    /// <summary>A value from text as XAML reads it for <paramref name="type"/>.</summary>
    internal static object? Parse(Type type, string text)
    {
        if (text == "(null)")
        {
            return null;
        }

        if (type == typeof(string) || type == typeof(object))
        {
            return text;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return Enum.Parse(underlying, text, ignoreCase: true);
        }

        if (underlying.IsPrimitive || underlying == typeof(decimal))
        {
            return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }

        // Brushes (IBrush, the properties' usual type) and anything with a Parse(string): Thickness, Color, CornerRadius…
        var parser = underlying.IsInterface && underlying.Name is "IBrush" or "IImmutableBrush" ? AvaloniaType("Avalonia.Media.Brush") : underlying;
        var parse = parser.GetMethod("Parse", Static, null, [typeof(string)], null)
            ?? throw new InvalidOperationException("A " + underlying.Name + " can't be set from text.");
        return parse.Invoke(null, [text]);
    }

    // ------------------------------------------------------------------ the app: highlight, pick, path

    private string Highlight(object? element)
    {
        if (_adorner is not null && _adornerLayer is not null && Property(_adornerLayer, "Children") is IList children)
        {
            children.Remove(_adorner);
        }

        _adorner = null;
        _adornerLayer = null;
        if (element is null)
        {
            return "ok";
        }

        var layerType = AvaloniaType("Avalonia.Controls.Primitives.AdornerLayer");
        var layer = layerType.GetMethod("GetAdornerLayer", Static)?.Invoke(null, [element]);
        if (layer is null || Property(layer, "Children") is not IList layerChildren)
        {
            return "ok"; // a window without an adorner layer: no highlight (ADR 0052)
        }

        var brush = AvaloniaType("Avalonia.Media.Brush").GetMethod("Parse", Static, null, [typeof(string)], null)!;
        var border = Activator.CreateInstance(AvaloniaType("Avalonia.Controls.Border"))!;
        border.GetType().GetProperty("Background")!.SetValue(border, brush.Invoke(null, ["#406495ED"]));
        border.GetType().GetProperty("BorderBrush")!.SetValue(border, brush.Invoke(null, ["#FF6495ED"]));
        border.GetType().GetProperty("BorderThickness")!.SetValue(border, Activator.CreateInstance(AvaloniaType("Avalonia.Thickness"), 1d));
        border.GetType().GetProperty("IsHitTestVisible")!.SetValue(border, false);
        layerType.GetMethod("SetAdornedElement", Static)!.Invoke(null, [border, element]);
        layerChildren.Add(border);
        _adorner = border;
        _adornerLayer = layer;
        return "ok";
    }

    private string Pick(bool on)
    {
        _picking = on;
        if (on)
        {
            foreach (var window in OpenWindows())
            {
                Hook(window);
            }
        }

        return "ok";
    }

    /// <summary>Listens to the window's pointer presses (tunneling, handled ones too) for pick mode.</summary>
    private void Hook(object window)
    {
        if (!_hooked.Add(window))
        {
            return;
        }

        var pressed = AvaloniaType("Avalonia.Input.InputElement").GetField("PointerPressedEvent", Static)!.GetValue(null)!;
        var argsType = AvaloniaType("Avalonia.Input.PointerPressedEventArgs");
        var handler = Delegate.CreateDelegate(typeof(EventHandler<>).MakeGenericType(argsType), this,
            typeof(LiveAgent).GetMethod(nameof(OnPointerPressed), BindingFlags.NonPublic | BindingFlags.Instance)!);
        var addHandler = window.GetType().GetMethods(Instance)
            .First(m => m.Name == "AddHandler" && Takes(m, "RoutedEvent", "Delegate", "*", "*"));
        var routes = Enum.Parse(addHandler.GetParameters()[2].ParameterType, "Tunnel");
        addHandler.Invoke(window, [pressed, handler, routes, true]);
    }

    private void OnPointerPressed(object? sender, EventArgs e)
    {
        if (!_picking || Property(e, "Source") is not { } source)
        {
            return;
        }

        _picking = false;
        e.GetType().GetProperty("Handled")?.SetValue(e, true); // the click selects; it doesn't press the button
        var element = source;
        while (element is not null && Property(element, "TemplatedParent") is { } templatedParent)
        {
            element = templatedParent;
        }

        send("event\tpicked\t" + IdOf(source).ToString(CultureInfo.InvariantCulture) + "\t"
             + IdOf(element ?? source).ToString(CultureInfo.InvariantCulture));
    }

    private static string Path(object element)
    {
        // Out of templates first: the XAML declares the templated parent, not the template's parts.
        var current = element;
        while (Property(current, "TemplatedParent") is { } templatedParent)
        {
            current = templatedParent;
        }

        var steps = new List<string>();
        object? window = null;
        for (var step = current; step is not null; step = LogicalParent(step))
        {
            var name = Property(step, "Name") as string ?? string.Empty;
            steps.Add(TypeName(step) + ":" + Ordinal(step).ToString(CultureInfo.InvariantCulture) + ":" + name);
            window = step;
        }

        steps.Reverse();
        return (window?.GetType().FullName ?? string.Empty) + "\t" + string.Join("/", steps);
    }
}

/// <summary>Reference equality for the hooked windows (netstandard2.0 has no ReferenceEqualityComparer).</summary>
internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
