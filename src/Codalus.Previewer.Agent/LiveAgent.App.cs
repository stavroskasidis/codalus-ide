using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using static Codalus.Previewer.Agent.PreviewAgent;

namespace Codalus.Previewer.Agent;

/// <summary>
/// The desktop-app tools for agents (M60b, ADR 0099): what an agent reads of the running app and the input it sends,
/// all inside the app. Nothing here moves the user's pointer or activates a window: a click goes through the control's
/// automation peer (invoke, toggle, select, expand), text and keys are routed events raised on the control, and a
/// scroll moves its scroll viewer.
/// <list type="bullet">
/// <item><c>controls id</c> → id, parent id, type, name, text, state (space-separated words), x, y, width, height per
/// element of the logical tree from the element, depth first; the bounds are in the window's device-independent pixels,
/// empty for an element that isn't laid out</item>
/// <item><c>picture id</c> → a PNG of the window (base64), its width and height in pixels, at 96 dpi</item>
/// <item><c>input id click</c>, <c>input id text base64 replace|append</c>, <c>input id keys spec</c>,
/// <c>input id scroll direction amount</c> → what was done, in words</item>
/// </list>
/// TODO(issue): a window's popups (menus, a combo box's drop-down: top levels of their own, not listed), and pointer
/// gestures other than a click (drag, hover, a right click).
/// </summary>
internal sealed partial class LiveAgent
{
    private const int MaxText = 200;
    private static readonly char[] s_space = [' '];

