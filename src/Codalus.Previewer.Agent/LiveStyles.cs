using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using static Codalus.Previewer.Agent.PreviewAgent;

namespace Codalus.Previewer.Agent;

/// <summary>
/// The Live Property Explorer's style explanation (M64b, ADR 0103): for an element of a running Avalonia app, each
/// style that targets its type (or one of its bases) in its scope (its own <c>Styles</c>, its ancestors', the
/// application's, included style files), its control theme and the theme's nested styles: whether it applies now,
/// and when it doesn't, the part of its selector that fails (its type, a class or pseudo-class, its name, a property,
/// a parent or ancestor, its template) and why; and the style's setters, with those a higher priority overrides.
/// Whether a style applies is Avalonia's own answer (the element's value store: a style frame, active or not); the
/// failing part is the agent's reading of the selector against the element as it is now.
/// Records: kind (<c>style</c>, <c>theme</c>, <c>nested</c>), selector, state (<c>applies</c>, <c>inactive</c> or
/// <c>no</c>), reason, setters (<c>|</c>-separated <c>Property = value</c>, <c>(overridden: Local)</c> when so),
/// file, line.
/// </summary>
internal static class LiveStyles
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private const int MaxStyles = 80;

    public static string Explain(object element)
    {
        var frames = Frames(element);
        var records = new List<string[]>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // The control theme first: the element's, as the value store has it (or its Theme property).
        var theme = frames.Keys.FirstOrDefault(s => s.GetType().Name == "ControlTheme") ?? Property(element, "Theme");
        if (theme is not null && seen.Add(theme))
        {
            records.Add(Record("theme", Property(theme, "TargetType") is Type target ? "ControlTheme " + target.Name : "ControlTheme",
                frames.TryGetValue(theme, out var active) ? (active ? "applies" : "inactive") : "no",
                frames.ContainsKey(theme) ? string.Empty : "The element doesn't use this theme now.", element, theme));
            foreach (var nested in NestedStyles(theme))
            {
                if (seen.Add(nested) && Relevant(element, nested, theme))
                {
                    records.Add(Explain(element, nested, theme, "nested", frames));
                }
            }
        }

        // A template part: its templated parent's theme's styles reach it (/template/).
        if (Property(element, "TemplatedParent") is { } templatedParent && Property(templatedParent, "Theme") is { } parentTheme)
        {
            foreach (var nested in NestedStyles(parentTheme))
            {
                if (seen.Add(nested) && Relevant(element, nested, parentTheme))
                {
                    records.Add(Explain(element, nested, parentTheme, "nested", frames));
                }
            }
        }

        foreach (var style in StylesInScope(element))
        {
            if (records.Count >= MaxStyles)
            {
                break;
            }

            if (seen.Add(style) && (frames.ContainsKey(style) || Relevant(element, style, null)))
            {
                records.Add(Explain(element, style, null, "style", frames));
            }
        }

        return string.Join(LiveAgent.Record.ToString(), records.Select(r => string.Join(LiveAgent.Field.ToString(), r.Select(LiveBreak.Clean))));
    }

    private static string[] Record(string kind, string selector, string state, string reason, object element, object style)
    {
        var location = LiveBreak.LocationOf(style);
        return [kind, selector, state, reason, Setters(element, style, state == "applies"), location?.File ?? string.Empty,
            (location?.Line ?? 0).ToString(CultureInfo.InvariantCulture)];
    }

    private static string[] Explain(object element, object style, object? theme, string kind, Dictionary<object, bool> frames)
    {
        var text = SelectorText(style, theme);
        var (matches, reason) = Evaluate(element, text, theme);
        string state;
        if (frames.TryGetValue(style, out var active))
        {
            state = active ? "applies" : "inactive";
            if (!active && matches)
            {
                reason = "Avalonia has it inactive now, though its selector reads as matching.";
            }
            else if (active)
            {
                reason = string.Empty;
            }
        }
        else
        {
            state = "no";
            if (matches)
            {
                reason = "Its selector matches the element now, but Avalonia didn't apply it (it reads ancestors and templates when the element joins the tree): the element was moved, or the style came later.";
            }
        }

        return Record(kind, text, state, reason, element, style);
    }

    // ------------------------------------------------------------------ the element's styles

    /// <summary>The styles of the element's value store (Avalonia's <c>GetValueStoreDiagnostic</c>), and whether each is active.</summary>
    private static Dictionary<object, bool> Frames(object element)
    {
        var frames = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
        try
        {
            var extensions = AvaloniaType("Avalonia.Diagnostics.AvaloniaObjectExtensions");
            var diagnostic = extensions.GetMethod("GetValueStoreDiagnostic", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [element]);
            if (diagnostic is not null && Property(diagnostic, "AppliedFrames") is IEnumerable applied)
            {
                foreach (var frame in applied)
                {
                    if (frame is not null && Property(frame, "Source") is { } source && source.GetType().Name is "Style" or "ControlTheme")
                    {
                        frames[source] = Property(frame, "IsActive") is true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
        }

        return frames;
    }

    /// <summary>The styles that reach the element: its own and its ancestors' <c>Styles</c>, then the application's, nearest first.</summary>
    private static IEnumerable<object> StylesInScope(object element)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var current = element; current is not null && visited.Add(current) && visited.Count < 512;
             current = LogicalParent(current) ?? Property(current, "TemplatedParent") ?? VisualParent(current))
        {
            if (current.GetType().GetProperty("Styles", Instance) is { } property && property.GetValue(current) is IEnumerable styles)
            {
                foreach (var style in Flatten(styles, 0))
                {
                    yield return style;
                }
            }
        }

        if (AvaloniaType("Avalonia.Application").GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is { } application
            && Property(application, "Styles") is IEnumerable applicationStyles)
        {
            foreach (var style in Flatten(applicationStyles, 0))
            {
                yield return style;
            }
        }
    }

    /// <summary>The <c>Style</c>s of a styles collection: nested collections, included files (<c>StyleInclude.Loaded</c>) and nested styles.</summary>
    private static IEnumerable<object> Flatten(IEnumerable styles, int depth)
    {
        if (depth > 16)
        {
            yield break;
        }

        foreach (var item in styles.Cast<object>().ToList())
        {
            if (item.GetType().GetProperty("Selector", Instance) is not null)
            {
                yield return item;
                foreach (var nested in NestedStyles(item))
                {
                    yield return nested;
                }
            }
            else if (item is IEnumerable inner and not string)
            {
                foreach (var style in Flatten(inner, depth + 1))
                {
                    yield return style;
                }
            }
            else if (SafeProperty(item, "Loaded") is { } loaded)
            {
                foreach (var style in Flatten(loaded is IEnumerable list ? list : new[] { loaded }, depth + 1))
                {
                    yield return style;
                }
            }
        }
    }

    /// <summary>A style's (or a theme's) nested styles, depth first.</summary>
    private static IEnumerable<object> NestedStyles(object style)
    {
        if (Property(style, "Children") is not IEnumerable children)
        {
            yield break;
        }

        foreach (var child in children.Cast<object>().ToList())
        {
            if (child.GetType().GetProperty("Selector", Instance) is null)
            {
                continue;
            }

            yield return child;
            foreach (var nested in NestedStyles(child))
            {
                yield return nested;
            }
        }
    }

    private static object? SafeProperty(object target, string name)
    {
        try
        {
            return Property(target, name);
        }
        catch (TargetInvocationException)
        {
            return null; // an include that fails to load is the app's to report
        }
    }

    /// <summary>The selector's text; in a control theme, its nesting part (<c>^</c>, which Avalonia writes as the theme's type) is the theme's type or a subclass.</summary>
    private static string SelectorText(object style, object? theme)
    {
        var text = style.ToString() ?? string.Empty;
        if (theme is not null && Property(theme, "TargetType") is Type target && text.StartsWith(target.Name, StringComparison.Ordinal))
        {
            text = ":is(" + target.Name + ")" + text.Substring(target.Name.Length);
        }

        return text;
    }

    /// <summary>Whether a style is about this element: the type of its selector's last part is the element's, a base, or none.</summary>
    private static bool Relevant(object element, object style, object? theme)
    {
        foreach (var alternative in SelectorParser.Parse(SelectorText(style, theme)))
        {
            if (alternative.Count == 0)
            {
                continue;
            }

            var last = alternative[alternative.Count - 1].Compound;
            if (last.Type is null || TypeMatches(element, last.Type, isOrBase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TypeMatches(object element, string type, bool isOrBase)
    {
        var name = type.Substring(type.IndexOf('|') + 1);
        var key = Property(element, "StyleKey") as Type ?? element.GetType();
        if (!isOrBase)
        {
            return key.Name == name;
        }

        for (var current = key; current is not null; current = current.BaseType)
        {
            if (current.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ setters

    private static string Setters(object element, object style, bool applies)
    {
        if (Property(style, "Setters") is not IEnumerable setters)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var setter in setters.Cast<object>())
        {
            if (Property(setter, "Property") is not { } property)
            {
                continue;
            }

            var name = Property(property, "Name") as string ?? "?";

            // An attached property the element's type also owns (TextBlock.FontSize, TextElement's) reads as its own.
            if (Property(property, "IsAttached") is true && Property(property, "OwnerType") is Type owner && element.GetType().GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) is null)
            {
                name = owner.Name + "." + name;
            }

            var value = Property(setter, "Value");
            var text = name + " = " + Describe(value);
            if (applies && property.GetType().GetProperty("OwnerType") is not null)
            {
                string source;
                try
                {
                    source = LiveAgent.Source(element, property);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or ArgumentException)
                {
                    source = string.Empty;
                }

                // A local value and an animation outrank every style.
                if (source is "Local" or "Animation")
                {
                    text += " (overridden: " + source + ")";
                }
            }

            parts.Add(text.Replace('|', '/'));
        }

        return string.Join("|", parts);
    }

    /// <summary>A setter's value as XAML would write it: a markup extension by its name and key, else the value.</summary>
    private static string Describe(object? value)
    {
        if (value is null || (!value.GetType().Name.EndsWith("Extension", StringComparison.Ordinal) && !value.GetType().Name.Contains("Binding")))
        {
            return LiveBreak.Format(value);
        }

        var kind = value.GetType().Name.Replace("Extension", string.Empty);
        var key = Property(value, "ResourceKey") ?? Property(value, "Path");
        return "{" + kind + (key is null ? string.Empty : " " + LiveBreak.Format(key)) + "}";
    }

    // ------------------------------------------------------------------ the selector against the element

    /// <summary>Whether the selector matches the element now, or the first part that fails and why.</summary>
    internal static (bool Matches, string Reason) Evaluate(object element, string selector, object? theme)
    {
        string? firstReason = null;
        foreach (var alternative in SelectorParser.Parse(selector))
        {
            var (matches, reason) = Match(alternative, alternative.Count - 1, element);
            if (matches)
            {
                return (true, string.Empty);
            }

            firstReason ??= reason;
        }

        return (false, firstReason ?? "Its selector couldn't be read.");
    }

    private static (bool, string) Match(List<SelectorStep> steps, int index, object element)
    {
        var step = steps[index];
        var (ok, why) = MatchCompound(step.Compound, element);
        if (!ok)
        {
            return (false, why);
        }

        if (index == 0)
        {
            return (true, string.Empty);
        }

        var left = steps[index - 1].Compound.Text;
        switch (step.Combinator)
        {
            case Combinator.Child:
                return LogicalParent(element) is { } parent
                    ? Match(steps, index - 1, parent) is (true, _) ? (true, string.Empty) : (false, $"'{left} >': its parent is a {TypeName(parent)}{NameOf(parent)}, which isn't '{left}'.")
                    : (false, $"'{left} >': it has no parent.");
            case Combinator.Template:
                return Property(element, "TemplatedParent") is { } templated
                    ? Match(steps, index - 1, templated) is (true, _) ? (true, string.Empty) : (false, $"'{left} /template/': its templated parent is a {TypeName(templated)}{NameOf(templated)}, which isn't '{left}'.")
                    : (false, $"'/template/': it isn't a part of a control's template.");
            default:
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                for (var ancestor = LogicalParent(element); ancestor is not null && visited.Add(ancestor); ancestor = LogicalParent(ancestor))
                {
                    if (Match(steps, index - 1, ancestor) is (true, _))
                    {
                        return (true, string.Empty);
                    }
                }

                return (false, $"'{left}': none of its ancestors is '{left}'.");
        }
    }

    private static string NameOf(object element) => Property(element, "Name") is string { Length: > 0 } name ? " named " + name : string.Empty;

    private static (bool, string) MatchCompound(Compound compound, object element)
    {
        var type = TypeName(element);
        if (compound.Type is { } wanted && !TypeMatches(element, wanted, compound.IsOrBase))
        {
            return (false, compound.IsOrBase
                ? $"':is({wanted})': it's a {type}, which isn't a {wanted}."
                : $"'{wanted}': it's a {type} (a type selector names the exact type; ':is({wanted})' takes subclasses too).");
        }

        if (compound.Name is { } name && Property(element, "Name") as string != name)
        {
            return (false, Property(element, "Name") is string { Length: > 0 } actual ? $"'#{name}': its name is {actual}." : $"'#{name}': it has no name.");
        }

        var classes = Property(element, "Classes") is IEnumerable list ? list.Cast<object>().Select(c => c.ToString() ?? string.Empty).ToList() : [];
        foreach (var wantedClass in compound.Classes)
        {
            if (!classes.Contains(wantedClass))
            {
                if (wantedClass.StartsWith(":", StringComparison.Ordinal))
                {
                    return (false, $"'{wantedClass}': the {type} isn't {wantedClass.Substring(1)} now.");
                }

                var own = classes.Where(c => !c.StartsWith(":", StringComparison.Ordinal)).ToList();
                return (false, $"'.{wantedClass}': it has no class {wantedClass} (its classes: {(own.Count == 0 ? "none" : string.Join(" ", own))}).");
            }
        }

        foreach (var (property, value) in compound.Properties)
        {
            var match = LiveAgent.PropertiesOf(element).FirstOrDefault(p => p.Name == property || p.Name.EndsWith("." + property, StringComparison.Ordinal));
            if (match.Property is null)
            {
                return (false, $"'[{property}={value}]': it has no property {property}.");
            }

            var actual = LiveBreak.Format(LiveAgent.GetValue(element, match.Property));
            if (!string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"'[{property}={value}]': its {property} is {actual}.");
            }
        }

        foreach (var not in compound.Nots)
        {
            if (Evaluate(element, not, null) is (true, _))
            {
                return (false, $"':not({not})': it is {not}.");
            }
        }

        return (true, string.Empty);
    }
}

internal enum Combinator
{
    None,
    Descendant,
    Child,
    Template,
}

/// <summary>A selector's simple part: <c>Button.primary:pointerover#Save[IsEnabled=True]:not(.x)</c>.</summary>
internal sealed class Compound
{
    public string Text { get; set; } = string.Empty;

    public string? Type { get; set; }

    /// <summary><c>:is(Type)</c>: the type or a subclass.</summary>
    public bool IsOrBase { get; set; }

    public string? Name { get; set; }

    /// <summary>Classes, and pseudo-classes with their colon (<c>:pointerover</c>), as Avalonia keeps them.</summary>
    public List<string> Classes { get; } = [];

    public List<(string Property, string Value)> Properties { get; } = [];

    public List<string> Nots { get; } = [];
}

/// <summary>A compound and the combinator that links it to the one before (<see cref="Combinator.None"/> for the first).</summary>
internal sealed class SelectorStep(Combinator combinator, Compound compound)
{
    public Combinator Combinator { get; } = combinator;

    public Compound Compound { get; } = compound;
}

/// <summary>Reads Avalonia's selector text (as <c>Selector.ToString</c> writes it) into its alternatives' steps.</summary>
internal static class SelectorParser
{
    public static List<List<SelectorStep>> Parse(string text)
    {
        var alternatives = new List<List<SelectorStep>>();
        foreach (var alternative in SplitTop(text, ','))
        {
            var steps = new List<SelectorStep>();
            var combinator = Combinator.None;
            var i = 0;
            var s = alternative.Trim();
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i]))
                {
                    i++;
                    if (steps.Count > 0 && combinator == Combinator.None)
                    {
                        combinator = Combinator.Descendant;
                    }

                    continue;
                }

                if (s[i] == '>')
                {
                    combinator = Combinator.Child;
                    i++;
                    continue;
                }

                if (string.CompareOrdinal(s, i, "/template/", 0, "/template/".Length) == 0)
                {
                    combinator = Combinator.Template;
                    i += "/template/".Length;
                    continue;
                }

                var start = i;
                var compound = ReadCompound(s, ref i);
                compound.Text = s.Substring(start, i - start);
                steps.Add(new SelectorStep(steps.Count == 0 ? Combinator.None : combinator, compound));
                combinator = Combinator.None;
                if (i == start)
                {
                    i++; // a character the reader doesn't know: past it
                }
            }

            alternatives.Add(steps);
        }

        return alternatives;
    }

    private static Compound ReadCompound(string s, ref int i)
    {
        var compound = new Compound();
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '>' && string.CompareOrdinal(s, i, "/template/", 0, "/template/".Length) != 0)
        {
            var c = s[i];
            if (c == '^')
            {
                i++;
            }
            else if (c == '.' || c == '#')
            {
                i++;
                var name = Identifier(s, ref i);
                if (c == '.')
                {
                    compound.Classes.Add(name);
                }
                else
                {
                    compound.Name = name;
                }
            }
            else if (c == ':')
            {
                i++;
                var name = Identifier(s, ref i);
                if (i < s.Length && s[i] == '(')
                {
                    var argument = Balanced(s, ref i);
                    switch (name)
                    {
                        case "is":
                            compound.Type = argument.Trim();
                            compound.IsOrBase = true;
                            break;
                        case "not":
                            compound.Nots.Add(argument.Trim());
                            break;
                    }

                    // :nth-child() and the like aren't read: they count as matching.
                }
                else
                {
                    compound.Classes.Add(":" + name);
                }
            }
            else if (c == '[')
            {
                var end = s.IndexOf(']', i);
                var inner = s.Substring(i + 1, (end < 0 ? s.Length : end) - i - 1);
                i = end < 0 ? s.Length : end + 1;
                var equals = inner.IndexOf('=');
                if (equals > 0)
                {
                    compound.Properties.Add((inner.Substring(0, equals).Trim().Trim('(', ')'), inner.Substring(equals + 1).Trim()));
                }
            }
            else if (char.IsLetter(c) || c == '_')
            {
                compound.Type = Identifier(s, ref i);
            }
            else
            {
                break;
            }
        }

        return compound;
    }

    private static string Identifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '|'))
        {
            i++;
        }

        return s.Substring(start, i - start);
    }

    private static string Balanced(string s, ref int i)
    {
        var depth = 0;
        var start = i + 1;
        for (; i < s.Length; i++)
        {
            depth += s[i] == '(' ? 1 : s[i] == ')' ? -1 : 0;
            if (depth == 0)
            {
                var inner = s.Substring(start, i - start);
                i++;
                return inner;
            }
        }

        return s.Substring(Math.Min(start, s.Length));
    }

    private static IEnumerable<string> SplitTop(string text, char separator)
    {
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in text)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (c == separator && depth == 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        yield return current.ToString();
    }
}
