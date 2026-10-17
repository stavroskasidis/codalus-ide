using System.ComponentModel;
using System.ComponentModel.Design;
using System.Reflection;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using ActionItem = Codalus.Workbench.WinForms.DesignerActionItem;

namespace Codalus.WinForms.Designer;

/// <summary>
/// Smart tags (M41b, ADR 0073): a component's designer actions, from its designer's <c>DesignerActionList</c>s and its
/// verbs, for the IDE's panel. A property sets the action list's property, a method runs, as Visual Studio's panel
/// does, and the state's difference writes what they did (a docking, a new tab page, a tool strip's standard items).
/// A method that would open one of Windows Forms' dialogs (the ones whose names end with "…") opens the IDE's own
/// editor instead (a collection's, an image import), or says it isn't available.
/// </summary>
internal sealed partial class DesignerSession
{
    private const string VerbPrefix = "verb:";

    private List<DesignerActionList> ActionListsOf(IComponent component)
    {
        var lists = new List<DesignerActionList>();
        try
        {
            if (_surface?.GetService(typeof(DesignerActionService)) is DesignerActionService service)
            {
                lists.AddRange(service.GetComponentActions(component).Cast<DesignerActionList>());
            }
            else if (Host.GetDesigner(component) is ComponentDesigner designer)
            {
                lists.AddRange(designer.ActionLists.Cast<DesignerActionList>());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{component.Site?.Name}'s designer actions couldn't be read: {ex.Message}");
        }

        return lists;
    }

    /// <summary>The verbs when there's no action service to list them with the action lists.</summary>
    private DesignerVerbCollection? LooseVerbs(IComponent component)
    {
        if (_surface?.GetService(typeof(DesignerActionService)) is DesignerActionService)
        {
            return null;
        }

        try
        {
            return Host.GetDesigner(component)?.Verbs;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{component.Site?.Name}'s verbs couldn't be read: {ex.Message}");
            return null;
        }
    }

    private bool HasActions(IComponent component)
    {
        foreach (var list in ActionListsOf(component))
        {
            try
            {
                if (list.GetSortedActionItems().Count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"An action list of {component.Site?.Name} failed: {ex.Message}");
            }
        }

        return LooseVerbs(component) is { Count: > 0 };
    }

    public DesignerActionsResult Actions(DesignerComponentsParams parameters)
    {
        var component = Target(parameters.Components.FirstOrDefault() ?? string.Empty) as IComponent ?? throw new InvalidOperationException("No component.");
        var items = new List<ActionItem>();
        foreach (var list in ActionListsOf(component))
        {
            DesignerActionItemCollection sorted;
            try
            {
                sorted = list.GetSortedActionItems();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"An action list of {component.Site?.Name} failed: {ex.Message}");
                continue;
            }

            foreach (System.ComponentModel.Design.DesignerActionItem item in sorted)
            {
                switch (item)
                {
                    case DesignerActionHeaderItem header:
                        items.Add(new ActionItem(DesignerActionKinds.Header, string.Empty, header.DisplayName ?? string.Empty) { Category = header.Category });
                        break;
                    case DesignerActionTextItem text:
                        items.Add(new ActionItem(DesignerActionKinds.Text, string.Empty, text.DisplayName ?? string.Empty) { Category = text.Category });
                        break;
                    case DesignerActionPropertyItem property:
                        items.Add(Describe(list, property));
                        break;
                    case DesignerActionMethodItem method:
                        items.Add(Describe(component, method));
                        break;
                }
            }
        }

        foreach (DesignerVerb verb in (IEnumerable<DesignerVerb>?)LooseVerbs(component)?.Cast<DesignerVerb>() ?? [])
        {
            items.Add(new ActionItem(DesignerActionKinds.Method, VerbPrefix + verb.Text, verb.Text) { Unavailable = !verb.Enabled });
        }

        return new DesignerActionsResult([.. items]);
    }

