using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Previewer;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Workbench.Tests;

/// <summary>M19a (ADR 0048): the previewer's selection mapping between the XAML's logical tree and the preview's paths, and the XAML compiler's build errors as editor diagnostics.</summary>
public sealed class PreviewSelectionTests
{
    private static Range Lines(int from, int to) => new(new Position(from, 0), new Position(to, 0));

    private static XamlLogicalNode Node(string type, int from, int to, string? name = null, params XamlLogicalNode[] children) =>
        new(type, name, Lines(from, to), Lines(from, from + 1), children);

    // MainWindow { DockPanel { TextBlock, StackPanel { Button, TextBlock, Button }, ListBox } }
    private static readonly XamlLogicalNode[] s_tree =
    [
        Node("MainWindow", 0, 30, null,
            Node("DockPanel", 2, 28, null,
                Node("TextBlock", 3, 4, "Greeting"),
                Node("StackPanel", 5, 10, null,
                    Node("Button", 6, 7, "First"),
                    Node("TextBlock", 7, 8),
                    Node("Button", 8, 9, "Second")),
                Node("ListBox", 11, 20, "Tasks"))),
    ];

    [Fact]
    public void The_caret_maps_to_the_deepest_element_and_its_path()
    {
        var at = PreviewSelection.At(s_tree, new Position(8, 4));
        Assert.NotNull(at);
        Assert.Equal("Second", at.Value.Node.Name);
        Assert.Equal(["MainWindow:0", "DockPanel:0", "StackPanel:0", "Button:1"], at.Value.Path.Select(s => $"{s.Type}:{s.Ordinal}"));

        // Between children: their parent.
        Assert.Equal("DockPanel", PreviewSelection.At(s_tree, new Position(10, 2))?.Node.Type);
        Assert.Null(PreviewSelection.At(s_tree, new Position(40, 0)));
        Assert.Null(PreviewSelection.At([], new Position(0, 0)));
    }

    [Fact]
    public void A_hit_aligns_at_the_root_and_stops_where_the_xaml_does()
    {
        // The window's own path from the designer: the same steps, plus the button's generated text.
        PreviewPathStep[] hit = [new("MainWindow", 0), new("DockPanel", 0), new("StackPanel", 0), new("Button", 1, "Second"), new("AccessText", 0)];
        Assert.Equal("Second", PreviewSelection.NodeFor(s_tree, hit)?.Name);

        // A user control's preview: the designer's window wraps it, so the path starts above the XAML's root.
        XamlLogicalNode[] control = [Node("TodoItemView", 0, 10, null, Node("StackPanel", 1, 9, null, Node("CheckBox", 2, 3)))];
        PreviewPathStep[] wrapped = [new("Window", 0), new("TodoItemView", 0), new("StackPanel", 0), new("CheckBox", 0)];
        Assert.Equal("CheckBox", PreviewSelection.NodeFor(control, wrapped)?.Type);

        // An item container the XAML doesn't declare: the list box.
        PreviewPathStep[] item = [new("MainWindow", 0), new("DockPanel", 0), new("ListBox", 0), new("ListBoxItem", 2)];
        Assert.Equal("Tasks", PreviewSelection.NodeFor(s_tree, item)?.Name);

        // Something else entirely.
        Assert.Null(PreviewSelection.NodeFor(s_tree, [new("Popup", 0)]));
    }

    [Fact]
    public void Xaml_compiler_errors_become_diagnostics_of_their_files()
    {
        var file = Path.Combine(Path.GetTempPath(), "Views", "Main.axaml");
        BuildDiagnosticInfo[] diagnostics =
        [
            new(BuildSeverities.Error, "AVLN2000", "Unable to resolve type Buton", file, 12, 6, 0, 0, "App.csproj", null),
            new(BuildSeverities.Error, "AVLN2000", "Unable to resolve type Buton", file, 12, 6, 0, 0, "App.csproj", null),
            new(BuildSeverities.Warning, "AVLN3001", "A warning", file.ToUpperInvariant(), 3, 1, 3, 9, "App.csproj", null),
            new(BuildSeverities.Error, "CS0103", "A C# error", Path.Combine(Path.GetTempPath(), "Main.axaml.cs"), 1, 1, 0, 0, "App.csproj", null),
        ];
        var byFile = XamlBuildDiagnostics.ByFile(diagnostics);
        var items = Assert.Single(byFile).Value;
        Assert.Equal(2, items.Length); // the duplicate once, the C# error not at all
        var error = items.Single(d => d.Code == "AVLN2000");
        Assert.Equal((DiagnosticSeverity.Error, new Position(11, 5), new Position(11, 6)), (error.Severity, error.Range.Start, error.Range.End));
        var warning = items.Single(d => d.Code == "AVLN3001");
        Assert.Equal((DiagnosticSeverity.Warning, new Position(2, 8)), (warning.Severity, warning.Range.End));
    }
}
