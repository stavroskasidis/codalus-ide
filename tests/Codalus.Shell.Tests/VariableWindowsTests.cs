using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Debugging.Companion;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Shell.Views.DataTips;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// Locals, Autos, Watch and QuickWatch as the data tip's rows (D44, decision 9, C1, C2): one flat list with each level's
/// indent inside the name cell, so Name, Value and Type stay in columns; expansion kept by evaluate name across steps;
/// changed values in red; a watch that doesn't evaluate with the error icon and color; values edited in place.
/// </summary>
public sealed class VariableWindowsTests
{
    private const string Code = "class Program\n{\n    static void Main()\n    {\n        app.Use();\n        app.Use();\n    }\n}\n";

    private static readonly IReadOnlyList<TypeMember> Members =
    [
        new("Count", "field", "public", false, "App.Builder", "/app/App.dll", true, "int"),
        new("Name", "property", "public", false, "App.Builder", "/app/App.dll", true, "string"),
    ];

    /// <summary>Two stops: between them app.Count goes from 3 to 4.</summary>
    private static object[] Stops() => [Stop(5, "3"), Stop(6, "4")];

    private static Dictionary<string, object> Stop(int line, string count) => new()
    {
        ["line"] = line,
        ["evaluate"] = new Dictionary<string, object> { ["app"] = Result("{App.Builder}", "App.Builder", 10) },
        ["variables"] = new Dictionary<string, object>
        {
            ["1"] = new object[] { Var("app", "{App.Builder}", "App.Builder", 10, "app"), Var("total", "12", "int", 0, "total") },
            ["10"] = new object[] { Var("Count", count, "int", 0, "app.Count"), Var("Name", "\"Development\"", "string", 0, "app.Name") },
        },
    };

    private static async Task<DataTipScenario> StartAsync()
    {
        var s = await DataTipScenario.StartAsync(Code, 5, Stops());
        s.F.Shell.Debugger.Displays = new DebuggerDisplays((type, _, _) => Task.FromResult<TypeDisplayResult?>(new TypeDisplayResult(null, null, null, [], [], null, type == "App.Builder" ? Members : null)));
        return s;
    }

    private static async Task<VariableRowList> ShowAsync(DataTipScenario s, string windowId, object model)
    {
        s.F.Shell.Layout.ShowToolWindow(windowId, focus: false);
        await ShellFixture.PumpUntil(() => List(s, model) is not null);
        return List(s, model)!;
    }

    private static VariableRowList? List(DataTipScenario s, object model) =>
        s.F.Window.GetVisualDescendants().OfType<UserControl>().FirstOrDefault(v => ReferenceEquals(v.DataContext, model))?.GetVisualDescendants().OfType<VariableRowList>().FirstOrDefault();

    private static string[] Names(VariableRowList list) => [.. list.Rows.Select(r => r.Node.Name)];

    private static async Task<VariableListRow> RowAsync(VariableRowList list, string name)
    {
        await ShellFixture.PumpUntil(() => list.Rows.Any(r => r.Node.Name == name) && list.RowOf(list.Rows.First(r => r.Node.Name == name).Node) is not null,
            describe: () => string.Join(", ", Names(list)));
        return list.RowOf(list.Rows.First(r => r.Node.Name == name).Node)!;
    }

    private static double X(Visual control, Visual list) => control.TranslatePoint(default, list)!.Value.X;

    [AvaloniaFact]
    public async Task Locals_is_one_flat_list_with_its_columns_aligned_and_keeps_expansion_across_a_step()
    {
        await using var s = await StartAsync();
        var list = await ShowAsync(s, VariablesViewModel.LocalsWindowId, s.F.Shell.Locals);
        await ShellFixture.PumpUntil(() => Names(list) is ["app", "total"]);

        // Expanded, the members are rows of the same list, one level deeper: the indent is inside the name cell.
        var app = await RowAsync(list, "app");
        Assert.Equal("symbol-local", app.Item.Node.Icon);
        app.Item.Node.IsExpanded = true;
        await ShellFixture.PumpUntil(() => Names(list) is ["app", "Count", "Name", "total"], describe: () => string.Join(", ", Names(list)));
        var count = await RowAsync(list, "Count");
        app = await RowAsync(list, "app");
        var total = await RowAsync(list, "total");
        ShellFixture.RenderFrame(s.F.Window);
        Assert.Equal(1, count.Item.Depth);
        Assert.Equal(("symbol-field", "symbol-property"), (count.Item.Node.Icon, (await RowAsync(list, "Name")).Item.Node.Icon));
        Assert.True(X(count.NameBlock, list) > X(app.NameBlock, list) + VariableListRow.IndentPerLevel - 1);
        Assert.Equal(X(app.ValueBlock, list), X(count.ValueBlock, list), 1);
        Assert.Equal(X(total.ValueBlock, list), X(count.ValueBlock, list), 1);
        Assert.Equal(X(app.TypeBlock, list), X(count.TypeBlock, list), 1);
        Assert.Equal(("3", "int"), (count.ValueBlock.Text, count.TypeBlock.Text));

        // A step builds new rows: app is open again (by its evaluate name), and Count, now 4, is red.
        await s.F.Shell.Debugger.StepOverAsync();
        await ShellFixture.PumpUntil(() => s.F.Shell.Debugger.State == DebugState.Stopped && s.Editor.CurrentDebugLine() == 6);
        await ShellFixture.PumpUntil(() => Names(list) is ["app", "Count", "Name", "total"] && list.Rows[1].Node.Value == "4", describe: () => string.Join(", ", list.Rows.Select(r => r.Node.Name + "=" + r.Node.Value)));
        count = await RowAsync(list, "Count");
        Assert.True(count.Item.Node.IsChanged);
        Assert.Contains("changed", count.ValueBlock.Classes);
        Assert.DoesNotContain("changed", (await RowAsync(list, "Name")).ValueBlock.Classes);

        // Left collapses; the next stop keeps it collapsed.
        list.FocusRow(list.Rows[0]);
        s.F.Press(Key.Left);
        await ShellFixture.PumpUntil(() => Names(list) is ["app", "total"], describe: () => $"{string.Join(", ", Names(list))}, focus on {TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement()}");
        Assert.False(s.F.Shell.Locals.Expansion.IsExpanded(list.Rows[0].Node));
    }

