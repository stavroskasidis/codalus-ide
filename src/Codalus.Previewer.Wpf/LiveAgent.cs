using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static Codalus.Previewer.Wpf.WpfTree;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// The live agent for WPF apps (M40b, ADR 0072): the Live Visual Tree and Live Property Explorer's view of a running
/// WPF app, with the commands and replies of the Avalonia agent (ADR 0052), so the IDE's <c>LiveSession</c> and tool
/// windows serve both. Lists are records separated by <see cref="WpfTree.Record"/>, fields by
/// <see cref="WpfTree.Field"/>; element ids stay the same while the elements live. Every command runs on the app's UI
/// thread at <see cref="DispatcherPriority.Loaded"/>, after the data binding and layout the app has queued (a binding
/// attaches at <c>DataBind</c> priority, after the constructor that set it), so a window that has just opened is read
/// as it's drawn: with its bound <c>Title</c> and its laid-out items:
/// <list type="bullet">
/// <item><c>windows</c> → id, title, type (the window's class) per open window</item>
/// <item><c>tree id logical|visual</c> → id, parent id, type, name, templated (1 when a template made it) per element,
/// depth first (<c>LogicalTreeHelper</c> or <c>VisualTreeHelper</c>)</item>
/// <item><c>props id</c> → name, value, type, source (<c>DependencyPropertyHelper.GetValueSource</c>), read-only per
/// dependency property: the element type's, and the attached ones set on it</item>
/// <item><c>set id property value</c> → <c>ok</c>: the value converted as XAML would (the property's type converter),
/// set as a local value</item>
/// <item><c>highlight id|none</c> → <c>ok</c>: an adorner over the element</item>
/// <item><c>pick on|off</c> → <c>ok</c>: the next click in the app selects its element (<c>event picked id logicalId</c>)
/// instead of reaching it</item>
/// <item><c>path id</c> → the window's class and the element's logical path from it</item>
/// </list>
/// </summary>
internal sealed class LiveAgent(Action<string> send)
{
    private readonly ConditionalWeakTable<DependencyObject, object> _ids = new();
    private readonly Dictionary<int, WeakReference<DependencyObject>> _elements = [];
    private int _next;
    private HighlightAdorner? _adorner;
    private bool _picking;
    private bool _hooked;

    public string Handle(string[] request)
    {
        var command = request[1];
        string Arg(int index) => request.Length > index ? request[index] : string.Empty;
        if (command == "ping")
        {
            return "pong";
        }

        // Application.Current is set once the app's own Application exists (its constructor, on its UI thread);
        // until then the IDE asks again. Nothing here touches the app's dispatcher before that.
        var application = Application.Current ?? throw new InvalidOperationException("The app hasn't started WPF yet.");
        return AgentLink.OnUiThread(application.Dispatcher, () => command switch
        {
            "windows" => Windows(application),
            "tree" => Tree(Element(Arg(2)), Arg(3) == "visual"),
            "props" => Props(Element(Arg(2))),
            "set" => Set(Element(Arg(2)), Arg(3), Arg(4)),
            "highlight" => Highlight(Arg(2) == "none" ? null : Element(Arg(2))),
            "pick" => Pick(Arg(2) == "on"),
            "path" => Path(Element(Arg(2))),
            _ => throw new InvalidOperationException("Unknown command " + command),
        }, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ ids

    private int IdOf(DependencyObject element)
    {
        if (_ids.TryGetValue(element, out var boxed))
        {
            return (int)boxed;
        }

        var id = ++_next;
        _ids.Add(element, id);
        _elements[id] = new WeakReference<DependencyObject>(element);
        return id;
    }

    private DependencyObject Element(string id) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        && _elements.TryGetValue(number, out var reference) && reference.TryGetTarget(out var element)
            ? element
            : throw new InvalidOperationException("The element is gone (" + id + ").");

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ windows and trees

    private string Windows(Application application) =>
        Join(application.Windows.Cast<Window>().Select(w => new[] { Text(IdOf(w)), w.Title ?? string.Empty, w.GetType().FullName ?? TypeName(w) }));

    private string Tree(DependencyObject root, bool visual)
    {
        var records = new List<string[]>();
        void Walk(DependencyObject element, int parent)
        {
            var id = IdOf(element);
            records.Add([Text(id), Text(parent), TypeName(element), Name(element), TemplatedParent(element) is null ? "0" : "1"]);
            foreach (var child in visual ? VisualChildren(element) : LogicalChildren(element))
            {
                Walk(child, id);
            }
        }

        Walk(root, 0);
        return Join(records);
    }

    // ------------------------------------------------------------------ properties

    /// <summary>The element's dependency properties: its type's (and its bases'), and the attached ones set on it.</summary>
    internal static List<(DependencyProperty Property, string Name)> PropertiesOf(DependencyObject element)
    {
        var type = element.GetType();
        var seen = new HashSet<DependencyProperty>();
        var result = new List<(DependencyProperty, string)>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(DependencyProperty) || field.GetValue(null) is not DependencyProperty property || !seen.Add(property))
                {
                    continue;
                }

                if (DependencyPropertyDescriptor.FromProperty(property, type) is { IsAttached: true })
                {
                    if (element.ReadLocalValue(property) != DependencyProperty.UnsetValue)
                    {
                        result.Add((property, property.OwnerType.Name + "." + property.Name));
                    }

                    continue;
                }

                result.Add((property, property.Name));
            }
        }

