using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Document Outline on a file of 10,000 symbols (M56): rows in a list box that creates only those in view, built off
/// the UI thread while the window shows, kept across the rebuilds of each pause in typing with their expansion.
/// </summary>
public sealed class DocumentOutlineTests
{
    /// <summary>A namespace of <paramref name="classes"/> classes of <paramref name="members"/> methods each (two of them overloads), on their own lines.</summary>
    internal static DocumentSymbol[] Symbols(int classes, int members, int extra = 0)
    {
        DocumentSymbol Symbol(string name, int kind, int start, int end, DocumentSymbol[]? children = null) =>
            new(name, kind, new Range(new Position(start, 0), new Position(end, 1)), new Range(new Position(start, 4), new Position(start, 4 + name.Length))) { Children = children, Detail = kind == SymbolKind.Method ? "()" : null };

        var types = new List<DocumentSymbol>();
        var line = 1;
        for (var c = 0; c < classes; c++)
        {
            var start = line++;
            var methods = new List<DocumentSymbol>();
            for (var m = 0; m < members + (c == 0 ? extra : 0); m++)
            {
                methods.Add(Symbol(m < 2 ? "Overload" : $"Method{m}", SymbolKind.Method, line, line + 1));
                line += 2;
            }

            types.Add(Symbol($"Class{c}", SymbolKind.Class, start, line++, [.. methods]));
        }

        return [Symbol("App", SymbolKind.Namespace, 0, line, [.. types])];
    }

