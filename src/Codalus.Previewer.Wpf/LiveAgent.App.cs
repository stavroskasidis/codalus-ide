using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Codalus.Previewer.Wpf.WpfTree;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// The desktop-app tools for agents (M60b, ADR 0099) in a WPF app, with the Avalonia agent's commands and replies:
/// what an agent reads of the running app and the input it sends, all inside the app. Nothing here moves the user's
/// pointer or activates a window: a click goes through the control's automation peer (invoke, toggle, select, expand),
/// text goes through the text box's own editing, keys are routed key events raised on the control, and a scroll moves its
/// scroll viewer.
/// <list type="bullet">
/// <item><c>controls id</c> → id, parent id, type, name, text, state (space-separated words), x, y, width, height per
/// element of the logical tree from the element, depth first, in the window's device-independent pixels</item>
/// <item><c>picture id</c> → a PNG of the window's client area (base64), its width and height in pixels, at 96 dpi</item>
/// <item><c>input id click</c>, <c>input id text base64 replace|append</c>, <c>input id keys spec</c>,
/// <c>input id scroll direction amount</c> → what was done, in words</item>
/// </list>
/// </summary>
internal sealed partial class LiveAgent
{
    private const int MaxText = 200;

    private static string Number(double value) => double.IsNaN(value) ? string.Empty : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The window's client area: its first visual child, which the bounds are relative to and the picture shows.</summary>
    private static Visual ClientOf(DependencyObject root) =>
        root is Window window && VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is Visual client ? client : (Visual)root;

    // ------------------------------------------------------------------ controls

