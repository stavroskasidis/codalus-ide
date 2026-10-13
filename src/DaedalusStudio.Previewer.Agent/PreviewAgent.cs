using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace DaedalusStudio.Previewer.Agent;

/// <summary>
/// The agent's commands, run on Avalonia's UI thread through reflection:
/// <list type="bullet">
/// <item><c>ping</c> → <c>pong</c></item>
/// <item><c>hit x y</c> (the window's device-independent pixels) → <c>x,y,w,h</c> of the element hit, a tab, and its
/// logical path from the previewed window: <c>Type:ordinal:Name</c> steps joined by <c>/</c>, where the ordinal counts
/// the logical siblings of the same type; empty when nothing is hit</item>
/// <item><c>bounds path</c> (<c>Type:ordinal</c> steps from the XAML's root) → <c>x,y,w,h</c>, or <c>none</c></item>
/// <item><c>theme Default|Light|Dark</c> → <c>ok</c> (the application's requested theme variant)</item>
/// </list>
/// </summary>
internal sealed class PreviewAgent
{
    private static readonly TimeSpan s_uiTimeout = TimeSpan.FromSeconds(10);
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

    public string Handle(string[] request)
    {
        var command = request[1];
        return command switch
        {
            "ping" => "pong",
            "hit" => OnUiThread(() => Hit(Double(request, 2), Double(request, 3))),
            "bounds" => OnUiThread(() => Bounds(request.Length > 2 ? request[2] : string.Empty)),
            "theme" => OnUiThread(() => Theme(request.Length > 2 ? request[2] : "Default")),
            _ => throw new InvalidOperationException("Unknown command " + command),
        };
    }