    /// <summary>The window's size (device-independent pixels): width and height.</summary>
    private static string[] SizeOf(object window) =>
        Get(window, "Bounds") is { } bounds
            ? [Number((double)Get(bounds, "Width")!), Number((double)Get(bounds, "Height")!)]
            : [string.Empty, string.Empty];

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A property by name, the most derived one when a type hides its base's (<c>new</c>), or null.</summary>
    internal static object? Get(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch (TargetInvocationException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool Is(object element, string typeName)
    {
        for (var type = element.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == typeName)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ controls

    private string Controls(object root)
    {
        var records = new List<string[]>();
        void Walk(object element, int parent, int depth)
        {
            var id = IdOf(element);
            var bounds = Is(element, "Avalonia.Visual") && Get(element, "IsEffectivelyVisible") is true ? SafeBounds(element, root) : null;
            records.Add(
            [
                id.ToString(CultureInfo.InvariantCulture),
                parent.ToString(CultureInfo.InvariantCulture),
                TypeName(element),
                Get(element, "Name") as string ?? string.Empty,
                TextOf(element),
                string.Join(" ", StateOf(element)),
                bounds is { } b ? Number(b.X) : string.Empty,
                bounds is { } b1 ? Number(b1.Y) : string.Empty,
                bounds is { } b2 ? Number(b2.Width) : string.Empty,
                bounds is { } b3 ? Number(b3.Height) : string.Empty,
            ]);
            if (depth < 64)
            {
                foreach (var child in LogicalChildren(element))
                {
                    Walk(child, id, depth + 1);
                }
            }
        }

        Walk(root, 0, 0);
        return Join(records);
    }

    private static (double X, double Y, double Width, double Height)? SafeBounds(object element, object root)
    {
        try
        {
            return ReferenceEquals(element, root) ? (0, 0, (double)Get(Get(root, "Bounds")!, "Width")!, (double)Get(Get(root, "Bounds")!, "Height")!) : BoundsOf(element, root);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NullReferenceException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>What the control shows as text: its text, its string content or header, or its title.</summary>
    internal static string TextOf(object element)
    {
        foreach (var name in new[] { "Text", "Content", "Header", "Title" })
        {
            var value = Get(element, name);
            var text = value switch
            {
                string s => s,
                IFormattable formattable when name is "Content" or "Header" => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => null,
            };
            if (text is { Length: > 0 })
            {
                return text.Length > MaxText ? text.Substring(0, MaxText) + "…" : text;
            }
        }

        return string.Empty;
    }

    /// <summary>The control's state, as words: disabled, hidden, focused, checked, selected, read-only, value=…</summary>
    internal static List<string> StateOf(object element)
    {
        var state = new List<string>();
        if (Get(element, "IsEffectivelyEnabled") is false)
        {
            state.Add("disabled");
        }

        if (Get(element, "IsEffectivelyVisible") is false)
        {
            state.Add("hidden");
        }

        if (Get(element, "IsFocused") is true)
        {
            state.Add("focused");
        }

        if (Get(element, "IsPressed") is true)
        {
            state.Add("pressed");
        }

        if (element.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == "IsChecked") is { } isChecked
            && (isChecked.PropertyType == typeof(bool) || Nullable.GetUnderlyingType(isChecked.PropertyType) == typeof(bool)))
        {
            state.Add(Get(element, "IsChecked") switch
            {
                true => "checked",
                false => "unchecked",
                _ => "indeterminate",
            });
        }

        if (Get(element, "IsSelected") is true)
        {
            state.Add("selected");
        }

        if (Get(element, "IsExpanded") is bool expanded)
        {
            state.Add(expanded ? "expanded" : "collapsed");
        }

        if (Get(element, "IsReadOnly") is true)
        {
            state.Add("read-only");
        }

        if (Get(element, "SelectedIndex") is int index && index >= 0)
        {
            state.Add("selectedIndex=" + index.ToString(CultureInfo.InvariantCulture));
        }

        if (Get(element, "Value") is { } value && (value is double or float or int or long or decimal))
        {
            state.Add("value=" + ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
        }

        return state;
    }

    // ------------------------------------------------------------------ picture

    /// <summary>A PNG of the window at 96 dpi (its device-independent size), as base64, with its width and height.</summary>
    private static string Picture(object window)
    {
        var bounds = Get(window, "Bounds") ?? throw new InvalidOperationException("That isn't a window.");
        var width = Math.Max(1, (int)Math.Ceiling((double)Get(bounds, "Width")!));
        var height = Math.Max(1, (int)Math.Ceiling((double)Get(bounds, "Height")!));
        var size = Activator.CreateInstance(AvaloniaType("Avalonia.PixelSize"), width, height);
        var dpi = Activator.CreateInstance(AvaloniaType("Avalonia.Vector"), 96d, 96d);
        var bitmapType = AvaloniaType("Avalonia.Media.Imaging.RenderTargetBitmap");
        var bitmap = Activator.CreateInstance(bitmapType, size, dpi)!;
        try
        {
            bitmapType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Render" && m.GetParameters().Length == 1)
                .Invoke(bitmap, [window]);
            using var stream = new MemoryStream();
            var save = bitmapType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Save" && m.GetParameters() is { Length: >= 1 } p && p[0].ParameterType == typeof(Stream))
                .OrderBy(m => m.GetParameters().Length)
                .First();
            save.Invoke(bitmap, save.GetParameters().Select((p, i) => i == 0 ? stream : DefaultOf(p)).ToArray());
            return Convert.ToBase64String(stream.ToArray()) + Field + width.ToString(CultureInfo.InvariantCulture) + Field + height.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            (bitmap as IDisposable)?.Dispose();
        }
    }

    /// <summary>An argument for a parameter left at its default: its declared default, the type's <c>Default</c>, or zero.</summary>
    private static object? DefaultOf(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        var type = parameter.ParameterType;
        if (type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static) is { } property && property.PropertyType == type)
        {
            return property.GetValue(null);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    // ------------------------------------------------------------------ input

    private string Input(object element, string kind, string first, string second)
    {
        if (!Is(element, "Avalonia.Controls.Control"))
        {
            throw new InvalidOperationException("That element isn't a control.");
        }

        if (kind != "scroll" && Get(element, "IsEffectivelyEnabled") is false)
        {
            throw new InvalidOperationException(Describe(element) + " is disabled.");
        }

        if (kind != "scroll" && Get(element, "IsEffectivelyVisible") is false)
        {
            throw new InvalidOperationException(Describe(element) + " isn't visible.");
        }

        return kind switch
        {
            "click" => Click(element),
            "text" => TypeText(element, Encoding.UTF8.GetString(Convert.FromBase64String(first)), second == "replace"),
            "keys" => SendKeys(element, first),
            "scroll" => Scroll(element, first, int.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? amount : 0),
            _ => throw new InvalidOperationException("Unknown input " + kind + "."),
        };
    }

    private static string Describe(object element) =>
        TypeName(element) + (Get(element, "Name") is string { Length: > 0 } name ? " '" + name + "'" : string.Empty);

    /// <summary>
    /// A click through the automation peer of the control or its nearest ancestor that has one of the patterns a click
    /// does: invoke (a button, a menu item), toggle (a check box, a toggle button), select (a list item, a tab), or expand.
    /// </summary>
    private static string Click(object element)
    {
        var peers = AvaloniaType("Avalonia.Automation.Peers.ControlAutomationPeer");
        var create = peers.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "CreatePeerForElement" && m.GetParameters().Length == 1);
        var depth = 0;
        for (var current = element; current is not null && depth++ < 32; current = VisualParent(current))
        {
            if (!Is(current, "Avalonia.Controls.Control"))
            {
                continue;
            }

            if (Get(current, "IsEffectivelyEnabled") is false)
            {
                throw new InvalidOperationException(Describe(current) + " is disabled.");
            }

            var peer = create.Invoke(null, [current]);
            if (peer is not null)
            {
                var done = Pattern(peer, "IInvokeProvider", "Invoke") ? "invoked"
                    : Pattern(peer, "IToggleProvider", "Toggle") ? "toggled"
                    : Pattern(peer, "ISelectionItemProvider", "Select") ? "selected"
                    : ExpandOrCollapse(peer);
                if (done is not null)
                {
                    var target = ReferenceEquals(current, element) ? Describe(element) : Describe(current) + " (around " + Describe(element) + ")";
                    return "Clicked " + target + ": " + done + ".";
                }
            }

            if (Is(current, "Avalonia.Controls.TopLevel"))
            {
                break;
            }
        }

        throw new InvalidOperationException(Describe(element) + " has nothing to click: neither it nor a control around it can be invoked, toggled, selected or expanded. Send it keys, or click a button, a check box or a list item.");
    }

    /// <summary>Calls the pattern's method when the peer provides it (its interface, or <c>GetProvider&lt;T&gt;()</c>).</summary>
    private static bool Pattern(object peer, string interfaceName, string method)
    {
        var provider = Provider(peer, interfaceName);
        if (provider.Interface is null)
        {
            return false;
        }

        provider.Interface.GetMethod(method)!.Invoke(provider.Target, null);
        return true;
    }

    private static (Type? Interface, object? Target) Provider(object peer, string interfaceName)
    {
        var fullName = "Avalonia.Automation.Provider." + interfaceName;
        if (peer.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == fullName) is { } own)
        {
            return (own, peer);
        }

        var type = AvaloniaType(fullName);
        var get = peer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetProvider" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return get?.MakeGenericMethod(type).Invoke(peer, null) is { } provided ? (type, provided) : (null, null);
    }

    private static string? ExpandOrCollapse(object peer)
    {
        var (expandCollapse, target) = Provider(peer, "IExpandCollapseProvider");
        if (expandCollapse is null)
        {
            return null;
        }

        var state = expandCollapse.GetProperty("ExpandCollapseState")?.GetValue(target)?.ToString();
        var expand = state is not ("Expanded" or "PartiallyExpanded");
        expandCollapse.GetMethod(expand ? "Expand" : "Collapse")!.Invoke(target, null);
        return expand ? "expanded" : "collapsed";
    }

    /// <summary>
    /// Text typed into the text box (the control, or the first one inside it) as text input events, at the end of its
    /// text or in place of it, as the keyboard would; a control without a text box gets the events itself.
    /// </summary>
    private static string TypeText(object element, string text, bool replace)
    {
        var box = Is(element, "Avalonia.Controls.TextBox") ? element : VisualDescendants(element).FirstOrDefault(d => Is(d, "Avalonia.Controls.TextBox"));
        var target = box ?? element;
        if (box is not null)
        {
            if (Get(box, "IsReadOnly") is true)
            {
                throw new InvalidOperationException(Describe(box) + " is read-only.");
            }

            if (replace)
            {
                Set(box, "Text", string.Empty);
            }

            var length = (Get(box, "Text") as string ?? string.Empty).Length;
            Set(box, "SelectionStart", length);
            Set(box, "SelectionEnd", length);
            Set(box, "CaretIndex", length);
        }

        var before = box is null ? null : Get(box, "Text") as string ?? string.Empty;
        var input = AvaloniaType("Avalonia.Input.TextInputEventArgs");
        var args = Activator.CreateInstance(input)!;
        input.GetProperty("RoutedEvent")!.SetValue(args, AvaloniaType("Avalonia.Input.InputElement").GetField("TextInputEvent", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        input.GetProperty("Text")!.SetValue(args, text);
        Raise(target, args);
        if (box is null)
        {
            return "Sent the text to " + Describe(element) + (Get(args, "Handled") is true ? ", which took it." : ", which didn't take it (it has no text box).");
        }

        // A text box that didn't take the events (no presenter yet, a filter): its text, as typing would have left it.
        var after = Get(box, "Text") as string ?? string.Empty;
        if (after == before && text.Length > 0)
        {
            Set(box, "Text", before + text);
            after = before + text;
        }

        return "Typed " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters into " + Describe(box) + ". Its text is now: " + (after.Length > MaxText ? after.Substring(0, MaxText) + "…" : after);
    }

    private static void Set(object target, string name, object? value)
    {
        if (target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } property)
        {
            property.SetValue(target, value);
        }
    }

    private static void Raise(object target, object args) =>
        target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "RaiseEvent" && m.GetParameters().Length == 1)
            .Invoke(target, [args]);

    private static IEnumerable<object> VisualDescendants(object element)
    {
        var queue = new Queue<object>();
        queue.Enqueue(element);
        while (queue.Count > 0)
        {
            foreach (var child in VisualChildren(queue.Dequeue()))
            {
                yield return child;
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Keys pressed on the control, each a key down and a key up: chords separated by spaces, a chord's modifiers by
    /// <c>+</c> (<c>Ctrl+A Enter</c>, <c>Shift+Tab</c>). Keys are Avalonia's <c>Key</c> names (Enter, Escape, Tab, Down,
    /// A, D1, F5…).
    /// </summary>
    private static string SendKeys(object element, string spec)
    {
        var keyType = AvaloniaType("Avalonia.Input.Key");
        var modifiersType = AvaloniaType("Avalonia.Input.KeyModifiers");
        var argsType = AvaloniaType("Avalonia.Input.KeyEventArgs");
        var inputElement = AvaloniaType("Avalonia.Input.InputElement");
        var down = inputElement.GetField("KeyDownEvent", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var up = inputElement.GetField("KeyUpEvent", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var sent = new List<string>();
        var handled = false;
        foreach (var chord in spec.Split(s_space, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chord.Split('+');
            var modifiers = 0;
            foreach (var modifier in parts.Take(parts.Length - 1))
            {
                modifiers |= modifier.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => Flag(modifiersType, "Control"),
                    "shift" => Flag(modifiersType, "Shift"),
                    "alt" or "option" => Flag(modifiersType, "Alt"),
                    "meta" or "cmd" or "command" or "win" or "super" => Flag(modifiersType, "Meta"),
                    _ => throw new InvalidOperationException("Unknown modifier '" + modifier + "' in " + chord + " (Ctrl, Shift, Alt, Meta)."),
                };
            }

            var key = KeyOf(keyType, parts[parts.Length - 1]);
            foreach (var routed in new[] { down, up })
            {
                var args = Activator.CreateInstance(argsType)!;
                argsType.GetProperty("RoutedEvent")!.SetValue(args, routed);
                argsType.GetProperty("Key")!.SetValue(args, key);
                argsType.GetProperty("KeyModifiers")!.SetValue(args, Enum.ToObject(modifiersType, modifiers));
                Raise(element, args);
                handled |= ReferenceEquals(routed, down) && Get(args, "Handled") is true;
            }

            sent.Add(chord);
        }

        if (sent.Count == 0)
        {
            throw new InvalidOperationException("No keys: give them as Enter, Ctrl+A or Shift+Tab, separated by spaces.");
        }

        return "Pressed " + string.Join(", ", sent) + " on " + Describe(element) + (handled ? " (the app handled them)." : " (nothing in the app handled them).");
    }

    private static int Flag(Type type, string name) => Convert.ToInt32(Enum.Parse(type, name), CultureInfo.InvariantCulture);

    private static object KeyOf(Type keyType, string name)
    {
        var alias = name.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "del" => "Delete",
            "ins" => "Insert",
            "pgup" => "PageUp",
            "pgdn" or "pgdown" => "PageDown",
            "bksp" or "backspace" => "Back",
            "spacebar" => "Space",
            "return" => "Enter",
            _ => name.Length == 1 && char.IsDigit(name[0]) ? "D" + name : name,
        };
        try
        {
            return Enum.Parse(keyType, alias, ignoreCase: true);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Unknown key '" + name + "': use Avalonia's key names (Enter, Escape, Tab, Back, Delete, Up, Down, Left, Right, Home, End, PageUp, PageDown, Space, F1–F12, A–Z, 0–9).");
        }
    }

    /// <summary>
    /// The control's scroll viewer (itself, the first one inside it, or the nearest around it) moved by lines or pages,
    /// to its start or end; <c>intoView</c> brings the control itself into view.
    /// </summary>
    private static string Scroll(object element, string direction, int amount)
    {
        if (direction == "intoView")
        {
            element.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).First(m => m.Name == "BringIntoView" && m.GetParameters().Length == 0).Invoke(element, null);
            return "Brought " + Describe(element) + " into view.";
        }

        var viewer = Is(element, "Avalonia.Controls.ScrollViewer") ? element
            : VisualDescendants(element).FirstOrDefault(d => Is(d, "Avalonia.Controls.ScrollViewer"))
            ?? Ancestors(element).FirstOrDefault(a => Is(a, "Avalonia.Controls.ScrollViewer"))
            ?? throw new InvalidOperationException(Describe(element) + " doesn't scroll: it has no scroll viewer, inside or around it.");
        var (method, times) = direction switch
        {
            "up" => ("LineUp", amount > 0 ? amount : 3),
            "down" => ("LineDown", amount > 0 ? amount : 3),
            "left" => ("LineLeft", amount > 0 ? amount : 3),
            "right" => ("LineRight", amount > 0 ? amount : 3),
            "pageUp" => ("PageUp", amount > 0 ? amount : 1),
            "pageDown" => ("PageDown", amount > 0 ? amount : 1),
            "top" => ("ScrollToHome", 1),
            "bottom" => ("ScrollToEnd", 1),
            _ => throw new InvalidOperationException("Unknown direction '" + direction + "' (up, down, left, right, pageUp, pageDown, top, bottom, intoView)."),
        };
        var call = viewer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).First(m => m.Name == method && m.GetParameters().Length == 0);
        for (var i = 0; i < Math.Min(times, 1000); i++)
        {
            call.Invoke(viewer, null);
        }

        var offset = Get(viewer, "Offset");
        var extent = Get(viewer, "Extent");
        return "Scrolled " + Describe(viewer) + " " + direction + (offset is null ? "." : ": its offset is now " + Point(offset) + (extent is null ? "" : " of " + Size(extent)) + ".");
    }

    private static IEnumerable<object> Ancestors(object element)
    {
        for (var current = VisualParent(element); current is not null; current = VisualParent(current))
        {
            yield return current;
        }
    }

    private static string Point(object vector) => Number((double)Get(vector, "X")!) + "," + Number((double)Get(vector, "Y")!);

    private static string Size(object size) => Number((double)Get(size, "Width")!) + "x" + Number((double)Get(size, "Height")!);
}
