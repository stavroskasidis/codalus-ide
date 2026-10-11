using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

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

            var items = new List<Control>();
            if (canEdit(node))
            {
                items.Add(Item("Edit Value", () => _ = EditAsync(node, setValue), "F2"));
            }

            items.Add(Item("Copy Value", () => _ = VariableActions.CopyText?.Invoke(node.Value)));
            if (node.EvaluateName is { } expression)
            {
                items.Add(Item("Copy Expression", () => _ = VariableActions.CopyText?.Invoke(expression)));
                items.Add(new Separator());
                items.Add(Item("Add Watch", () => _ = VariableActions.AddWatch?.Invoke(expression)));
                items.Add(Item("QuickWatch", () => _ = VariableActions.QuickWatch?.Invoke(expression), "Shift+F9"));
            }

            var kinds = node.IsString ? Visualizers.Suggested(ViewModels.Visualizers.Unquote(node.Value))
                : node.EvaluateName is not null ? Visualizers.ForType(node.Type)
                : [];
            var plugins = node.EvaluateName is not null ? VariableActions.PluginVisualizers?.Invoke(node.Type) ?? [] : [];
            if (kinds.Count > 0 || plugins.Count > 0)
            {
                items.Add(new Separator());
                var view = new MenuItem { Header = "View" };
                view.ItemsSource = kinds
                    .Select(kind => Item(Visualizers.Title(kind), () => _ = VariableActions.Visualize?.Invoke(node, kind)))
                    .Concat(plugins.Select(p => Item(p.Title, () => _ = VariableActions.VisualizeWithPlugin?.Invoke(node, p.Id))))
                    .ToList();
                items.Add(view);
            }

            new ContextMenu { ItemsSource = items }.Open(tree);
            e.Handled = true;
        };
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

    private static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
        item.Click += (_, _) => action();
        return item;
    }
}
