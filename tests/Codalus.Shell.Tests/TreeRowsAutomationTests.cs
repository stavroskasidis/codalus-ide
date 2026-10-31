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
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The virtualized trees as a screen reader sees them (M57, the maintainer's "proper peer for all trees"): each list of a
/// flattened tree's rows (<see cref="TreeRowsList"/>) is a tree, each row a tree item whose folder has the ExpandCollapse
/// pattern (its Expand and Collapse are the view's own), with its level and its place among its siblings, and a name
/// without ", expanded" or ", collapsed" in it (M56 put the state there). The Solution Explorer and the Document Outline
/// here; Git Changes, the Git Repository tree, a commit's changes and Compare in their own tests.
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
        Assert.DoesNotContain(", expanded", name, StringComparison.Ordinal);
        Assert.DoesNotContain(", collapsed", name, StringComparison.Ordinal);
    }

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
            Assert.Equal("a2", PeerOf(list, At("a2")).GetName());

            // AT-SPI reads a row's place from its control: the rows a screen reader knows carry it once the list is idle.
            ShellFixture.Pump();
            var a3 = list.ContainerFromItem(At("a3"))!;
            Assert.Equal((3, 3), (AutomationProperties.GetPositionInSet(a3), AutomationProperties.GetSizeOfSet(a3)));

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

            // A sibling removed: the places move, on the peers and on the controls.
            a.Children.RemoveAt(0);
            rows.Remove(At("a1"));
            ShellFixture.Pump();
            AssertTreeItem(list, At("a3"), level: 2, position: 2, size: 2, ExpandCollapseState.LeafNode);
            a3 = list.ContainerFromItem(At("a3"))!;
            Assert.Equal((2, 2), (AutomationProperties.GetPositionInSet(a3), AutomationProperties.GetSizeOfSet(a3)));
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
        Assert.Equal("src", PeerOf(list, Row("src")).GetName());

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
        Assert.Equal("Class Class1", PeerOf(list, class1).GetName());

        Invoke(list, class1, expand: false);
        Assert.False(class1.IsExpanded);
        Assert.Equal(12, outline.Rows.Count);
        AssertTreeItem(list, class1, level: 2, position: 2, size: 3, ExpandCollapseState.Collapsed);
        Invoke(list, class1, expand: true);
        Assert.True(class1.IsExpanded);
        Assert.Equal(16, outline.Rows.Count);
    }
}
