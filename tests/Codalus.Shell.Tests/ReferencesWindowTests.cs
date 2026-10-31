using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Location = Codalus.LanguageServices.Protocol.Location;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// The References window (Find All References as the classic IDE shows it, 0.6): its groupings, scope, column filters,
/// search, List View, Expand and Collapse All, Copy, the resizable columns, and Keep Results' extra windows.
/// </summary>
public sealed class ReferencesWindowTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "refs");
    private static readonly string Program = Path.Combine(Root, "App", "Program.cs");
    private static readonly string Shapes = Path.Combine(Root, "Lib", "Shapes.cs");
    private static readonly string Tests = Path.Combine(Root, "Tests", "CircleTests.cs");

    private static ReferenceItem Item(string path, int line, int character, string kind, string project, string text, string definition, string? type = "Program") =>
        new(new Location(DocumentUris.FromPath(path), new Range(new Position(line - 1, character), new Position(line - 1, character + 6))), kind, type is null ? null : type + ".Main()", project, text)
        {
            Definition = definition,
            DefinitionKind = definition.Contains('(', StringComparison.Ordinal) ? SymbolKind.Constructor : SymbolKind.Class,
            ContainingType = type,
            TextStart = text.IndexOf("Circle", StringComparison.Ordinal),
        };

    private static IReadOnlyList<ReferenceItem> Items() =>
    [
        Item(Shapes, 20, 20, ReferenceItemKinds.Declaration, "Lib", "public sealed class Circle(double radius) : Shape", "Circle", "Circle"),
        Item(Program, 4, 17, ReferenceItemKinds.Reference, "App", "var circle = new Circle(2);", "Circle.Circle(double)"),
        Item(Program, 9, 17, ReferenceItemKinds.Reference, "App", "var other = new Circle(3);", "Circle.Circle(double)"),
        Item(Tests, 12, 20, ReferenceItemKinds.Reference, "Tests", "Assert.IsType<Circle>(shape);", "Circle", "CircleTests"),
    ];

    private static ReferencesViewModel Window(Func<string?>? active = null)
    {
        var opened = new List<(string, Position)>();
        var window = new ReferencesViewModel(ReferencesViewModel.ToolWindowId, (path, position, _) =>
        {
            opened.Add((path, position));
            return Task.CompletedTask;
        })
        {
            ActiveDocument = active,
            ActiveProject = () => "App",
            OpenDocuments = () => [Program, Tests],
            RootFolder = () => Root,
        };
        window.Show("Circle", Items());
        return window;
    }

    private static string[] Shown(ReferencesViewModel window) =>
        [.. window.Rows.Select(r => new string(' ', r.Depth * 2) + (r.IsHeader ? r.Label : $"{r.FileName}:{r.Line}"))];

    [Fact]
    public void Project_then_definition_is_the_default_tree_with_counts()
    {
        var window = Window();

        Assert.Equal("'Circle' references", window.Title);
        Assert.Equal("'Circle' references - Entire solution", window.Caption);
        Assert.Equal(
            ["App (2)", "  Circle.Circle(double) (2)", "    Program.cs:4", "    Program.cs:9",
             "Lib (1)", "  Circle (1)", "    Shapes.cs:20",
             "Tests (1)", "  Circle (1)", "    CircleTests.cs:12"],
            Shown(window));
        Assert.Equal(("project", "method@Build", "class@Open"), (window.Rows[0].HeaderIcon, window.Rows[1].HeaderIcon, window.Rows[5].HeaderIcon));
        Assert.True(window.Rows[1].IsDefinition);

        // A reference's columns, and its symbol highlighted in the code.
        var reference = window.Rows[2];
        Assert.Equal(("Program.cs", "4", "18", "App", "Program.Main()", "Program", "Reference"),
            (reference.FileName, reference.LineLabel, reference.ColumnLabel, reference.Project, reference.ContainingMember, reference.ContainingType, reference.KindLabel));
        Assert.Equal("Circle", string.Concat(reference.Highlights!.Select(i => reference.Text[i])));
    }

    [Fact]
    public void Every_grouping_nests_its_levels_in_order()
    {
        var window = Window();

        window.GroupBy = ReferencesGrouping.DefinitionOnly;
        Assert.Equal(["Circle (2)", "  Shapes.cs:20", "  CircleTests.cs:12", "Circle.Circle(double) (2)", "  Program.cs:4", "  Program.cs:9"], Shown(window));

        window.GroupBy = ReferencesGrouping.DefinitionThenProject;
        Assert.Equal(["Circle (2)", "  Lib (1)", "    Shapes.cs:20", "  Tests (1)", "    CircleTests.cs:12", "Circle.Circle(double) (2)", "  App (2)", "    Program.cs:4", "    Program.cs:9"], Shown(window));

        window.GroupBy = ReferencesGrouping.DefinitionThenPath;
        Assert.Equal(Path.Combine("App", "Program.cs") + " (2)", window.Rows[^3].Label);
        Assert.Equal(FileIcons.ForFile("Program.cs"), window.Rows[^3].HeaderIcon);

        window.GroupBy = ReferencesGrouping.ProjectThenDefinitionThenPath;
        Assert.Equal(["App (2)", "  Circle.Circle(double) (2)", $"    {Path.Combine("App", "Program.cs")} (2)", "      Program.cs:4", "      Program.cs:9"], Shown(window)[..5]);

        // List View: the references flat, by file and line.
        window.IsListView = true;
        Assert.False(window.CanGroup);
        Assert.Equal(["Program.cs:4", "Program.cs:9", "Shapes.cs:20", "CircleTests.cs:12"], Shown(window));

        Assert.Equal(ReferencesGrouping.DefinitionThenPath, ReferencesGrouping.FromId(ReferencesGrouping.ToId(ReferencesGrouping.DefinitionThenPath)));
        Assert.Equal(ReferencesGrouping.ProjectThenDefinition, ReferencesGrouping.FromId("nonsense"));
    }

    [Fact]
    public void The_scope_filters_without_searching_again()
    {
        var window = Window(() => Shapes);

        window.Scope = ReferencesScope.CurrentDocument;
        Assert.Equal(["Lib (1)", "  Circle (1)", "    Shapes.cs:20"], Shown(window));
        Assert.Equal("'Circle' references - Current document", window.Caption);

        window.Scope = ReferencesScope.CurrentProject;
        Assert.Equal(2, window.Rows.Count(r => !r.IsHeader));
        Assert.All(window.Rows.Where(r => !r.IsHeader), r => Assert.Equal("App", r.Project));

        window.Scope = ReferencesScope.OpenDocuments;
        Assert.Equal(["Program.cs", "Program.cs", "CircleTests.cs"], window.Rows.Where(r => !r.IsHeader).Select(r => r.FileName));
        Assert.Equal("3 of 4 result(s) shown", window.Summary);

        window.Scope = ReferencesScope.EntireSolution;
        Assert.Equal("4 result(s)", window.Summary);
    }

    [Fact]
    public void Column_filters_and_the_search_hide_references_until_cleared()
    {
        var window = Window();
        Assert.False(window.HasFilters);
        Assert.False(window.ClearFiltersCommand.CanExecute(null));

        // A column's filter lists its values with their counts; unchecking one hides its references.
        Assert.Equal(["App (2)", "Lib (1)", "Tests (1)"], window.ProjectFilter.Values.Select(v => v.Display));
        window.ProjectFilter.Values.Single(v => v.Value == "App").IsChecked = false;
        Assert.True(window.ProjectFilter.IsActive);
        Assert.True(window.ClearFiltersCommand.CanExecute(null));
        Assert.Equal(2, window.Rows.Count(r => !r.IsHeader));

        window.KindFilter.ClearCommand.Execute(null);
        Assert.Empty(window.Rows);
        window.KindFilter.SelectAllCommand.Execute(null);

        // The search looks in the code, file, project, member, type and definition.
        window.Filter = "IsType";
        Assert.Equal(["Tests (1)", "  Circle (1)", "    CircleTests.cs:12"], Shown(window));

        window.ClearFiltersCommand.Execute(null);
        Assert.Equal(string.Empty, window.Filter);
        Assert.False(window.ProjectFilter.IsActive);
        Assert.Equal(4, window.Rows.Count(r => !r.IsHeader));

        // A new search keeps what a filter hides when the value is there again.
        window.KindFilter.Values.Single(v => v.Value == "Declaration").IsChecked = false;
        window.Show("Circle", Items());
        Assert.False(window.KindFilter.Values.Single(v => v.Value == "Declaration").IsChecked);
        Assert.Equal(3, window.Rows.Count(r => !r.IsHeader));
    }

    [Fact]
    public void Expand_and_collapse_all_reach_every_level()
    {
        var window = Window();

        window.CollapseAllCommand.Execute(null);
        Assert.Equal(["App (2)", "Lib (1)", "Tests (1)"], Shown(window));

        window.SetExpanded(window.Rows[0], true);
        Assert.Equal(["App (2)", "  Circle.Circle(double) (2)", "Lib (1)", "Tests (1)"], Shown(window));

        window.ExpandAllCommand.Execute(null);
        Assert.Equal(10, window.Rows.Count);
    }

    [Fact]
    public async Task Copy_walking_and_opening_follow_the_rows_shown()
    {
        var window = Window();

        // Copy: a reference's columns tab-separated; a header its group, indented; nothing selected, every row.
        window.SelectedRow = window.Rows[2];
        Assert.Equal("var circle = new Circle(2);\tProgram.cs\t4\t18\tApp\tProgram.Main()\tProgram\tReference", window.CopyText());
        window.SelectedRow = window.Rows[4];
        Assert.Equal(["Lib (1)", "  Circle (1)", "public sealed class Circle(double radius) : Shape\tShapes.cs\t20\t21\tLib\tCircle.Main()\tCircle\tDeclaration"],
            window.CopyText().Split(Environment.NewLine));

        // F8 goes to the next reference after the selection (headers skipped), wrapping around.
        await window.GoToAsync(1);
        Assert.Equal("Shapes.cs:20", $"{window.SelectedRow!.FileName}:{window.SelectedRow.Line}");
        await window.GoToAsync(1);
        await window.GoToAsync(1);
        Assert.Equal("Program.cs:4", $"{window.SelectedRow!.FileName}:{window.SelectedRow.Line}");
    }

    /// <summary>A logging call used everywhere: 20,000 references in 2,000 files of 40 projects, in 5,000 members.</summary>
    private static IReadOnlyList<ReferenceItem> Many() =>
    [
        .. Enumerable.Range(0, 20_000).Select(i => new ReferenceItem(
            new Location(DocumentUris.FromPath(Path.Combine(Root, $"P{i % 40}", $"F{i % 2000:0000}.cs")), new Range(new Position(i / 2000, 4), new Position(i / 2000, 7))),
            i % 10 == 0 ? ReferenceItemKinds.Write : ReferenceItemKinds.Read,
            $"T{i % 500}.M{i % 5000}()",
            $"P{i % 40}",
            $"Log.Write(\"{i}\");")
        {
            Definition = "Log.Write(string)",
            DefinitionKind = SymbolKind.Method,
            ContainingType = $"T{i % 500}",
            TextStart = 4,
        }),
    ];

    [Fact]
    public void Twenty_thousand_references_show_and_filter_with_a_change_or_two_each()
    {
        var window = Window();
        var changes = 0;
        window.Rows.CollectionChanged += (_, _) => changes++;

        // Shown at once: a change for each row paused the window for every widely used symbol (M56).
        window.Show("Write", Many());
        Assert.InRange(changes, 1, 2);
        Assert.Equal(20_000, window.Rows.Count(r => !r.IsHeader));

        // Each keystroke in the search changes the rows at once, and the selected reference stays selected.
        window.SelectedRow = window.Rows.First(r => r.Text == "Log.Write(\"1234\");");
        foreach (var text in new[] { "1", "12", "123", "1234", "123", string.Empty })
        {
            changes = 0;
            window.Filter = text;
            Assert.InRange(changes, 0, 2);
            Assert.Equal("Log.Write(\"1234\");", window.SelectedRow?.Text);
        }

        Assert.Equal(20_000, window.Rows.Count(r => !r.IsHeader));

        // A group collapsed and expanded again: a change each, the header selected meanwhile.
        var header = window.Rows.First(r => r.IsHeader && r.Label.StartsWith("P1 ", StringComparison.Ordinal));
        changes = 0;
        window.Toggle(header);
        window.Toggle(window.Rows.First(r => r.GroupKey == header.GroupKey));
        Assert.InRange(changes, 2, 4);
        Assert.Equal(20_000, window.Rows.Count(r => !r.IsHeader));
    }

    [Fact]
    public void Find_results_of_a_large_replace_all_show_and_filter_with_a_change_or_two_each()
    {
        var results = new FindResultsViewModel((_, _) => Task.FromResult(new FilesToSearch([], new Dictionary<string, string>())), (_, _, _) => Task.CompletedTask);
        var page = results.CurrentPage;
        var changes = 0;
        page.Rows.CollectionChanged += (_, _) => changes++;

        // Replace All changed 100,000 lines in 2,000 files: they were added a row at a time (M56).
        var rows = Enumerable.Range(0, 100_000).Select(i => new FindResultRow(Path.Combine(Root, $"F{i / 50:0000}.cs"), (i % 50) + 1, 0, 5, $"after{i}", false)).ToList();
        results.ShowReplaced(new FindInFilesOptions("before"), "after", rows, 100_000, 2_000);
        Assert.InRange(changes, 1, 2);
        Assert.Equal(102_000, page.Rows.Count);

        page.SelectedRow = page.Rows.First(r => r.Code == "after4321");
        foreach (var text in new[] { "4", "43", "432", "4321", string.Empty })
        {
            changes = 0;
            page.Filter = text;
            Assert.InRange(changes, 0, 2);
            Assert.Equal("after4321", page.SelectedRow?.Code);
        }
    }

    [AvaloniaFact]
    public async Task A_column_filter_of_thousands_of_values_creates_only_the_check_boxes_in_view()
    {
        await using var f = ShellFixture.Create();
        var window = f.Shell.ShowReferences("Write", Many());
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ReferencesView>().Any(v => v.IsEffectivelyVisible && v.DataContext == window));
        var view = f.Window.GetVisualDescendants().OfType<ReferencesView>().First(v => v.DataContext == window);
        Assert.Equal(5_000, window.MemberFilter.Values.Count);

        // The Containing member column's filter: a check box for each of 5,000 members took a second to open (M56).
        var button = view.FindControl<ColumnsPanel>("Header")!.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == "ContainingMember");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => view.FilterFlyout is { IsOpen: true });
        var list = ((Control)view.FilterFlyout!.Content!).GetVisualDescendants().OfType<ListBox>().Single();
        await ShellFixture.PumpUntil(() => list.GetRealizedContainers().Any());
        ShellFixture.Pump();
        var boxes = list.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.InRange(boxes.Count, 1, 100);

        // The check boxes are the values': unchecking one hides its references, Clear and Select All check them all.
        boxes.Single(b => b.DataContext == window.MemberFilter.Values[0]).IsChecked = false;
        Assert.False(window.MemberFilter.Values[0].IsChecked);
        Assert.Equal(20_000 - 4, window.Rows.Count(r => !r.IsHeader));
        window.MemberFilter.ClearCommand.Execute(null);
        Assert.All(list.GetVisualDescendants().OfType<CheckBox>(), b => Assert.False(b.IsChecked));
        Assert.Empty(window.Rows);
        window.MemberFilter.SelectAllCommand.Execute(null);
        Assert.All(list.GetVisualDescendants().OfType<CheckBox>(), b => Assert.True(b.IsChecked));
        Assert.Equal(20_000, window.Rows.Count(r => !r.IsHeader));
        view.FilterFlyout.Hide();
    }

    [Fact]
    public async Task Plain_locations_read_their_lines_and_projects_off_the_ui_thread_from_one_index()
    {
        // 300 projects of 170 items: each location looked through every item of every project (M56).
        var root = Path.Combine(Path.GetTempPath(), "codalus-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Codalus.Projects.Protocol.ProjectInfo Project(int p) => new(
                Path.Combine(root, $"P{p}", $"P{p}.fsproj"), $"P{p}", Codalus.Projects.Protocol.ProjectStates.Loaded, null, ["net10.0"], "Library",
                [.. Enumerable.Range(0, 170).Select(i => new Codalus.Projects.Protocol.ProjectItemInfo("Compile", Path.Combine(root, $"P{p}", $"F{i}.fs"), null, null))], [], [], Restored: true);
            var projects = Enumerable.Range(0, 300).Select(Project).ToList();
            var paths = Enumerable.Range(0, 1_000).Select(i => Path.Combine(root, $"P{i % 300}", $"F{i % 170}.fs")).Distinct().ToList();
            foreach (var path in paths.Skip(1))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "module M\nlet greet name = name\n", TestContext.Current.CancellationToken);
            }

            var open = new Dictionary<string, string> { [paths[0]] = "module Open\nlet unsaved = 1\n" };
            var (lines, names) = await ShellHost.ReadReferencedFilesAsync(paths, open, projects);

            Assert.Equal(paths.Count, lines.Count);
            Assert.Equal("let unsaved = 1", lines[paths[0]][1]);
            Assert.Equal("let greet name = name", lines[paths[^1]][1]);
            foreach (var path in paths.Take(50))
            {
                Assert.Equal(ErrorListViewModel.ProjectOf(projects, path)?.Name, names[path]);
            }

            Assert.Null((await ShellHost.ReadReferencedFilesAsync([Path.Combine(root, "Elsewhere.fs")], open, projects)).Projects[Path.Combine(root, "Elsewhere.fs")]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plain_locations_group_under_the_symbol_with_their_lines()
    {
        var window = new ReferencesViewModel(ReferencesViewModel.ToolWindowId, (_, _, _) => Task.CompletedTask);
        var location = new Location(DocumentUris.FromPath(Program), new Range(new Position(3, 8), new Position(3, 14)));

        window.ShowLocations("greet", [location], (_, _) => "    let greet name = name", _ => "App.FSharp");

        Assert.Equal(["App.FSharp (1)", "  greet (1)", "    Program.cs:4"], Shown(window));
        var row = window.Rows[2];
        Assert.Equal(("let greet name = name", "Reference"), (row.Text, row.KindLabel));
        Assert.Equal("greet ", string.Concat(row.Highlights!.Select(i => row.Text[i])));
    }

    [Fact]
    public void Column_widths_resize_from_their_splitters_and_round_trip_through_the_setting()
    {
        var window = Window();
        Assert.Equal(ReferencesViewModel.DefaultWidths, window.Widths);

        // Code's splitter narrows File (Code takes the rest); another's widens its own column.
        window.Resize(0, 30);
        Assert.Equal(110, window.Widths[1]);
        window.Resize(4, 25);
        Assert.Equal(165, window.Widths[4]);
        window.Resize(2, -500);
        Assert.Equal(24, window.Widths[2]);

        IReadOnlyList<double>? saved = null;
        window.WidthsChanged = widths => saved = widths;
        window.CommitWidths();
        var text = ReferencesViewModel.FormatWidths(saved!);
        Assert.Equal("110,24,40,165,180,140,80", text);

        var restored = Window();
        restored.LoadWidths(text);
        Assert.Equal(window.Widths, restored.Widths);
        restored.LoadWidths("1,2,x");
        Assert.Equal(window.Widths, restored.Widths); // a malformed value is ignored

        // The columns panel: Code takes what the others leave, down to its minimum.
        var columns = ColumnsPanel.Layout(window.Widths, 1000);
        Assert.Equal(1000 - window.Widths.Skip(1).Sum(), columns[0].Width);
        Assert.Equal((columns[0].Width, 110.0), (columns[1].Left, columns[1].Width));
        // Too narrow for them all: Code keeps its minimum and the others shrink in proportion, so none runs past the edge.
        var narrow = ColumnsPanel.Layout(window.Widths, 300);
        Assert.Equal(ColumnsPanel.MinFirstWidth, narrow[0].Width);
        Assert.Equal(300, narrow[^1].Left + narrow[^1].Width, 3);
        Assert.Equal(window.Widths[4] / window.Widths[1], narrow[4].Width / narrow[1].Width, 3);
        Assert.Equal(80, ColumnsPanel.Layout(window.Widths, 200)[0].Width, 3);
    }

    [AvaloniaFact]
    public async Task The_window_shows_its_columns_aligned_and_keep_results_opens_another()
    {
        await using var f = ShellFixture.Create();
        var window = f.Shell.ShowReferences("Circle", Items());
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ReferencesView>().Any(v => v.IsEffectivelyVisible && v.DataContext == window));
        ShellFixture.Pump();
        var view = f.Window.GetVisualDescendants().OfType<ReferencesView>().First(v => v.DataContext == window);
        Assert.Equal("'Circle' references", f.Shell.Layout.ToolWindow(ReferencesViewModel.ToolWindowId)!.Title);

        // A reference's File cell starts where the File header does.
        var header = view.FindControl<ColumnsPanel>("Header")!;
        var list = view.FindControl<ListBox>("List")!;
        var row = list.GetVisualDescendants().OfType<ColumnsPanel>().First(p => p.DataContext is ReferenceRow { IsHeader: false });
        double Left(Control cell) => cell.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value.X;
        var fileHeader = header.Children.First(c => ColumnsPanel.GetColumn(c) == 1).GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("columnTitle"));
        var fileCell = row.Children.First(c => ColumnsPanel.GetColumn(c) == 1);
        Assert.True(Math.Abs(Left(fileHeader) - Left(fileCell)) <= 1, $"File header at {Left(fileHeader)}, its cell at {Left(fileCell)}");
        NavigationDepthTests.Screenshot(f, "references-window");

        // Dragging a splitter resizes the column in every row; letting go saves the widths.
        var thumb = header.GetVisualDescendants().OfType<Thumb>().First(t => (string?)t.Tag == "4");
        thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Avalonia.Vector(20, 0) });
        thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = default });
        Assert.Equal(ReferencesViewModel.DefaultWidths[4] + 20, window.Widths[4]);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ReferencesViewModel.ColumnWidthsSetting, string.Empty).Length > 0);

        // Keep Results: the next references open in another window beside this one, which the layout doesn't restore.
        window.KeepResults = true;
        var second = f.Shell.ShowReferences("Shape", Items());
        Assert.NotSame(window, second);
        Assert.Equal("references:2", second.Id);
        Assert.True(f.Shell.Layout.IsVisible(second.Id));
        Assert.Null(f.Shell.Workbench.ToolWindows.Find(second.Id));
        Assert.Equal("'Circle' references", window.Title);
        f.Shell.Layout.CloseToolWindow(second.Id);
        Assert.Equal([window], f.Shell.ReferenceWindows);
    }
}