    private static ActionItem Describe(DesignerActionList list, DesignerActionPropertyItem item)
    {
        var property = list.GetType().GetProperty(item.MemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var result = new ActionItem(DesignerActionKinds.Property, item.MemberName, item.DisplayName ?? item.MemberName) { Category = item.Category, Description = item.Description };
        if (property is null)
        {
            return result with { Unavailable = true };
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var converter = TypeDescriptor.GetConverter(type);
        string? value;
        try
        {
            value = property.GetValue(list) is { } current ? converter.ConvertToInvariantString(current) : string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            value = null;
        }

        if (!property.CanWrite || !converter.CanConvertFrom(typeof(string)))
        {
            return result with { Value = value, Unavailable = true };
        }

        return result with
        {
            Value = value,
            Editor = type == typeof(bool) ? DesignerEditors.Boolean : type.IsEnum ? DesignerEditors.Enum : type == typeof(Color) ? DesignerEditors.Color : DesignerEditors.Text,
            StandardValues = type == typeof(bool) ? ["True", "False"] : type.IsEnum ? System.Enum.GetNames(type) : null,
        };
    }

    /// <summary>A method; one that opens a dialog (its name ends with "…") opens the IDE's editor of the collection or image it's about, or isn't available.</summary>
    private ActionItem Describe(IComponent component, DesignerActionMethodItem item)
    {
        var display = item.DisplayName ?? item.MemberName ?? string.Empty;
        var member = string.IsNullOrEmpty(item.MemberName) ? VerbPrefix + display : item.MemberName;
        var result = new ActionItem(DesignerActionKinds.Method, member, display) { Category = item.Category, Description = item.Description };
        var name = display.TrimEnd();
        if (!name.EndsWith("...", StringComparison.Ordinal) && !name.EndsWith('…'))
        {
            return result;
        }

        var hint = (display + " " + item.MemberName).ToUpperInvariant();
        var collections = CollectionsOf(component).Select(c => c.Info).ToList();
        DesignerCollectionInfo? Pick(params string[] properties) => collections.FirstOrDefault(c => properties.Contains(c.Property));
        var collection = hint.Contains("COLUMN", StringComparison.Ordinal) || hint.Contains("ROW", StringComparison.Ordinal) ? Pick("ColumnStyles", "Columns")
            : hint.Contains("ITEM", StringComparison.Ordinal) ? Pick("Items", "DropDownItems")
            : hint.Contains("TAB", StringComparison.Ordinal) ? Pick("TabPages")
            : hint.Contains("IMAGE", StringComparison.Ordinal) ? Pick("Images")
            : collections.Count == 1 ? collections[0] : null;
        if (collection is not null)
        {
            return result with { Collection = collection.Key };
        }

        if (hint.Contains("IMAGE", StringComparison.Ordinal) && TypeDescriptor.GetProperties(component)["Image"] is { IsReadOnly: false })
        {
            return result with { ImageProperty = "Image" };
        }

        return result with { Unavailable = true };
    }

    /// <summary>Runs an action's method (or a verb), or sets an action's property, and returns what it changed.</summary>
    public DesignerChangeResult InvokeAction(DesignerInvokeActionParams parameters)
    {
        var before = Capture();
        var component = Target(parameters.Component) as IComponent ?? throw Unknown(parameters.Component);
        if (!Run(component, parameters))
        {
            return new DesignerChangeResult([], Render()) { Error = $"{parameters.Component} has no action {parameters.Member}." };
        }

        return Result(before);
    }

    private bool Run(IComponent component, DesignerInvokeActionParams parameters)
    {
        if (parameters.Member.StartsWith(VerbPrefix, StringComparison.Ordinal))
        {
            var text = parameters.Member[VerbPrefix.Length..];
            foreach (DesignerVerb verb in LooseVerbs(component) ?? [])
            {
                if (verb.Text == text)
                {
                    verb.Invoke();
                    return true;
                }
            }
        }

        foreach (var list in ActionListsOf(component))
        {
            foreach (System.ComponentModel.Design.DesignerActionItem item in list.GetSortedActionItems())
            {
                switch (item)
                {
                    case DesignerActionPropertyItem property when property.MemberName == parameters.Member:
                    {
                        var info = list.GetType().GetProperty(property.MemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? throw new InvalidOperationException($"{property.DisplayName} isn't a property of its action list.");
                        var type = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
                        info.SetValue(list, TypeDescriptor.GetConverter(type).ConvertFromInvariantString(parameters.Text ?? string.Empty));
                        return true;
                    }

                    case DesignerActionMethodItem method when (string.IsNullOrEmpty(method.MemberName) ? VerbPrefix + method.DisplayName : method.MemberName) == parameters.Member:
                        method.Invoke();
                        return true;
                }
            }
        }

        return false;
    }
}
