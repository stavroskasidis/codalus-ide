using System.Globalization;
using System.Windows;
using System.Windows.Diagnostics;
using static Codalus.Previewer.Wpf.WpfTree;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// The Live Property Explorer's style explanation for WPF (M64b, ADR 0103; Windows only), in the Avalonia agent's
/// records (kind, selector, state, reason, setters, file, line): the element's style (its <c>Style</c> attribute or the
/// implicit one of its type) and the styles it's based on, each with its setters (those a local value or a template
/// overrides noted) and its triggers: a property trigger or a multi-trigger with the condition that's false now and
/// the element's value, a data trigger by whether its setters are in effect (its binding isn't evaluated here), an
/// event trigger by its event. Without a style: why none applies.
/// </summary>
internal static class WpfStyles
{
    public static string Explain(DependencyObject element)
    {
        var records = new List<string[]>();
        var style = element switch
        {
            FrameworkElement fe => fe.Style,
            FrameworkContentElement fce => fce.Style,
            _ => null,
        };
        if (style is null)
        {
            var target = element.GetType().Name;
            records.Add(["style", "(no Style)", "no", $"The {target} has no Style: no Style attribute, and no style keyed by its type ({{x:Type {target}}}) in its resources' scope or the application's. Its theme's default style still applies.", string.Empty, string.Empty, "0"]);
            return Join(records);
        }

        var source = element is FrameworkElement or FrameworkContentElement
            ? DependencyPropertyHelper.GetValueSource(element, element is FrameworkElement ? FrameworkElement.StyleProperty : FrameworkContentElement.StyleProperty).BaseValueSource
            : BaseValueSource.Unknown;
        var how = source == BaseValueSource.ImplicitStyleReference ? "implicit (keyed by its type)" : "its Style attribute or a style setter";
        var depth = 0;
        for (var current = style; current is not null && depth < 16; current = current.BasedOn, depth++)
        {
            var (file, line) = Location(current);
            var targetType = current.TargetType?.Name ?? "?";
            var applies = current.TargetType is null || current.TargetType.IsInstanceOfType(element);
            records.Add([depth == 0 ? "style" : "basedOn", $"Style TargetType={targetType}",
                applies ? "applies" : "no",
                applies ? (depth == 0 ? "The element's style: " + how + "." : "Its style is based on it.") : $"Its TargetType is {targetType}: the element is a {element.GetType().Name}.",
                Setters(element, current.Setters), file, line]);
            foreach (var trigger in current.Triggers)
            {
                records.Add(Trigger(element, trigger));
            }
        }

        return Join(records);
    }

    private static (string File, string Line) Location(object item) =>
        VisualDiagnostics.GetXamlSourceInfo(item) is { SourceUri: { } uri } info
            ? (uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString, info.LineNumber.ToString(CultureInfo.InvariantCulture))
            : (string.Empty, "0");

    private static string Setters(DependencyObject element, SetterBaseCollection setters)
    {
        var parts = new List<string>();
        foreach (var setter in setters.OfType<Setter>())
        {
            if (setter.Property is null)
            {
                continue;
            }

            var text = setter.Property.Name + " = " + LiveAgent.Format(setter.Value);
            if (setter.TargetName is null)
            {
                var source = DependencyPropertyHelper.GetValueSource(element, setter.Property).BaseValueSource;
                if (source is BaseValueSource.Local or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger or BaseValueSource.StyleTrigger or BaseValueSource.TemplateTrigger)
                {
                    text += " (overridden: " + LiveAgent.Source(element, setter.Property) + ")";
                }
            }
            else
            {
                text = setter.TargetName + "." + text;
            }

            parts.Add(text.Replace('|', '/'));
        }

        return string.Join("|", parts);
    }

    private static string[] Trigger(DependencyObject element, TriggerBase trigger)
    {
        var (file, line) = Location(trigger);
        switch (trigger)
        {
            case Trigger property:
            {
                var (met, why) = Condition(element, property.Property, property.Value);
                return ["trigger", $"Trigger {property.Property?.Name}={LiveAgent.Format(property.Value)}", met ? "applies" : "inactive", met ? string.Empty : why, Setters(element, property.Setters), file, line];
            }

            case MultiTrigger multi:
                foreach (var condition in multi.Conditions)
                {
                    var (met, why) = Condition(element, condition.Property, condition.Value);
                    if (!met)
                    {
                        return ["trigger", "MultiTrigger " + string.Join(", ", multi.Conditions.Select(c => c.Property?.Name + "=" + LiveAgent.Format(c.Value))), "inactive", why, Setters(element, multi.Setters), file, line];
                    }
                }

                return ["trigger", "MultiTrigger " + string.Join(", ", multi.Conditions.Select(c => c.Property?.Name + "=" + LiveAgent.Format(c.Value))), "applies", string.Empty, Setters(element, multi.Setters), file, line];
            case DataTrigger data:
            {
                var active = InEffect(element, data.Setters);
                return ["trigger", $"DataTrigger {{Binding {(data.Binding as System.Windows.Data.Binding)?.Path?.Path}}}={LiveAgent.Format(data.Value)}", active ? "applies" : "inactive",
                    active ? string.Empty : "Its setters aren't in effect: its binding's value isn't the trigger's now (the binding isn't evaluated here).", Setters(element, data.Setters), file, line];
            }

            case MultiDataTrigger multiData:
            {
                var active = InEffect(element, multiData.Setters);
                return ["trigger", "MultiDataTrigger", active ? "applies" : "inactive", active ? string.Empty : "Its setters aren't in effect: one of its bindings' values isn't the trigger's now.", Setters(element, multiData.Setters), file, line];
            }

            case EventTrigger events:
                return ["trigger", "EventTrigger " + events.RoutedEvent?.Name, "inactive", "It runs its actions when " + events.RoutedEvent?.Name + " is raised; it sets nothing.", string.Empty, file, line];
            default:
                return ["trigger", trigger.GetType().Name, "inactive", string.Empty, string.Empty, file, line];
        }
    }

    /// <summary>Whether a trigger's condition holds now, and the element's value when it doesn't.</summary>
    private static (bool Met, string Why) Condition(DependencyObject element, DependencyProperty? property, object? wanted)
    {
        if (property is null)
        {
            return (false, "The trigger names no property.");
        }

        var value = element.GetValue(property);
        var met = Equals(value, wanted) || (wanted is string text && LiveAgent.Format(value).Equals(text, StringComparison.OrdinalIgnoreCase));
        return (met, met ? string.Empty : $"{property.Name} is {LiveAgent.Format(value)} (the trigger wants {LiveAgent.Format(wanted)}).");
    }

    /// <summary>Whether a trigger's setters are in effect: each property's value comes from a style trigger and is the setter's.</summary>
    private static bool InEffect(DependencyObject element, SetterBaseCollection setters)
    {
        var own = setters.OfType<Setter>().Where(s => s.Property is not null && s.TargetName is null).ToList();
        return own.Count > 0 && own.All(s => DependencyPropertyHelper.GetValueSource(element, s.Property).BaseValueSource == BaseValueSource.StyleTrigger
            && Equals(element.GetValue(s.Property), s.Value));
    }
}