    [AvaloniaFact]
    public async Task A_watch_that_doesnt_evaluate_shows_the_error_icon_and_values_are_edited_in_place()
    {
        await using var s = await StartAsync();
        var list = await ShowAsync(s, WatchViewModel.ToolWindowId, s.F.Shell.Watch);
        await s.F.Shell.Watch.AddAsync("app.Count");
        await s.F.Shell.Watch.AddAsync("missing");
        await ShellFixture.PumpUntil(() => Names(list) is ["app.Count", "missing"] && list.Rows[1].Node.IsError);

        var missing = await RowAsync(list, "missing");
        Assert.Equal("error: The name 'missing' does not exist in the current context", missing.ValueBlock.Text);
        Assert.Contains("error", missing.ValueBlock.Classes);
        Assert.Equal("error", missing.GetVisualDescendants().OfType<Codalus.Shell.Controls.Icon>().First(i => i.Kind is "error" or "symbol-local").Kind);

        // F2 edits the selected watch in place (setExpression); Enter sets it, and the watches are evaluated again.
        var watch = await RowAsync(list, "app.Count");
        Assert.Equal(("3", "symbol-property"), (watch.ValueBlock.Text, watch.Item.Node.Icon)); // guessed from the name
        list.FocusRow(list.Rows[0]);
        s.F.Press(Key.F2);
        await ShellFixture.PumpUntil(() => watch.Editor.Box is { IsFocused: true });
        watch.Editor.Box!.Text = "7";
        s.F.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => list.Rows[0].Node.Value == "7", describe: () =>
            $"value {list.Rows[0].Node.Value}, same node {ReferenceEquals(list.Rows[0].Node, watch.Item.Node)}, editing {watch.Editor.IsEditing}, error {watch.Editor.Error}, " +
            $"box focused {watch.Editor.Box?.IsFocused}, box text {watch.Editor.Box?.Text}, focus on {TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement()}, " +
            $"debugger {s.F.Shell.Debugger.State}, row attached {watch.IsAttachedToVisualTree()}");
        Assert.Equal("7", (await s.F.Shell.Debugger.EvaluateAsync("app.Count", "watch", TestContext.Current.CancellationToken)).Result);

        // Delete removes a watch. The edit evaluates the watches again, which rebuilds the rows: a row focused before that
        // lost the focus, and Delete went nowhere (macOS CI, PR #175). Wait for the evaluation, and focus the row again if
        // a late rebuild took it.
        await ShellFixture.PumpUntil(() => list.Rows.Any(r => r.Node.Name == "missing" && r.Node.IsError));
        for (var attempt = 0; attempt < 3 && Names(list) is not ["app.Count"]; attempt++)
        {
            list.FocusRow(list.Rows.First(r => r.Node.Name == "missing"));
            s.F.Press(Key.Delete);
            try
            {
                await ShellFixture.PumpUntil(() => Names(list) is ["app.Count"], seconds: 5);
            }
            catch (Exception) when (attempt < 2)
            {
            }
        }

        Assert.True(Names(list) is ["app.Count"], $"the watches: {string.Join(", ", Names(list))}, focus on {TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement()}");
    }

    [AvaloniaFact]
    public async Task QuickWatch_shows_the_expression_and_its_members_as_rows()
    {
        await using var s = await StartAsync();
        await s.F.Shell.ShowQuickWatchAsync("app");
        var list = await ShowAsync(s, QuickWatchViewModel.ToolWindowId, s.F.Shell.QuickWatch);
        await ShellFixture.PumpUntil(() => Names(list) is ["app", "Count", "Name"], describe: () => string.Join(", ", Names(list)));
        var name = await RowAsync(list, "Name");
        Assert.Equal(("\"Development\"", "string", 1), (name.ValueBlock.Text, name.TypeBlock.Text, name.Item.Depth));
        Assert.Contains(name.GetVisualDescendants().OfType<ViewButton>(), b => b.IsVisible); // a string: Text, JSON, XML, HTML
    }
}
