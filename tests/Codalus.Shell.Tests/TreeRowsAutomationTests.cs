using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The virtualized trees as a screen reader sees them (M57, the maintainer's "proper peer for all trees"): each list of a
/// flattened tree's rows (<see cref="TreeRowsList"/>) is a tree, each row a tree item whose folder has the ExpandCollapse
/// pattern (its Expand and Collapse are the view's own), with its level and its place among its siblings, and a name
/// without ", expanded" or ", collapsed" in it (M56 put the state there; macOS keeps it, for VoiceOver). The shared piece
/// as the list scrolls, the Solution Explorer, the Document Outline, the Test Explorer and Coverage Results here; Git
/// Changes, the Git Repository tree, a commit's changes and Compare in their own tests.
/// </summary>
public sealed class TreeRowsAutomationTests
{
    /// <summary>The tree item of a row whose control is realized.</summary>
    internal static TreeRowItemAutomationPeer PeerOf(ListBox list, object row) =>
        Assert.IsType<TreeRowItemAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(Assert.IsAssignableFrom<Control>(list.ContainerFromItem(row))));

    /// <summary>A row's tree item: its role, level, place among its siblings and expansion (null: a leaf, with no pattern).</summary>
    internal static void AssertTreeItem(ListBox list, object row, int level, int position, int size, ExpandCollapseState state)
    {
        Assert.Equal(AutomationControlType.Tree, ControlAutomationPeer.CreatePeerForElement(list).GetAutomationControlType());
        var peer = PeerOf(list, row);
        Assert.Equal(AutomationControlType.TreeItem, peer.GetAutomationControlType());
        Assert.Equal((level, position, size), (peer.Level, peer.PositionInSet, peer.SizeOfSet));
        Assert.Equal(state, peer.ExpandCollapseState);
        Assert.Equal(state != ExpandCollapseState.LeafNode, peer.GetProvider<IExpandCollapseProvider>() is not null);
        var name = peer.GetName();
        Assert.False(string.IsNullOrEmpty(name));
        if (list is TreeRowsList { NamesCarryState: true } && state != ExpandCollapseState.LeafNode)
        {
            Assert.EndsWith(state == ExpandCollapseState.Expanded ? ", expanded" : ", collapsed", name, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(", expanded", name, StringComparison.Ordinal);
            Assert.DoesNotContain(", collapsed", name, StringComparison.Ordinal);
        }
    }

    /// <summary>A row's tree item's name: its name, and on macOS a folder's state (<see cref="TreeRowsList.NamesCarryState"/>).</summary>
    internal static string NameOf(ListBox list, string name, ExpandCollapseState state) =>
        list is not TreeRowsList { NamesCarryState: true } ? name
        : state switch
        {
            ExpandCollapseState.Expanded => name + ", expanded",
            ExpandCollapseState.Collapsed => name + ", collapsed",
            _ => name,
        };

    /// <summary>The place a row's control carries for AT-SPI (<c>posinset</c>, <c>setsize</c>).</summary>
    private static void AssertPlace(Control item, int position, int size) =>
        Assert.Equal((position, size), (AutomationProperties.GetPositionInSet(item), AutomationProperties.GetSizeOfSet(item)));

    /// <summary>A screen reader's Expand or Collapse on a row's tree item, as UI Automation, NSAccessibility and AT-SPI call it.</summary>
    internal static void Invoke(ListBox list, object row, bool expand)
    {
        var provider = PeerOf(list, row).GetProvider<IExpandCollapseProvider>();
        Assert.NotNull(provider);
        if (expand)
        {
            provider.Expand();
        }
        else
        {
            provider.Collapse();
        }

        ShellFixture.Pump();
    }

    /// <summary>A row of a small tree, as the views' rows: its depth, its children, and its expansion, which it announces.</summary>
    private sealed class Row(string name, int depth, params Row[] children) : ITreeRow, INotifyPropertyChanged
    {
        private bool _expanded;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;

        public int Depth { get; } = depth;

        public List<Row> Children { get; } = [.. children];

        public bool HasChildren => Children.Count > 0;

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// The shared piece on its own: a <see cref="TreeRowsList"/> of a small tree whose owner inserts and removes the rows
    /// under a folder, as the views' owners do. Its rows keep the list's look (a <c>ListBoxItem</c> style applies to them),
    /// its peers say what each row is and where, a folder's Expand and Collapse go to the owner and are announced, and a
    /// sibling's removal moves the places the rows carry for AT-SPI.
    /// </summary>
    [AvaloniaFact]
    public void A_tree_rows_list_is_a_tree_of_items_with_their_level_place_and_expansion()
    {
        // A (a1, a2 (a2x), a3), B, C (c1): A expanded.
        var a2 = new Row("a2", 1, new Row("a2x", 2));
        var a = new Row("A", 0, new Row("a1", 1), a2, new Row("a3", 1)) { IsExpanded = true };
        var rows = new ObservableCollection<Row>();
        void Add(Row row, int at)
        {
            rows.Insert(at, row);
            if (row.IsExpanded)
            {
                foreach (var child in row.Children)
                {
                    Add(child, ++at);
                }
            }
        }

        foreach (var root in new[] { a, new Row("B", 0), new Row("C", 0, new Row("c1", 1)) })
        {
            Add(root, rows.Count);
        }

        var calls = new List<(string, bool)>();
        var list = new TreeRowsList
        {
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<Row>((row, _) => new TextBlock { Text = row?.Name }),
            ExpandRow = (row, expanded) =>
            {
                var folder = (Row)row;
                calls.Add((folder.Name, expanded));
                var index = rows.IndexOf(folder);
                folder.IsExpanded = expanded;
                if (expanded)
                {
                    var at = index;
                    foreach (var child in folder.Children)
                    {
                        Add(child, ++at);
                    }
                }
                else
                {
                    while (index + 1 < rows.Count && rows[index + 1].Depth > folder.Depth)
                    {
                        rows.RemoveAt(index + 1);
                    }
                }
            },
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 31.0),
                new Setter(AutomationProperties.NameProperty, new Binding(nameof(Row.Name))),
            },
        });
        var window = new Window { Content = list, Width = 300, Height = 400 };
        window.Show();
        try
        {
            ShellFixture.Pump();
            Row At(string name) => rows.Single(r => r.Name == name);

            // The list's look: its rows are ListBoxItems to the styles and the theme.
            var item = Assert.IsType<TreeRowItem>(list.ContainerFromItem(At("a1")));
            Assert.Equal(31.0, item.MinHeight);
            Assert.NotNull(item.Template);

            // What each row is and where: A's children 1 to 3 of 3 at level 2; B the second root of 3.
            AssertTreeItem(list, At("A"), level: 1, position: 1, size: 3, ExpandCollapseState.Expanded);
            AssertTreeItem(list, At("a1"), level: 2, position: 1, size: 3, ExpandCollapseState.LeafNode);
            AssertTreeItem(list, At("a2"), level: 2, position: 2, size: 3, ExpandCollapseState.Collapsed);
            AssertTreeItem(list, At("a3"), level: 2, position: 3, size: 3, ExpandCollapseState.LeafNode);
            AssertTreeItem(list, At("B"), level: 1, position: 2, size: 3, ExpandCollapseState.LeafNode);
            AssertTreeItem(list, At("C"), level: 1, position: 3, size: 3, ExpandCollapseState.Collapsed);
            Assert.Equal(NameOf(list, "a2", ExpandCollapseState.Collapsed), PeerOf(list, At("a2")).GetName());

            // AT-SPI reads a row's place from its control: once a screen reader has asked for a row, the rows in view carry it.
            AssertPlace(list.ContainerFromItem(At("a3"))!, 3, 3);

            // Expand goes to the owner, as the keys' command does; the new state is announced.
            var announced = new List<(ExpandCollapseState Old, ExpandCollapseState New)>();
            PeerOf(list, a2).PropertyChanged += (_, e) =>
            {
                if (e.Property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty)
                {
                    announced.Add(((ExpandCollapseState)e.OldValue!, (ExpandCollapseState)e.NewValue!));
                }
            };
            Invoke(list, a2, expand: true);
            Assert.Equal([("a2", true)], calls);
            Assert.Equal([(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded)], announced);
            Assert.Equal(["A", "a1", "a2", "a2x", "a3", "B", "C"], rows.Select(r => r.Name));
            AssertTreeItem(list, a2, level: 2, position: 2, size: 3, ExpandCollapseState.Expanded);
            AssertTreeItem(list, At("a2x"), level: 3, position: 1, size: 1, ExpandCollapseState.LeafNode);
            AssertTreeItem(list, At("a3"), level: 2, position: 3, size: 3, ExpandCollapseState.LeafNode);

            // Expand on an expanded row asks nothing; Collapse goes to the owner too.
            Invoke(list, a2, expand: true);
            Assert.Single(calls);
            Invoke(list, a2, expand: false);
            Assert.Equal(("a2", false), calls[^1]);
            Assert.Equal((ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed), announced[^1]);
            AssertTreeItem(list, a2, level: 2, position: 2, size: 3, ExpandCollapseState.Collapsed);

            // A sibling removed: the places move, on the peers and on the controls, before a screen reader's next call
            // (Avalonia's AT-SPI answers at Default priority: the jobs run here are what runs before it). The rows after it
            // move up; the rows before it stay where they are, but their siblings are fewer.
            ShellFixture.Pump();
            a.Children.RemoveAt(0);
            rows.Remove(At("a1"));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
            AssertTreeItem(list, At("a3"), level: 2, position: 2, size: 2, ExpandCollapseState.LeafNode);
            AssertPlace(list.ContainerFromItem(a2)!, 1, 2);
            AssertPlace(list.ContainerFromItem(At("a3"))!, 2, 2);
            a.Children.RemoveAt(1);
            rows.Remove(At("a3"));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
            AssertPlace(list.ContainerFromItem(a2)!, 1, 1);
            AssertPlace(list.ContainerFromItem(At("B"))!, 2, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Solution Explorer's rows (a folder's view of a plain folder, so no build host loads anything): tree items with
    /// their level and place, whose Expand and Collapse are the explorer's own (the keys' <c>SetExpanded</c>).
    /// </summary>
    [AvaloniaFact]
    public async Task Solution_explorer_rows_are_tree_items_that_expand_and_collapse_as_its_keys_do()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "Plain");
        foreach (var name in new[] { "docs", "src", "tests" })
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
            await File.WriteAllTextAsync(Path.Combine(folder, name, "README.md"), "#\n", TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "src", $"F{i}.cs"), "class F;\n", TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "notes\n", TestContext.Current.CancellationToken);

        // The workspace writes its state in .codalus at some point after it opens; made now, the folder is in the first
        // listing, so the root's children (and each row's place among them) don't change under the assertions below
        // (local run 4 of 5, M57: .codalus came in mid-test and every place after it moved by one).
        Directory.CreateDirectory(Path.Combine(folder, ".codalus"));
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderWorkspaceAsync(folder);
        var explorer = f.Shell.Explorer;
        await ShellFixture.PumpUntil(() => explorer.IsFolderView && explorer.Rows.Any(r => r.Name == "notes.txt"), seconds: 60, describe: () => string.Join(", ", explorer.Rows.Select(r => r.Name)));
        f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Any(v => v.IsEffectivelyVisible));
        var list = f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().First(v => v.IsEffectivelyVisible).FindControl<TreeRowsList>("List")!;
        ExplorerRowViewModel Row(string name) => explorer.Rows.First(r => r.Name == name);
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(Row("notes.txt")) is not null);

        // The folder's row, and its children: docs, src and tests (folders first), then notes.txt.
        var children = explorer.Rows.Where(r => r.Depth == 1).ToList();
        Assert.Equal(["docs", "src", "tests", "notes.txt"], children.Select(r => r.Name).Where(n => n != ".codalus"));
        AssertTreeItem(list, explorer.Rows[0], level: 1, position: 1, size: 1, ExpandCollapseState.Expanded);
        AssertTreeItem(list, Row("src"), level: 2, position: children.IndexOf(Row("src")) + 1, size: children.Count, ExpandCollapseState.Collapsed);
        AssertTreeItem(list, Row("notes.txt"), level: 2, position: children.Count, size: children.Count, ExpandCollapseState.LeafNode);
        Assert.Equal(NameOf(list, "src", ExpandCollapseState.Collapsed), PeerOf(list, Row("src")).GetName());

        // Expand shows src's rows, as Right does; Collapse hides them, as Left does.
        Invoke(list, Row("src"), expand: true);
        Assert.True(Row("src").IsExpanded);
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(Row("F1.cs")) is not null);
        AssertTreeItem(list, Row("src"), level: 2, position: children.IndexOf(Row("src")) + 1, size: children.Count, ExpandCollapseState.Expanded);
        AssertTreeItem(list, Row("F1.cs"), level: 3, position: 2, size: 4, ExpandCollapseState.LeafNode); // F0, F1, F2, README.md
        Invoke(list, Row("src"), expand: false);
        Assert.False(Row("src").IsExpanded);
        Assert.DoesNotContain(explorer.Rows, r => r.Name == "F1.cs");
        AssertTreeItem(list, Row("src"), level: 2, position: children.IndexOf(Row("src")) + 1, size: children.Count, ExpandCollapseState.Collapsed);
    }

    /// <summary>The Document Outline's rows: a symbol's tree item, whose Expand and Collapse are the outline's own.</summary>
    [AvaloniaFact]
    public async Task Document_outline_rows_are_tree_items_that_expand_and_collapse_as_its_keys_do()
    {
        var (f, editor) = await DocumentOutlineTests.OpenAsync();
        await using var fixture = f;
        await DocumentOutlineTests.ShowOutlineAsync(f);
        var outline = f.Shell.DocumentOutline;
        editor.Insights.ShowSymbols(DocumentOutlineTests.Symbols(3, 4));
        await ShellFixture.PumpUntil(() => outline.Rows.Count == 16 && !outline.IsBuilding, describe: () => $"{outline.Rows.Count} rows");
        var list = f.Window.GetVisualDescendants().OfType<DocumentOutlineView>().First(v => v.IsEffectivelyVisible).FindControl<TreeRowsList>("List")!;
        var class1 = outline.Rows.Single(r => r.Name == "Class1");
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(class1) is not null);

        // App, its 3 classes, their 4 methods each.
        AssertTreeItem(list, outline.Rows[0], level: 1, position: 1, size: 1, ExpandCollapseState.Expanded);
        AssertTreeItem(list, class1, level: 2, position: 2, size: 3, ExpandCollapseState.Expanded);
        var method = outline.Rows[outline.Rows.IndexOf(class1) + 3];
        AssertTreeItem(list, method, level: 3, position: 3, size: 4, ExpandCollapseState.LeafNode);
        Assert.Equal(NameOf(list, "Class Class1", ExpandCollapseState.Expanded), PeerOf(list, class1).GetName());

        Invoke(list, class1, expand: false);
        Assert.False(class1.IsExpanded);
        Assert.Equal(12, outline.Rows.Count);
        AssertTreeItem(list, class1, level: 2, position: 2, size: 3, ExpandCollapseState.Collapsed);
        Invoke(list, class1, expand: true);
        Assert.True(class1.IsExpanded);
        Assert.Equal(16, outline.Rows.Count);
    }

    /// <summary>A list of <paramref name="rows"/> in a window of <paramref name="height"/>, each row's name its tree item's.</summary>
    private static (TreeRowsList List, Window Window) Show(ObservableCollection<Row> rows, double height)
    {
        var list = new TreeRowsList
        {
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<Row>((row, _) => new TextBlock { Text = row?.Name }),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 20.0),
                new Setter(AutomationProperties.NameProperty, new Binding(nameof(Row.Name))),
            },
        });
        var window = new Window { Content = list, Width = 300, Height = height };
        window.Show();
        ShellFixture.Pump();
        return (list, window);
    }

    /// <summary>
    /// The list scrolls under a screen reader (M57 review): a recycled container shows another row, and carries that row's
    /// place for AT-SPI before anything a screen reader's call could read (Avalonia's AT-SPI answers at Default priority;
    /// the place was set at Background, and Orca could say "4 of 9" for a root 5 of 40), and its peer announces no change
    /// of expansion for it (no row was expanded or collapsed); the row it shows now is still followed.
    /// </summary>
    [AvaloniaFact]
    public void Recycled_rows_carry_their_own_place_and_announce_no_expansion_as_the_list_scrolls()
    {
        // 40 roots, expanded, with 9 or 4 children each, every other one a collapsed folder: 300 rows, about 20 in view.
        var rows = new ObservableCollection<Row>();
        for (var g = 0; g < 40; g++)
        {
            var children = Enumerable.Range(0, g % 2 == 0 ? 9 : 4)
                .Select(c => c % 2 == 0 ? new Row($"G{g}c{c}", 1, new Row($"G{g}c{c}x", 2)) : new Row($"G{g}c{c}", 1))
                .ToArray();
            rows.Add(new Row($"G{g}", 0, children) { IsExpanded = true });
            foreach (var child in children)
            {
                rows.Add(child);
            }
        }

        var (list, window) = Show(rows, height: 400);
        try
        {
            // A screen reader reads the rows in view: each container gets its peer, which follows its row.
            var announced = new List<string>();
            var peers = new HashSet<TreeRowItemAutomationPeer>();
            void Read()
            {
                foreach (var container in list.GetRealizedContainers())
                {
                    var peer = Assert.IsType<TreeRowItemAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(container));
                    if (peers.Add(peer))
                    {
                        peer.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty)
                            {
                                announced.Add($"{(container.DataContext as Row)?.Name}: {e.OldValue} -> {e.NewValue}");
                            }
                        };
                    }
                }
            }

            Read();
            ShellFixture.Pump();
            var recycled = 0;
            for (var step = 1; step * 12 < rows.Count; step++)
            {
                var before = list.GetRealizedContainers().ToDictionary(c => c, c => c.DataContext);
                list.ScrollIntoView(step * 12);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Input); // layout, and what runs before a screen reader's call
                foreach (var container in list.GetRealizedContainers())
                {
                    var index = list.IndexFromContainer(container);
                    var (_, position, size) = list.PlaceOf(index);
                    var carried = (AutomationProperties.GetPositionInSet(container), AutomationProperties.GetSizeOfSet(container));
                    Assert.True((position, size) == carried, $"step {step}: {rows[index].Name} is {position} of {size}, its control says {carried.Item1} of {carried.Item2}");
                    if (before.TryGetValue(container, out var old) && !ReferenceEquals(old, container.DataContext))
                    {
                        recycled++;
                    }
                }

                Read();
            }

            Assert.True(recycled > 20, $"{recycled} containers recycled");
            Assert.Empty(announced);

            // A recycled container's peer follows the row it shows now: that row's expansion is announced.
            var folder = list.GetRealizedContainers()
                .Where(c => c.DataContext is Row { HasChildren: true, IsExpanded: false } && peers.Contains((TreeRowItemAutomationPeer)ControlAutomationPeer.CreatePeerForElement(c)))
                .Select(c => (Row)c.DataContext!)
                .First();
            folder.IsExpanded = true;
            Assert.Equal([$"{folder.Name}: Collapsed -> Expanded"], announced);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// On macOS a folder's tree item says its state after its name too (M57 review): Avalonia 12.1's native peer passes
    /// <c>AXExpanded</c>, not the <c>AXDisclosing</c> VoiceOver reads on an outline's row. Its name changes, and says so,
    /// as the row expands; a file's name is its name alone.
    /// </summary>
    [AvaloniaFact]
    public void On_macOS_a_folder_s_tree_item_also_says_its_state_in_its_name()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), new TreeRowsList().NamesCarryState);
        var b = new Row("B", 0, new Row("b1", 1));
        var rows = new ObservableCollection<Row> { new("A", 0, new Row("a1", 1)) { IsExpanded = true }, new("a1", 1), b, new("C", 0) };
        var (list, window) = Show(rows, height: 300);
        try
        {
            list.NamesCarryState = true;
            Assert.Equal("A, expanded", PeerOf(list, rows[0]).GetName());
            Assert.Equal("B, collapsed", PeerOf(list, b).GetName());
            Assert.Equal("C", PeerOf(list, rows[3]).GetName());
            Assert.Equal("a1", PeerOf(list, rows[1]).GetName());

            var names = new List<(object? Old, object? New)>();
            PeerOf(list, b).PropertyChanged += (_, e) =>
            {
                if (e.Property == AutomationElementIdentifiers.NameProperty)
                {
                    names.Add((e.OldValue, e.NewValue));
                }
            };
            b.IsExpanded = true;
            Assert.Equal([("B, collapsed", "B, expanded")], names);
            Assert.Equal("B, expanded", PeerOf(list, b).GetName());

            list.NamesCarryState = false; // Windows and Linux: the state is the tree item's alone
            Assert.Equal("B", PeerOf(list, b).GetName());
            b.IsExpanded = false;
            Assert.Single(names);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Test Explorer's rows (tests fed to the model by hand, as <c>TestRefreshTests</c>): tree items whose Expand and
    /// Collapse are the explorer's own (its <c>SetExpanded</c>, as Right and Left), named without their state.
    /// </summary>
    [AvaloniaFact]
    public async Task Test_explorer_rows_are_tree_items_that_expand_and_collapse_as_its_keys_do()
    {
        await using var f = ShellFixture.Create();
        await f.StartViewShownAsync();
        const string Controller = "codalus.synthetic:tests";
        var uri = DocumentUris.FromPath(Path.Combine(f.UserData, "CalcTests.cs"));
        f.Shell.Tests.ControllerCreated(new TestControllerParams("codalus.synthetic", Controller, "Synthetic"), new TestRefreshTests.Connection());
        f.Shell.Tests.ItemsUpdated(new TestItemsUpdateParams(Controller,
        [
            new("P", null, "P", null, null, null, null, null, null),
            new("P|c", "P", "Calc", null, uri, new(0, 0, 9, 0), null, null, null),
            .. Enumerable.Range(0, 4).Select(i => new TestItemDto($"P|m{i}", "P|c", "Test" + i, null, uri, new((2 * i) + 1, 0, (2 * i) + 2, 0), null, null, null)),
        ], []));
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        var explorer = f.Shell.TestExplorer;
        await ShellFixture.PumpUntil(() => explorer.IsViewShown && explorer.Rows.Any(r => r.Label == "Calc"), describe: () => string.Join(", ", explorer.Rows.Select(r => r.Label)));
        var list = f.Window.GetVisualDescendants().OfType<TestExplorerView>().First(v => v.IsEffectivelyVisible).FindControl<TreeRowsList>("List")!;
        TestRowViewModel Row(string label) => explorer.Rows.First(r => r.Label == label);
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(Row("Calc")) is not null);

        // P, then its class Calc, collapsed or expanded as the explorer starts it.
        var calc = Row("Calc");
        var state = calc.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        AssertTreeItem(list, explorer.Rows[0], level: 1, position: 1, size: 1, ExpandCollapseState.Expanded);
        AssertTreeItem(list, calc, level: 2, position: 1, size: 1, state);
        Assert.Equal(NameOf(list, calc.AccessibleName, state), PeerOf(list, calc).GetName());
        Assert.DoesNotContain("expanded", calc.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("collapsed", calc.AccessibleName, StringComparison.Ordinal);

        // Expand shows its tests, as Right does; Collapse hides them, as Left does.
        if (calc.IsExpanded)
        {
            Invoke(list, calc, expand: false);
        }

        Assert.DoesNotContain(explorer.Rows, r => r.Label == "Test2");
        Invoke(list, Row("Calc"), expand: true);
        Assert.True(Row("Calc").IsExpanded);
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Label == "Test2") && list.ContainerFromItem(Row("Test2")) is not null);
        AssertTreeItem(list, Row("Calc"), level: 2, position: 1, size: 1, ExpandCollapseState.Expanded);
        AssertTreeItem(list, Row("Test2"), level: 3, position: 3, size: 4, ExpandCollapseState.LeafNode);
        Invoke(list, Row("Calc"), expand: false);
        Assert.False(Row("Calc").IsExpanded);
        Assert.DoesNotContain(explorer.Rows, r => r.Label == "Test2");
    }

    /// <summary>
    /// The Coverage Results' rows (a coverage report fed to it, as <c>CoverageTests</c>): tree items whose Expand and Collapse
    /// are its own (its <c>SetExpanded</c>, as Right and Left), named without their state.
    /// </summary>
    [AvaloniaFact]
    public async Task Coverage_results_rows_are_tree_items_that_expand_and_collapse_as_its_keys_do()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var uri = DocumentUris.FromPath(Path.Combine(f.UserData, "Calculator.cs"));
        f.Shell.Tests.RunCoverage(new TestRunCoverageParams(
            "run-1",
            [new(uri, [new(4, 3, 0, 0), new(7, 2, 0, 0), new(8, 2, 1, 2)])],
            [
                new("Calc", "Calc", "Arithmetic", "Add(int, int)", uri, 4, 1, 1, 0, 0),
                new("Calc", "Calc", "Arithmetic", "Divide(int, int)", uri, 7, 3, 4, 1, 2),
                new("Calc", "Calc", "Geometry", "Area(int)", uri, 9, 1, 1, 0, 0),
            ]));
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<CoverageResultsView>().Any(v => v.IsEffectivelyVisible));
        var results = f.Shell.CoverageResults;
        var list = f.Window.GetVisualDescendants().OfType<CoverageResultsView>().First(v => v.IsEffectivelyVisible).FindControl<TreeRowsList>("List")!;
        CoverageRowViewModel Row(string label) => results.Rows.First(r => r.Label == label);
        await ShellFixture.PumpUntil(() => results.Rows.Any(r => r.Label == "Geometry") && list.ContainerFromItem(Row("Geometry")) is not null);

        // The assembly, its namespace, its two classes (collapsed: the first results open down to the classes).
        AssertTreeItem(list, results.Rows[0], level: 1, position: 1, size: 1, ExpandCollapseState.Expanded);
        AssertTreeItem(list, Row("Arithmetic"), level: 3, position: 1, size: 2, ExpandCollapseState.Collapsed);
        AssertTreeItem(list, Row("Geometry"), level: 3, position: 2, size: 2, ExpandCollapseState.Collapsed);
        Assert.Equal(NameOf(list, Row("Arithmetic").AccessibleName, ExpandCollapseState.Collapsed), PeerOf(list, Row("Arithmetic")).GetName());

        Invoke(list, Row("Arithmetic"), expand: true);
        Assert.True(Row("Arithmetic").IsExpanded);
        list.ScrollIntoView(Row("Divide(int, int)"));
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(Row("Divide(int, int)")) is not null, describe: () => $"{list.Bounds}, {string.Join(", ", results.Rows.Select(r => r.Label))}");
        AssertTreeItem(list, Row("Arithmetic"), level: 3, position: 1, size: 2, ExpandCollapseState.Expanded);
        AssertTreeItem(list, Row("Divide(int, int)"), level: 4, position: 2, size: 2, ExpandCollapseState.LeafNode);
        Assert.Equal((3, 2, 2), list.PlaceOf(results.Rows.IndexOf(Row("Geometry")))); // below the pane's few rows in view
        Invoke(list, Row("Arithmetic"), expand: false);
        Assert.False(Row("Arithmetic").IsExpanded);
        Assert.DoesNotContain(results.Rows, r => r.Label == "Divide(int, int)");
    }
}