    private static double Double(string[] request, int index) =>
        request.Length > index ? double.Parse(request[index], NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

    // ------------------------------------------------------------------ commands

    private static string Hit(double x, double y)
    {
        var window = CurrentWindow() ?? throw new InvalidOperationException("Nothing is previewed.");
        var point = Activator.CreateInstance(AvaloniaType("Avalonia.Point"), x, y);
        var getVisualAt = AvaloniaType("Avalonia.VisualTree.VisualExtensions").GetMethods(Static)
            .First(m => m.Name == "GetVisualAt" && m.GetParameters().Length == 2);
        var hit = getVisualAt.Invoke(null, [window, point]);

        // From the visual hit to the element the XAML declares: out of control templates, up the visual tree to one
        // in the window's logical tree.
        var element = hit;
        while (element is not null && !IsXamlElement(element, window))
        {
            element = Property(element, "TemplatedParent") ?? VisualParent(element);
        }

        if (element is null)
        {
            return string.Empty;
        }

        var steps = new List<string>();
        for (var current = element; current is not null; current = current == window ? null : LogicalParent(current))
        {
            var name = Property(current, "Name") as string ?? string.Empty;
            steps.Add(TypeName(current) + ":" + Ordinal(current).ToString(CultureInfo.InvariantCulture) + ":" + name);
        }

        steps.Reverse();
        return FormatBounds(BoundsOf(element, window)) + "\t" + string.Join("/", steps);
    }

    private static string Bounds(string path)
    {
        var window = CurrentWindow() ?? throw new InvalidOperationException("Nothing is previewed.");
        var steps = path.Split(['/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(':'))
            .Select(p => (Type: p[0], Ordinal: p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0))
            .ToList();
        if (steps.Count == 0)
        {
            return "none";
        }

        // The XAML's root: the previewed window itself, else the window's content (a UserControl's preview), else
        // the first element of the root's type (Design.PreviewWith).
        var root = TypeName(window) == steps[0].Type ? window
            : Property(window, "Content") is { } content && TypeName(content) == steps[0].Type ? content
            : Descendants(window).FirstOrDefault(d => TypeName(d) == steps[0].Type);
        var element = root;
        foreach (var step in steps.Skip(1))
        {
            if (element is null)
            {
                break;
            }

            element = LogicalChildren(element).Where(c => TypeName(c) == step.Type).Skip(step.Ordinal).FirstOrDefault();
        }

        return element is null ? "none" : FormatBounds(BoundsOf(element, window));
    }

    private static string Theme(string variant)
    {
        var application = AvaloniaType("Avalonia.Application").GetProperty("Current", Static)?.GetValue(null)
            ?? throw new InvalidOperationException("No application.");
        var themeType = AvaloniaType("Avalonia.Styling.ThemeVariant");
        var value = themeType.GetProperty(variant, Static)?.GetValue(null) ?? themeType.GetField(variant, Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Unknown theme variant " + variant);
        application.GetType().GetProperty("RequestedThemeVariant", Instance)!.SetValue(application, value);
        return "ok";
    }

    // ------------------------------------------------------------------ the trees

    private static object? CurrentWindow() =>
        AvaloniaType("Avalonia.DesignerSupport.Remote.RemoteDesignerEntryPoint")
            .GetField("s_currentWindow", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    /// <summary>An element in the window's logical tree that no template created.</summary>
    private static bool IsXamlElement(object element, object window)
    {
        if (Property(element, "TemplatedParent") is not null)
        {
            return false;
        }

        for (var current = element; current is not null; current = LogicalParent(current))
        {
            if (current == window)
            {
                return true;
            }
        }

        return false;
    }

    internal static object? VisualParent(object visual)
    {
        var extensions = AvaloniaType("Avalonia.VisualTree.VisualExtensions");
        var method = extensions.GetMethods(Static).FirstOrDefault(m => m.Name == "GetVisualParent" && !m.IsGenericMethod && m.GetParameters().Length == 1);
        return method is not null ? method.Invoke(null, [visual]) : Property(visual, "VisualParent");
    }

    internal static object? LogicalParent(object element) =>
        Logical(element)?.GetProperty("LogicalParent")?.GetValue(element);

    internal static IEnumerable<object> LogicalChildren(object element) =>
        Logical(element)?.GetProperty("LogicalChildren")?.GetValue(element) is IEnumerable children ? children.Cast<object>() : [];

    private static IEnumerable<object> Descendants(object element)
    {
        foreach (var child in LogicalChildren(element))
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    internal static Type? Logical(object element) =>
        element.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == "Avalonia.LogicalTree.ILogical");

    /// <summary>The element's index among its logical siblings of the same type (0 for the root).</summary>
    internal static int Ordinal(object element)
    {
        if (LogicalParent(element) is not { } parent)
        {
            return 0;
        }

        var type = TypeName(element);
        var index = 0;
        foreach (var sibling in LogicalChildren(parent))
        {
            if (sibling == element)
            {
                return index;
            }

            if (TypeName(sibling) == type)
            {
                index++;
            }
        }

        return 0;
    }

    internal static string TypeName(object element)
    {
        var name = element.GetType().Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }

    /// <summary>The element's bounds in the window's coordinates (device-independent pixels).</summary>
    private static (double X, double Y, double Width, double Height) BoundsOf(object element, object window)
    {
        var bounds = Property(element, "Bounds")!;
        var width = (double)Property(bounds, "Width")!;
        var height = (double)Property(bounds, "Height")!;
        var pointType = AvaloniaType("Avalonia.Point");
        var origin = Activator.CreateInstance(pointType, 0d, 0d);
        s_translatePoint ??= pointType.Assembly.GetTypes()
            .SelectMany(t => t.IsAbstract && t.IsSealed ? t.GetMethods(Static) : [])
            .FirstOrDefault(m => m.Name == "TranslatePoint" && m.GetParameters().Length == 3);
        var translated = s_translatePoint?.Invoke(null, [element, origin, window]);
        return translated is null
            ? (0, 0, width, height)
            : ((double)Property(translated, "X")!, (double)Property(translated, "Y")!, width, height);
    }

    private static string FormatBounds((double X, double Y, double Width, double Height) b) =>
        string.Join(",", new[] { b.X, b.Y, b.Width, b.Height }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ reflection

    internal static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, Instance)?.GetValue(target);

    private static readonly Dictionary<string, Type> s_types = [];
    private static MethodInfo? s_translatePoint;

    internal static Type AvaloniaType(string fullName)
    {
        lock (s_types)
        {
            if (s_types.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                .Select(a => a.GetType(fullName, throwOnError: false))
                .FirstOrDefault(t => t is not null)
                ?? throw new InvalidOperationException(fullName + " not found (is this an Avalonia app?)");
            s_types[fullName] = type;
            return type;
        }
    }

    /// <summary>Runs <paramref name="work"/> on Avalonia's UI thread and waits for it (at most 10 s).</summary>
    internal static string OnUiThread(Func<string> work)
    {
        var dispatcherType = AvaloniaType("Avalonia.Threading.Dispatcher");
        var dispatcher = dispatcherType.GetProperty("UIThread", Static)!.GetValue(null)!;
        string? result = null;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Action action = () =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            }
            finally
            {
                done.Set();
            }
        };

        var post = dispatcherType.GetMethods(Instance)
            .Where(m => m.Name == "Post" && m.GetParameters() is { Length: >= 1 } p && p[0].ParameterType == typeof(Action))
            .OrderBy(m => m.GetParameters().Length)
            .First();
        var arguments = post.GetParameters().Select((p, i) => i == 0 ? action : Normal(p.ParameterType)).ToArray();
        post.Invoke(dispatcher, arguments);
        if (!done.Wait(s_uiTimeout))
        {
            throw new TimeoutException("The previewer's UI thread didn't respond.");
        }

        return error is null ? result ?? string.Empty : throw new InvalidOperationException(error.Message, error);
    }

    internal static object? Normal(Type priorityType) =>
        priorityType.GetField("Normal", Static)?.GetValue(null)
        ?? priorityType.GetProperty("Normal", Static)?.GetValue(null)
        ?? (priorityType.IsValueType ? Activator.CreateInstance(priorityType) : null);

}