    private string Controls(DependencyObject root)
    {
        var client = ClientOf(root);
        var records = new List<string[]>();
        void Walk(DependencyObject element, int parent, int depth)
        {
            var id = IdOf(element);
            var bounds = element is UIElement { IsVisible: true } ? SafeBounds(element, client) : null;
            records.Add(
            [
                Text(id), Text(parent), TypeName(element), Name(element), TextOf(element), string.Join(" ", StateOf(element)),
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

    private static Rect? SafeBounds(DependencyObject element, Visual client)
    {
        try
        {
            return BoundsIn(element, client);
        }
        catch (InvalidOperationException)
        {
            return null; // not under the window's client area (a popup's content)
        }
    }

    internal static string TextOf(DependencyObject element)
    {
        var text = element switch
        {
            TextBox box => box.Text,
            PasswordBox => string.Empty, // never a password
            TextBlock block => block.Text,
            Window window => window.Title,
            HeaderedContentControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            ContentControl { Content: string content } => content,
            ContentControl { Content: IFormattable formattable } => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };
        return text is null ? string.Empty : text.Length > MaxText ? text[..MaxText] + "…" : text;
    }

    internal static List<string> StateOf(DependencyObject element)
    {
        var state = new List<string>();
        if (element is UIElement visual)
        {
            if (!visual.IsEnabled)
            {
                state.Add("disabled");
            }

            if (!visual.IsVisible)
            {
                state.Add("hidden");
            }

            if (visual.IsKeyboardFocused || visual.IsFocused)
            {
                state.Add("focused");
            }
        }

        switch (element)
        {
            case ToggleButton toggle:
                state.Add(toggle.IsChecked switch { true => "checked", false => "unchecked", _ => "indeterminate" });
                break;
            case ButtonBase { IsPressed: true }:
                state.Add("pressed");
                break;
        }

        if (element is ListBoxItem { IsSelected: true } or TabItem { IsSelected: true } or TreeViewItem { IsSelected: true })
        {
            state.Add("selected");
        }

        if (element is Expander expander)
        {
            state.Add(expander.IsExpanded ? "expanded" : "collapsed");
        }
        else if (element is TreeViewItem item && item.HasItems)
        {
            state.Add(item.IsExpanded ? "expanded" : "collapsed");
        }

        if (element is TextBoxBase { IsReadOnly: true })
        {
            state.Add("read-only");
        }

        if (element is Selector { SelectedIndex: >= 0 } selector)
        {
            state.Add("selectedIndex=" + Text(selector.SelectedIndex));
        }

        if (element is RangeBase range)
        {
            state.Add("value=" + range.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (element.GetType().GetProperty("Value") is { } property && property.GetIndexParameters().Length == 0
                 && property.PropertyType is var type && (type == typeof(double) || type == typeof(int)))
        {
            state.Add("value=" + Convert.ToString(property.GetValue(element), CultureInfo.InvariantCulture));
        }

        return state;
    }

    // ------------------------------------------------------------------ picture

    private static string Picture(DependencyObject root)
    {
        if (ClientOf(root) is not FrameworkElement client)
        {
            throw new InvalidOperationException("That isn't a window.");
        }

        var width = Math.Max(1, (int)Math.Ceiling(client.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(client.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(client);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray()) + WpfTree.Field + Text(width) + WpfTree.Field + Text(height);
    }

    // ------------------------------------------------------------------ input

    private static string Input(DependencyObject element, string kind, string first, string second)
    {
        if (element is not UIElement control)
        {
            throw new InvalidOperationException("That element isn't a control.");
        }

        if (kind != "scroll" && !control.IsEnabled)
        {
            throw new InvalidOperationException(Describe(element) + " is disabled.");
        }

        if (kind != "scroll" && !control.IsVisible)
        {
            throw new InvalidOperationException(Describe(element) + " isn't visible.");
        }

        return kind switch
        {
            "click" => Click(control),
            "text" => TypeText(control, Encoding.UTF8.GetString(Convert.FromBase64String(first)), second == "replace"),
            "keys" => SendKeys(control, first),
            "scroll" => Scroll(control, first, int.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? amount : 0),
            _ => throw new InvalidOperationException("Unknown input " + kind + "."),
        };
    }

    private static string Describe(DependencyObject element) =>
        TypeName(element) + (Name(element) is { Length: > 0 } name ? " '" + name + "'" : string.Empty);

    /// <summary>A click through the automation peer of the control or its nearest ancestor with a click's pattern.</summary>
    private static string Click(UIElement element)
    {
        var depth = 0;
        for (DependencyObject? current = element; current is not null && depth++ < 32; current = VisualParent(current) ?? LogicalParent(current))
        {
            if (current is not UIElement visual)
            {
                continue;
            }

            if (!visual.IsEnabled)
            {
                throw new InvalidOperationException(Describe(current) + " is disabled.");
            }

            var peer = UIElementAutomationPeer.CreatePeerForElement(visual);
            string? done = null;
            if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            {
                invoke.Invoke();
                done = "invoked";
            }
            else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            {
                toggle.Toggle();
                done = "toggled";
            }
            else if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection)
            {
                selection.Select();
                done = "selected";
            }
            else if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
            {
                var expanding = expand.ExpandCollapseState is not (System.Windows.Automation.ExpandCollapseState.Expanded or System.Windows.Automation.ExpandCollapseState.PartiallyExpanded);
                if (expanding)
                {
                    expand.Expand();
                }
                else
                {
                    expand.Collapse();
                }

                done = expanding ? "expanded" : "collapsed";
            }

            if (done is not null)
            {
                return "Clicked " + (ReferenceEquals(current, element) ? Describe(element) : Describe(current) + " (around " + Describe(element) + ")") + ": " + done + ".";
            }

            if (current is Window)
            {
                break;
            }
        }

        throw new InvalidOperationException(Describe(element) + " has nothing to click: neither it nor a control around it can be invoked, toggled, selected or expanded. Send it keys, or click a button, a check box or a list item.");
    }

    /// <summary>Text into the text box (the control, or the first inside it), at the end of its text or in place of it.</summary>
    private static string TypeText(UIElement element, string text, bool replace)
    {
        var box = element as TextBox ?? Descendants(element).OfType<TextBox>().FirstOrDefault();
        if (box is null)
        {
            throw new InvalidOperationException(Describe(element) + " has no text box to type into.");
        }

        if (box.IsReadOnly)
        {
            throw new InvalidOperationException(Describe(box) + " is read-only.");
        }

        if (replace)
        {
            box.Text = text;
        }
        else
        {
            box.AppendText(text);
        }

        box.CaretIndex = box.Text.Length;
        var after = box.Text;
        return "Typed " + Text(text.Length) + " characters into " + Describe(box) + ". Its text is now: " + (after.Length > MaxText ? after[..MaxText] + "…" : after);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        var queue = new Queue<DependencyObject>();
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
    /// Keys pressed on the control (preview and bubbling key down, then key up). WPF reads the modifier keys from the
    /// keyboard itself, so a chord with Ctrl, Shift or Alt is refused rather than sent without them.
    /// </summary>
    private static string SendKeys(UIElement element, string spec)
    {
        var source = PresentationSource.FromVisual(element) ?? throw new InvalidOperationException(Describe(element) + " isn't shown in a window.");
        var sent = new List<string>();
        var handled = false;
        foreach (var chord in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (chord.Contains('+', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("WPF reads Ctrl, Shift and Alt from the keyboard itself, so " + chord + " can't be sent without the user's keyboard: send the key alone, or click or type instead.");
            }

            var key = KeyOf(chord);
            foreach (var (preview, routed) in new[] { (Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent), (Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent) })
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = preview };
                element.RaiseEvent(args);
                if (!args.Handled)
                {
                    args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routed };
                    element.RaiseEvent(args);
                }

                handled |= args.Handled && ReferenceEquals(routed, Keyboard.KeyDownEvent);
            }

            sent.Add(chord);
        }

        if (sent.Count == 0)
        {
            throw new InvalidOperationException("No keys: give them as Enter, Tab or Down, separated by spaces.");
        }

        return "Pressed " + string.Join(", ", sent) + " on " + Describe(element) + (handled ? " (the app handled them)." : " (nothing in the app handled them).");
    }

    private static Key KeyOf(string name)
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
            _ => name.Length == 1 && char.IsAsciiDigit(name[0]) ? "D" + name : name,
        };
        return Enum.TryParse<Key>(alias, ignoreCase: true, out var key)
            ? key
            : throw new InvalidOperationException("Unknown key '" + name + "': use WPF's key names (Enter, Escape, Tab, Back, Delete, Up, Down, Left, Right, Home, End, PageUp, PageDown, Space, F1–F12, A–Z, 0–9).");
    }

    private static string Scroll(UIElement element, string direction, int amount)
    {
        if (direction == "intoView")
        {
            (element as FrameworkElement)?.BringIntoView();
            return "Brought " + Describe(element) + " into view.";
        }

        var viewer = element as ScrollViewer
            ?? Descendants(element).OfType<ScrollViewer>().FirstOrDefault()
            ?? Ancestors(element).OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidOperationException(Describe(element) + " doesn't scroll: it has no scroll viewer, inside or around it.");
        Action step = direction switch
        {
            "up" => viewer.LineUp,
            "down" => viewer.LineDown,
            "left" => viewer.LineLeft,
            "right" => viewer.LineRight,
            "pageUp" => viewer.PageUp,
            "pageDown" => viewer.PageDown,
            "top" => viewer.ScrollToTop,
            "bottom" => viewer.ScrollToBottom,
            _ => throw new InvalidOperationException("Unknown direction '" + direction + "' (up, down, left, right, pageUp, pageDown, top, bottom, intoView)."),
        };
        var times = direction is "top" or "bottom" ? 1 : amount > 0 ? amount : direction.StartsWith("page", StringComparison.Ordinal) ? 1 : 3;
        for (var i = 0; i < Math.Min(times, 1000); i++)
        {
            step();
        }

        viewer.UpdateLayout(); // the offset moves on the layout pass
        return "Scrolled " + Describe(viewer) + " " + direction + ": its offset is now " + Number(viewer.HorizontalOffset) + "," + Number(viewer.VerticalOffset)
            + " of " + Number(viewer.ExtentWidth) + "x" + Number(viewer.ExtentHeight) + ".";
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var current = VisualParent(element); current is not null; current = VisualParent(current))
        {
            yield return current;
        }
    }
}