    internal static async Task<(ShellFixture Fixture, CodeEditorController Editor)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var file = Path.Combine(f.UserData, "Large.txt");
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Range(0, 21_000).Select(i => $"line {i}")));
        var document = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        return (f, document!.Controller!);
    }

    private static string Describe(DocumentOutlineViewModel outline, int builds) =>
        $"{outline.Builds} builds (from {builds}), building {outline.IsBuilding}, shown {outline.IsShown}, {outline.Rows.Count} rows";

    internal static async Task ShowOutlineAsync(ShellFixture f)
    {
        // The Welcome plugin shows its Start view once at startup, as the left column's active tab: coming after the Outline
        // (the plugin host starts seconds late on a busy machine), it hid it before its content was made. It comes first.
        await ShellFixture.PumpUntil(() => f.Shell.Layout.OpenToolWindows.Any(t => t.Id == "welcome.view"), seconds: 60);
        f.Shell.Layout.ShowToolWindow(DocumentOutlineViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(
            () => f.Window.GetVisualDescendants().OfType<DocumentOutlineView>().Any(v => v.IsEffectivelyVisible) && f.Shell.DocumentOutline.IsShown,
            describe: () => $"shown {f.Shell.DocumentOutline.IsShown}, layout visible {f.Shell.Layout.IsVisible(DocumentOutlineViewModel.ToolWindowId)}, views ["
                + string.Join(", ", f.Window.GetVisualDescendants().OfType<DocumentOutlineView>().Select(v => $"visible {v.IsEffectivelyVisible}, model {ReferenceEquals(v.DataContext, f.Shell.DocumentOutline)}"))
                + $"], active {f.Shell.ActiveEditor?.Model.Name}");
    }

    [AvaloniaFact]
    public async Task Ten_thousand_symbols_show_only_the_rows_in_view_and_keep_them_across_rebuilds()
    {
        var (f, editor) = await OpenAsync();
        await using var fixture = f;
        await ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;

        editor.Insights.ShowSymbols(Symbols(100, 100));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 10_101 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        ShellFixture.Pump();
        var list = f.Window.GetVisualDescendants().OfType<DocumentOutlineView>().First(v => v.IsEffectivelyVisible).FindControl<ListBox>("List")!;
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 200);
        Assert.Equal(("App", 0, true), (outline.Rows[0].Name, outline.Rows[0].Depth, outline.Rows[0].IsExpanded));
        Assert.Equal(["Overload", "Overload"], outline.Rows.Skip(2).Take(2).Select(r => r.Name));
        Assert.NotEqual(outline.Rows[2].Key, outline.Rows[3].Key); // overloads are rows of their own

        // Each pause in typing sends the symbols again: the same symbols change no row, and keep every row's instance.
        var rows = outline.Rows.ToList();
        var changes = 0;
        outline.Rows.CollectionChanged += (_, _) => changes++;
        var builds = outline.Builds;
        editor.Insights.ShowSymbols(Symbols(100, 100));
        await ShellFixture.PumpUntil(() => outline.Builds == builds + 1 && !outline.IsBuilding, describe: () => Describe(outline, builds));
        Assert.Equal(0, changes);
        Assert.True(rows.Zip(outline.Rows).All(p => ReferenceEquals(p.First, p.Second)));

        // A collapsed class stays collapsed when the next pause's symbols come; a member added inserts its row alone.
        var collapsed = outline.Rows.First(r => r.Name == "Class5");
        outline.Toggle(collapsed);
        Assert.Equal(10_001, outline.Rows.Count);
        changes = 0;
        builds = outline.Builds;
        editor.Insights.ShowSymbols(Symbols(100, 100, extra: 1));
        await ShellFixture.PumpUntil(() => outline.Builds == builds + 1 && !outline.IsBuilding, describe: () => Describe(outline, builds));
        Assert.Equal(10_002, outline.Rows.Count);
        Assert.InRange(changes, 1, 2);
        Assert.Same(collapsed, outline.Rows.First(r => r.Name == "Class5"));
        Assert.False(collapsed.IsExpanded);
        outline.SetExpanded(collapsed, true);
        Assert.Equal(10_102, outline.Rows.Count);

        // The caret's symbol is the current row; the filter lists the matches flat, with their containers.
        editor.Editor.CaretOffset = editor.Model.Text.GetLineByNumber(5).Offset + 2; // in Class0's second method (its lines 4-5 from 0)
        await ShellFixture.PumpUntil(() => outline.Current is { Name: "Overload" }, describe: () => $"current {outline.Current?.Name}");
        Assert.Same(outline.Rows[3], outline.Current);
        outline.Filter = "Method99";
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 100 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        Assert.All(outline.Rows, r => Assert.Equal((0, "Method99"), (r.Depth, r.Name)));
        Assert.Equal("Class7", outline.Rows[7].Detail);
        outline.Filter = string.Empty;
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 10_102 && !outline.IsBuilding);
    }

    [AvaloniaFact]
    public async Task A_hidden_outline_builds_no_rows_and_catches_up_once_shown()
    {
        var (f, editor) = await OpenAsync();
        await using var fixture = f;
        await ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;
        editor.Insights.ShowSymbols(Symbols(10, 10));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 111 && !outline.IsBuilding);

        f.Shell.Layout.CloseToolWindow(DocumentOutlineViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => !outline.IsShown);
        var builds = outline.Builds;
        for (var i = 0; i < 5; i++)
        {
            editor.Insights.ShowSymbols(Symbols(10, 10 + i));
            ShellFixture.Pump();
        }

        Assert.Equal(builds, outline.Builds);

        await ShowOutlineAsync(f);
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 10 * (14 + 1) + 1 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        Assert.Equal(builds + 1, outline.Builds);
    }

    [AvaloniaFact]
    public async Task Pauses_in_typing_in_a_file_of_ten_thousand_symbols_never_hang_the_ui()
    {
        var (f, editor) = await OpenAsync();
        await using var fixture = f;
        await ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;
        editor.Insights.ShowSymbols(Symbols(100, 100));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 10_101 && !outline.IsBuilding);

        using var monitor = new UiStallMonitor();
        async Task FlowAsync()
        {
            // The symbols of each pause, a member added at times; then the outline's filter, typed.
            for (var i = 0; i < 6; i++)
            {
                var builds = outline.Builds;
                editor.Insights.ShowSymbols(Symbols(100, 100, extra: i % 2));
                await ShellFixture.PumpUntil(() => outline.Builds > builds && !outline.IsBuilding, describe: () => Describe(outline, builds));
            }

            foreach (var text in new[] { "M", "Me", "Method", "Method9", string.Empty })
            {
                outline.Filter = text;
                await ShellFixture.PumpUntil(() => !outline.IsBuilding);
            }
        }

        await FlowAsync(); // the JIT's
        monitor.Reset();
        await FlowAsync();
        monitor.AssertResponsive("the outline of 10,000 symbols at each pause in typing", 1000);
    }

    private static ListBox ListOf(ShellFixture f) =>
        f.Window.GetVisualDescendants().OfType<DocumentOutlineView>().First(v => v.IsEffectivelyVisible).FindControl<ListBox>("List")!;

    /// <summary>Clicks a control as the user does: the pointer down and up at its middle.</summary>
    private static void Click(ShellFixture f, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseDown(center, MouseButton.Left);
        f.Window.MouseUp(center, MouseButton.Left);
        ShellFixture.Pump();
    }

    private static void Press(ShellFixture f, Key key, PhysicalKey physical)
    {
        f.Window.KeyPress(key, RawInputModifiers.None, physical, null);
        ShellFixture.Pump();
    }

    [AvaloniaFact]
    public async Task The_chevron_toggles_a_row_a_click_or_enter_goes_to_its_symbol_and_left_and_right_fold()
    {
        var (f, editor) = await OpenAsync();
        await using var fixture = f;
        await ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;
        editor.Insights.ShowSymbols(Symbols(3, 3));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 13 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        ShellFixture.Pump();
        var list = ListOf(f);
        SymbolNodeViewModel Row(string name, int which = 0) => outline.Rows.Where(r => r.Name == name).ElementAt(which);
        Control Part(SymbolNodeViewModel row, Func<Control, bool> part)
        {
            list.ScrollIntoView(row);
            ShellFixture.Pump();
            return list.ContainerFromItem(row)!.GetVisualDescendants().OfType<Control>().First(part);
        }

        Position Caret() => editor.Model.PositionAt(editor.Editor.CaretOffset);

        // The chevron collapses and expands its row at each click (the second, soon after, was a double tap) and leaves
        // the caret; a click on the row's name goes to its symbol.
        var caret = Caret();
        var class1 = Row("Class1");
        Click(f, Part(class1, c => c is Border b && b.Classes.Contains("chevron")));
        Assert.False(class1.IsExpanded);
        Assert.Equal(10, outline.Rows.Count);
        Assert.Equal(caret, Caret());
        Click(f, Part(class1, c => c is Border b && b.Classes.Contains("chevron")));
        Assert.True(class1.IsExpanded);
        Assert.Equal(13, outline.Rows.Count);
        Assert.Equal(caret, Caret());
        var method = Row("Method2", 1); // Class1's
        Click(f, Part(method, c => c is TextBlock { Text: "Method2" }));
        Assert.Equal(method.Symbol.SelectionRange.Start, Caret());
        Assert.Equal(13, outline.Rows.Count);

        // From the keyboard: Left collapses a row, Right expands it, Left on a child goes to its parent, Enter to the symbol.
        void Select(SymbolNodeViewModel row)
        {
            list.SelectedItem = row;
            list.ScrollIntoView(row);
            ShellFixture.Pump();
            list.ContainerFromItem(row)!.Focus(NavigationMethod.Directional);
            ShellFixture.Pump();
        }

        var class2 = Row("Class2");
        Select(class2);
        Press(f, Key.Left, PhysicalKey.ArrowLeft);
        Assert.False(class2.IsExpanded);
        Assert.Equal(10, outline.Rows.Count);
        Press(f, Key.Right, PhysicalKey.ArrowRight);
        Assert.True(class2.IsExpanded);
        Assert.Equal(13, outline.Rows.Count);
        Select(Row("Overload", 2)); // Class1's first
        Press(f, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Same(class1, list.SelectedItem);
        Assert.True(class1.IsExpanded);
        Press(f, Key.Left, PhysicalKey.ArrowLeft);
        Assert.False(class1.IsExpanded);
        Press(f, Key.Left, PhysicalKey.ArrowLeft); // collapsed: to its parent, the namespace
        Assert.Same(Row("App"), list.SelectedItem);
        Select(class2);
        Press(f, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(class2.Symbol.SelectionRange.Start, Caret());

        // The collapsed row stays collapsed when the next pause's symbols come, a member added to another class.
        var builds = outline.Builds;
        editor.Insights.ShowSymbols(Symbols(3, 3, extra: 1));
        await ShellFixture.PumpUntil(() => outline.Builds == builds + 1 && !outline.IsBuilding, describe: () => Describe(outline, builds));
        Assert.Same(class1, Row("Class1"));
        Assert.False(class1.IsExpanded);
        Assert.Equal(11, outline.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Another_documents_rows_go_at_once_and_never_go_to_their_symbols_in_the_new_one()
    {
        var (f, large) = await OpenAsync();
        await using var fixture = f;
        await ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;
        large.Insights.ShowSymbols(Symbols(10, 10));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 111 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");

        var file = Path.Combine(f.UserData, "Small.txt");
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Range(0, 100).Select(i => $"small {i}")));
        var document = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => document?.Controller is not null && f.Shell.ActiveEditor == document);
        var small = document!.Controller!;
        small.Insights.ShowSymbols(Symbols(2, 2));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 7 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");

        // Back to the large document: its rows are flattened on the pool, and the small one's showed meanwhile and went to
        // their symbols' positions in the large one. They go at once; the same document's stay (they patch, no flicker).
        var old = outline.Rows[^1];
        var caret = large.Editor.CaretOffset;
        outline.SetEditor(large);
        Assert.Empty(outline.Rows);
        Assert.True(outline.HasMessage);
        Assert.Null(outline.Current);
        outline.Navigate(old);
        Assert.Equal(caret, large.Editor.CaretOffset);
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 111 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        Assert.False(outline.HasMessage);
    }
}