        var local = element.GetLocalValueEnumerator();
        while (local.MoveNext())
        {
            if (seen.Add(local.Current.Property))
            {
                var property = local.Current.Property;
                var owned = property.OwnerType.IsInstanceOfType(element);
                result.Add((property, owned ? property.Name : property.OwnerType.Name + "." + property.Name));
            }
        }

        return result;
    }

    /// <summary>Where the value comes from, in the words of the Avalonia agent where they match.</summary>
    internal static string Source(DependencyObject element, DependencyProperty property)
    {
        var source = DependencyPropertyHelper.GetValueSource(element, property);
        var name = source.BaseValueSource switch
        {
            BaseValueSource.Local => "Local",
            BaseValueSource.Style => "Style",
            BaseValueSource.StyleTrigger => "Style trigger",
            BaseValueSource.ImplicitStyleReference => "Implicit style",
            BaseValueSource.DefaultStyle => "Theme style",
            BaseValueSource.DefaultStyleTrigger => "Theme style trigger",
            BaseValueSource.ParentTemplate => "Template",
            BaseValueSource.ParentTemplateTrigger or BaseValueSource.TemplateTrigger => "Template trigger",
            BaseValueSource.Inherited => "Inherited",
            BaseValueSource.Default => "Default",
            _ => source.BaseValueSource.ToString(),
        };
        return source.IsAnimated ? name + " (animated)" : name;
    }

    internal static string Format(object? value) => value switch
    {
        null => "(null)",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Props(DependencyObject element) =>
        Join(PropertiesOf(element)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new[]
            {
                p.Name,
                Format(SafeValue(element, p.Property)),
                p.Property.PropertyType.Name,
                Source(element, p.Property),
                p.Property.ReadOnly ? "1" : "0",
            }));

    private static object? SafeValue(DependencyObject element, DependencyProperty property)
    {
        try
        {
            return element.GetValue(property);
        }
        catch (Exception ex)
        {
            return "(" + ex.Message + ")";
        }
    }

    private static string Set(DependencyObject element, string name, string text)
    {
        var (property, _) = PropertiesOf(element).FirstOrDefault(p => p.Name == name);
        if (property is null)
        {
            throw new InvalidOperationException("No property " + name + ".");
        }

        if (property.ReadOnly)
        {
            throw new InvalidOperationException(name + " is read-only.");
        }

        element.SetValue(property, Parse(element, property, text));
        return "ok";
    }

    /// <summary>A value from text as XAML converts it: the property's own converter (Width's LengthConverter), else its type's.</summary>
    internal static object? Parse(DependencyObject element, DependencyProperty property, string text)
    {
        var type = property.PropertyType;
        if (text == "(null)" && (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null))
        {
            return null;
        }

        if (type == typeof(string) || type == typeof(object))
        {
            return text;
        }

        var converter = DependencyPropertyDescriptor.FromProperty(property, element.GetType())?.Converter;
        if (converter is null || !converter.CanConvertFrom(typeof(string)))
        {
            converter = TypeDescriptor.GetConverter(type);
        }

        if (converter.CanConvertFrom(typeof(string)))
        {
            return converter.ConvertFromInvariantString(text);
        }

        return Convert.ChangeType(text, Nullable.GetUnderlyingType(type) ?? type, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ highlight, pick, path

    private string Highlight(DependencyObject? element)
    {
        if (_adorner is { } old)
        {
            AdornerLayer.GetAdornerLayer(old.AdornedElement)?.Remove(old);
            _adorner = null;
        }

        if (element is UIElement visual && AdornerLayer.GetAdornerLayer(visual) is { } layer)
        {
            _adorner = new HighlightAdorner(visual);
            layer.Add(_adorner);
        }

        // A content element, or a window without an adorner layer: no highlight (ADR 0052).
        return "ok";
    }

    private string Pick(bool on)
    {
        _picking = on;
        if (on && !_hooked)
        {
            // A class handler sees every window's presses, handled ones too; it does nothing while not picking.
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
            _hooked = true;
        }

        return "ok";
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_picking || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        _picking = false;
        e.Handled = true; // the click selects; it doesn't press the button
        send("event\tpicked\t" + Text(IdOf(source)) + "\t" + Text(IdOf(OutOfTemplates(source))));
    }

    private static string Path(DependencyObject element)
    {
        // Out of templates first: the XAML declares the templated parent, not the template's parts.
        var (path, top) = PathOf(OutOfTemplates(element), null, null);
        return (top.GetType().FullName ?? string.Empty) + "\t" + path;
    }
}

/// <summary>The live tools' highlight: a translucent rectangle over the selected element, outside its layout.</summary>
internal sealed class HighlightAdorner : Adorner
{
    private static readonly Brush s_fill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x64, 0x95, 0xED)));
    private static readonly Pen s_pen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x95, 0xED))), 1));

    public HighlightAdorner(UIElement adorned)
        : base(adorned) => IsHitTestVisible = false;

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext drawingContext) =>
        drawingContext.DrawRectangle(s_fill, s_pen, new Rect(AdornedElement.RenderSize));
}
