using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// What the debugger's variable trees (Locals, Autos, Watch, QuickWatch) can do with a value, set by the shell: it owns
/// the input dialog, the Watch window and the visualizers (ADR 0021).
/// </summary>
public static class VariableActions
{
    /// <summary>Asks for a new value; null when cancelled.</summary>
    public static Func<VariableNode, Task<string?>>? AskValue { get; set; }

    public static Func<string, Task>? AddWatch { get; set; }

    public static Func<string, Task>? QuickWatch { get; set; }

    public static Func<VariableNode, VisualizerKind, Task>? Visualize { get; set; }

    /// <summary>The plugins' visualizers for a type: id and title (proposed API 0.2.7).</summary>
    public static Func<string?, IReadOnlyList<(string Id, string Title)>>? PluginVisualizers { get; set; }

    public static Func<VariableNode, string, Task>? VisualizeWithPlugin { get; set; }

    public static Func<string, Task>? CopyText { get; set; }
}

/// <summary>Editing (F2, double-click) and the context menu of a variable tree.</summary>
internal static class VariableTreeSupport
{
    public static void Attach(TreeView tree, Func<VariableNode, string, Task<string?>> setValue, Func<VariableNode, bool> canEdit)
    {
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && tree.SelectedItem is VariableNode node && canEdit(node))
            {
                _ = EditAsync(node, setValue);
                e.Handled = true;
            }
        };
        tree.DoubleTapped += (_, _) =>
        {
            if (tree.SelectedItem is VariableNode node && canEdit(node))
            {
                _ = EditAsync(node, setValue);
            }
        };
        tree.ContextRequested += (_, e) =>
        {
            if (tree.SelectedItem is not VariableNode node || node.IsError)
            {
                return;
            }

            new ContextMenu { ItemsSource = MenuItems(node, canEdit(node) ? () => _ = EditAsync(node, setValue) : null) }.Open(tree);
            e.Handled = true;
        };
    }

    /// <summary>A visualizer a value can be opened in: its title, whether a plugin gives it, and what opens it.</summary>
    public sealed record VisualizerChoice(string Title, bool IsPlugin, Action Open);

    /// <summary>
    /// The visualizers <paramref name="node"/> can be opened in, the likely one first: Text, JSON, XML and HTML for a
    /// string, Grid (and Image) for a collection, then the plugins' for its type.
    /// </summary>
    public static IReadOnlyList<VisualizerChoice> VisualizerChoices(VariableNode node)
    {
        if (node.IsError || node.IsPlaceholder)
        {
            return [];
        }

        var kinds = node.IsString ? Visualizers.Suggested(ViewModels.Visualizers.Unquote(node.Value))
            : node.EvaluateName is not null ? Visualizers.ForType(node.Type)
            : [];
        var plugins = node.EvaluateName is not null ? VariableActions.PluginVisualizers?.Invoke(node.Type) ?? [] : [];
        return [
            .. kinds.Select(kind => new VisualizerChoice(Visualizers.Title(kind), false, () => _ = VariableActions.Visualize?.Invoke(node, kind))),
            .. plugins.Select(p => new VisualizerChoice(p.Title, true, () => _ = VariableActions.VisualizeWithPlugin?.Invoke(node, p.Id))),
        ];
    }

    /// <summary>
    /// A value's menu: Edit Value (with <paramref name="edit"/>, for a value that can change), copying, Add Watch,
    /// QuickWatch, Pin to Source (with <paramref name="pin"/>) and the visualizers.
    /// </summary>
    public static List<Control> MenuItems(VariableNode node, Action? edit, Action? pin = null)
    {
        var items = new List<Control>();
        if (edit is not null)
        {
            items.Add(Item("Edit Value", "edit", edit, "F2"));
        }

        items.Add(Item("Copy Value", "copy", () => _ = VariableActions.CopyText?.Invoke(node.Value)));
        if (node.EvaluateName is { } expression)
        {
            items.Add(Item("Copy Expression", "copy", () => _ = VariableActions.CopyText?.Invoke(expression)));
            items.Add(new Separator());
            items.Add(Item("Add Watch", "watch", () => _ = VariableActions.AddWatch?.Invoke(expression)));
            items.Add(Item("QuickWatch", "watch", () => _ = VariableActions.QuickWatch?.Invoke(expression), "Shift+F9"));
            if (pin is not null)
            {
                items.Add(Item("Pin to Source", "pin", pin));
            }
        }

        var visualizers = VisualizerChoices(node);
        if (visualizers.Count > 0)
        {
            items.Add(new Separator());
            items.Add(Controls.MenuEntry.Submenu("View", "window", visualizers.Select(v => Item(v.Title, null, v.Open))));
        }

        return items;
    }

    public static async Task EditAsync(VariableNode node, Func<VariableNode, string, Task<string?>> setValue)
    {
        if (VariableActions.AskValue is not { } ask || await ask(node).ConfigureAwait(true) is not { } value)
        {
            return;
        }

        if (await setValue(node, value).ConfigureAwait(true) is { } error)
        {
            node.Update(error, node.Type, 0);
        }
    }

    private static MenuItem Item(string header, string? icon, Action action, string? keys = null) => Controls.MenuEntry.Create(header, icon, keys: keys, click: action);
}
