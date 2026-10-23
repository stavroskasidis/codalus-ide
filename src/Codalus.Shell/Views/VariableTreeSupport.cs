using Avalonia.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// What the debugger's variable trees (Locals, Autos, Watch, QuickWatch) can do with a value, set by the shell: it owns
/// the Watch window, QuickWatch and the visualizers (ADR 0021).
/// </summary>
public static class VariableActions
{
    public static Func<string, Task>? AddWatch { get; set; }

    public static Func<string, Task>? QuickWatch { get; set; }

    public static Func<VariableNode, VisualizerKind, Task>? Visualize { get; set; }

    /// <summary>The plugins' visualizers for a type: id and title (proposed API 0.2.7).</summary>
    public static Func<string?, IReadOnlyList<(string Id, string Title)>>? PluginVisualizers { get; set; }

    public static Func<VariableNode, string, Task>? VisualizeWithPlugin { get; set; }

    public static Func<string, Task>? CopyText { get; set; }
}

/// <summary>The menu and visualizers of a value in the data tip and the variable windows.</summary>
internal static class VariableTreeSupport
{
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

    private static MenuItem Item(string header, string? icon, Action action, string? keys = null) => Controls.MenuEntry.Create(header, icon, keys: keys, click: action);
}
