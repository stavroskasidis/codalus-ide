using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// WPF's element trees as the previewer and the live tools see them (M40b, ADR 0072), the same model as Avalonia's
/// agent (ADR 0048, 0052): the logical tree's elements (<see cref="FrameworkElement"/> and
/// <see cref="FrameworkContentElement"/>; data items aren't elements), a step of a path is an element's type and its
/// index among its logical siblings of that type, and an element "the XAML declares" is one no template made.
/// Everything here runs on the elements' UI thread.
/// </summary>
internal static class WpfTree
{
    public const char Record = '\u001e';
    public const char Field = '\u001f';

    public static string TypeName(object element)
    {
        var name = element.GetType().Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    public static string Name(DependencyObject element) => element switch
    {
        FrameworkElement fe => fe.Name,
        FrameworkContentElement fce => fce.Name,
        _ => string.Empty,
    };

    public static DependencyObject? TemplatedParent(DependencyObject element) => element switch
    {
        FrameworkElement fe => fe.TemplatedParent,
        FrameworkContentElement fce => fce.TemplatedParent,
        _ => null,
    };

    public static bool IsElement(object item) => item is FrameworkElement or FrameworkContentElement;

    public static DependencyObject? LogicalParent(DependencyObject element) => LogicalTreeHelper.GetParent(element);

    public static IEnumerable<DependencyObject> LogicalChildren(DependencyObject element) =>
        LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().Where(IsElement).ToList();

    public static IEnumerable<DependencyObject> VisualChildren(DependencyObject element)
    {
        if (element is not (Visual or Visual3D))
        {
            return [];
        }

        var count = VisualTreeHelper.GetChildrenCount(element);
        var children = new List<DependencyObject>(count);
        for (var i = 0; i < count; i++)
        {
            children.Add(VisualTreeHelper.GetChild(element, i));
        }

        return children;
    }

    public static DependencyObject? VisualParent(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalParent(element);

    /// <summary>The element out of any templates: the templated parent the XAML declares.</summary>
    public static DependencyObject OutOfTemplates(DependencyObject element)
    {
        var current = element;
        while (TemplatedParent(current) is { } parent)
        {
            current = parent;
        }

        return current;
    }

    /// <summary>Whether <paramref name="element"/> is <paramref name="root"/> or in its logical tree.</summary>
    public static bool IsLogicalDescendant(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = LogicalParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// From a visual hit to the element the XAML declares: out of templates and up the visual tree to an element in
    /// <paramref name="root"/>'s logical tree that no template made (a button's text lands on the button, an item's
    /// template on its list).
    /// </summary>
    public static DependencyObject? DeclaredElement(DependencyObject? hit, DependencyObject root)
    {
        var element = hit;
        while (element is not null && !(TemplatedParent(element) is null && IsElement(element) && IsLogicalDescendant(element, root)))
        {
            element = TemplatedParent(element) ?? VisualParent(element);
        }

        return element;
    }

    /// <summary>The element's index among its logical siblings of the same type (0 without a parent).</summary>
    public static int Ordinal(DependencyObject element)
    {
        if (LogicalParent(element) is not { } parent)
        {
            return 0;
        }

        var type = TypeName(element);
        var index = 0;
        foreach (var sibling in LogicalChildren(parent))
        {
            if (ReferenceEquals(sibling, element))
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

    /// <summary>
    /// The logical path from <paramref name="root"/> (or the top of the tree when it isn't an ancestor) to
    /// <paramref name="element"/>: <c>Type:ordinal:Name</c> steps joined by <c>/</c>; the root's step is named
    /// <paramref name="rootType"/> when given (the previewed XAML's <c>x:Class</c>).
    /// </summary>
    public static (string Path, DependencyObject Top) PathOf(DependencyObject element, DependencyObject? root, string? rootType)
    {
        var steps = new List<string>();
        DependencyObject top = element;
        for (DependencyObject? current = element; current is not null; current = ReferenceEquals(current, root) ? null : LogicalParent(current))
        {
            var isRoot = ReferenceEquals(current, root);
            var type = isRoot && rootType is { Length: > 0 } ? rootType : TypeName(current);
            var ordinal = isRoot ? 0 : Ordinal(current);
            steps.Add(type + ":" + ordinal.ToString(CultureInfo.InvariantCulture) + ":" + Name(current));
            top = current;
        }

        steps.Reverse();
        return (string.Join("/", steps), top);
    }

    /// <summary>The element at <paramref name="path"/> (<c>Type:ordinal</c> steps; the first is the root, whatever its type).</summary>
    public static DependencyObject? Find(DependencyObject root, string path)
    {
        var steps = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split(':')).ToList();
        if (steps.Count == 0)
        {
            return null;
        }

        DependencyObject? element = root;
        foreach (var step in steps.Skip(1))
        {
            var ordinal = step.Length > 1 && int.TryParse(step[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
            element = LogicalChildren(element).Where(c => TypeName(c) == step[0]).Skip(ordinal).FirstOrDefault();
            if (element is null)
            {
                return null;
            }
        }

        return element;
    }

    /// <summary>The element's bounds in <paramref name="ancestor"/>'s coordinates (device-independent pixels), or null.</summary>
    public static Rect? BoundsIn(DependencyObject element, Visual ancestor)
    {
        if (element is not UIElement visual || !(ReferenceEquals(visual, ancestor) || visual.IsDescendantOf(ancestor)))
        {
            return null;
        }

        return ReferenceEquals(visual, ancestor)
            ? new Rect(visual.RenderSize)
            : visual.TransformToAncestor(ancestor).TransformBounds(new Rect(visual.RenderSize));
    }

    public static string Format(Rect bounds) =>
        string.Join(",", new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary>Text for a field of a record: the separators, tabs and line breaks become spaces.</summary>
    public static string Clean(string? text) =>
        (text ?? string.Empty).Replace(Record, ' ').Replace(Field, ' ').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public static string Join(IEnumerable<IEnumerable<string>> records) =>
        string.Join(Record.ToString(), records.Select(r => string.Join(Field.ToString(), r.Select(Clean))));
}
