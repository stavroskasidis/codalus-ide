using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>M12a: document symbols, hierarchies, Go to Base, the inheritance margin, inlay hints, CodeLens, classified references and F1 help.</summary>
public sealed partial class LanguageServiceTests
{
    private static readonly Protocol.Range s_wholeFile = new(new Position(0, 0), new Position(10_000, 0));

    [Fact]
    public async Task Document_symbols_nest_types_and_members()
    {
        await fixture.OpenAsync(Shapes);
        var symbols = await fixture.Client.DocumentSymbolsAsync(Uri(Shapes), Ct);

        var ns = Assert.Single(symbols);
        Assert.Equal(("Lib", SymbolKind.Namespace), (ns.Name, ns.Kind));
        Assert.Equal(["IShape", "Shape", "Circle", "Square", "Geometry"], ns.Children!.Select(s => s.Name));
        Assert.Equal(SymbolKind.Interface, ns.Children![0].Kind);
        var circle = ns.Children!.Single(s => s.Name == "Circle");
        Assert.Equal(["Radius", "Area", "Describe"], circle.Children!.Select(s => s.Name));
        Assert.Equal("(int) : string", circle.Children!.Single(s => s.Name == "Describe").Detail);
        Assert.Equal(2, ns.Children!.Single(s => s.Name == "Geometry").Children!.Count(s => s.Name == "Scale"));
        Assert.Equal(fixture.At(Shapes, "Circle(double").Line, circle.SelectionRange.Start.Line);
    }

    [Fact]
    public async Task Call_hierarchy_finds_callers_and_callees()
    {
        await fixture.OpenAsync(Shapes);
        var area = Assert.Single(await fixture.Client.PrepareCallHierarchyAsync(Uri(Shapes), fixture.At(Shapes, "double Area();", 8), Ct));
        Assert.Equal("Area", area.Name);

        var incoming = await fixture.Client.IncomingCallsAsync(area, Ct);
        Assert.Contains(incoming, c => c.From.Name == "Scale" && c.FromRanges.Length == 1);
        Assert.Contains(incoming, c => c.From.Name == "Describe");

        var scale = Assert.Single(await fixture.Client.PrepareCallHierarchyAsync(Uri(Shapes), fixture.At(Shapes, "Scale(IShape", 1), Ct));
        var outgoing = await fixture.Client.OutgoingCallsAsync(scale, Ct);
        Assert.Equal(["Area"], outgoing.Select(c => c.To.Name));

        // Callees outside the solution can be listed (and expanded) by their documentation id.
        var describe = Assert.Single(await fixture.Client.PrepareCallHierarchyAsync(Uri(Shapes), fixture.At(Shapes, "virtual string Describe", 16), Ct));
        var calls = await fixture.Client.OutgoingCallsAsync(describe, Ct);
        Assert.Contains(calls, c => c.To.Name == "ToString" && c.To.Uri.Length == 0);
        Assert.Contains(calls, c => c.To.Name == "Area" && c.To.Uri.EndsWith("Shapes.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Type_hierarchy_and_go_to_base()
    {
        await fixture.OpenAsync(Shapes);
        var shape = Assert.Single(await fixture.Client.PrepareTypeHierarchyAsync(Uri(Shapes), fixture.At(Shapes, "abstract class Shape", 16), Ct));
        Assert.Equal("Shape", shape.Name);
        Assert.Equal(["object", "IShape"], (await fixture.Client.SupertypesAsync(shape, Ct)).Select(t => t.Name));
        Assert.Equal(["Circle", "Square"], (await fixture.Client.SubtypesAsync(shape, Ct)).Select(t => t.Name));

        var interfaceItem = Assert.Single(await fixture.Client.PrepareTypeHierarchyAsync(Uri(Shapes), fixture.At(Shapes, "interface IShape", 11), Ct));
        Assert.Equal(["Shape"], (await fixture.Client.SubtypesAsync(interfaceItem, Ct)).Select(t => t.Name));

        // Go to Base from Circle.Area: Shape.Area (overridden) and IShape.Area (implemented).
        var bases = await fixture.Client.GoToBaseAsync(Uri(Shapes), fixture.At(Shapes, "override double Area() => System.Math.PI", 17), Ct);
        Assert.Equal([fixture.At(Shapes, "abstract double Area", 16).Line, fixture.At(Shapes, "double Area();", 7).Line], bases.Select(b => b.Range.Start.Line));

        // Go to Base from a type: base classes, then interfaces.
        var typeBases = await fixture.Client.GoToBaseAsync(Uri(Shapes), fixture.At(Shapes, "class Circle", 7), Ct);
        Assert.Equal(2, typeBases.Length);
    }

    [Fact]
    public async Task Inheritance_margin_lists_relations_per_line()
    {
        await fixture.OpenAsync(Shapes);
        var items = await fixture.Client.InheritanceMarginAsync(Uri(Shapes), s_wholeFile, Ct);

        var shapeInterface = items.Single(i => i.Line == fixture.At(Shapes, "interface IShape").Line);
        Assert.Contains(InheritanceRelations.ImplementedBy, shapeInterface.Relations);
        var shape = items.Single(i => i.Line == fixture.At(Shapes, "abstract class Shape").Line);
        Assert.Equal([InheritanceRelations.Implements, InheritanceRelations.InheritedBy], shape.Relations.Order());
        var circleArea = items.Single(i => i.Line == fixture.At(Shapes, "override double Area() => System.Math.PI").Line);
        Assert.Equal([InheritanceRelations.Implements, InheritanceRelations.Overrides], circleArea.Relations.Order());
        Assert.All(circleArea.Targets, t => Assert.NotNull(t.Location));
        var describe = items.Single(i => i.Line == fixture.At(Shapes, "virtual string Describe").Line);
        Assert.Equal([InheritanceRelations.OverriddenBy], describe.Relations);
        Assert.Equal("Lib.Circle.Describe(int)", Assert.Single(describe.Targets).DisplayName);
    }

    [Fact]
    public async Task Inlay_hints_show_parameter_names_and_inferred_types()
    {
        await fixture.OpenAsync(Program);
        var hints = await fixture.Client.InlayHintsAsync(Uri(Program), s_wholeFile, Ct);

        Assert.Contains(hints, h => h.Label == "radius:" && h.Position == fixture.At(Program, "new Circle(2)", 11));
        Assert.Contains(hints, h => h.Label == "precision:" && h.Position == fixture.At(Program, "Describe(2)", 9));
        Assert.Contains(hints, h => h.Label == "factor:" && h.Position == fixture.At(Program, "circle, 3)", 8));
        Assert.Contains(hints, h => h.Label == ": string?" && h.Kind == InlayHintKind.Type && h.Position == fixture.At(Program, "json =", 4));
        Assert.DoesNotContain(hints, h => h.Position == fixture.At(Program, "circle =", 6)); // new Circle(2): the type is apparent
    }

    [Fact]
    public async Task Code_lenses_count_references_and_implementations()
    {
        await fixture.OpenAsync(Shapes);
        var lenses = await fixture.Client.CodeLensesAsync(Uri(Shapes), Ct);
        Assert.All(lenses, l => Assert.Null(l.Command));

        var interfaceLine = fixture.At(Shapes, "interface IShape").Line;
        var resolved = new List<CodeLens>();
        foreach (var lens in lenses.Where(l => l.Range.Start.Line == interfaceLine))
        {
            resolved.Add(await fixture.Client.ResolveCodeLensAsync(lens, Ct));
        }

        Assert.Contains(resolved, l => l.Command!.Title == "3 implementations" && l.Command.CommandId == "editor.action.goToImplementation");
        var references = resolved.Single(l => l.Command!.Title.EndsWith("references", StringComparison.Ordinal) || l.Command.Title == "1 reference");
        Assert.Equal("2 references", references.Command!.Title); // Shape's base list and Geometry.Scale's parameter
        Assert.Equal(2, references.Command.Arguments!.Length);
    }

    [Fact]
    public async Task Reference_items_classify_reads_and_writes()
    {
        await fixture.OpenAsync(Program);
        var result = await fixture.Client.ReferenceItemsAsync(Uri(Program), fixture.At(Program, "circle =", 1), Ct);

        Assert.NotNull(result);
        Assert.Equal("circle", result!.SymbolName);
        Assert.Equal([ReferenceItemKinds.Declaration, ReferenceItemKinds.Read, ReferenceItemKinds.Read], result.Items.Select(i => i.Kind));
        Assert.All(result.Items, i => Assert.Equal("App", i.Project));
        Assert.Equal("Console.WriteLine(circle.Describe(2));", result.Items[1].LineText);
    }

    [Fact]
    public async Task Symbol_details_link_framework_symbols_to_the_api_reference()
    {
        await fixture.OpenAsync(Program);
        var console = await fixture.Client.SymbolDetailsAsync(Uri(Program), fixture.At(Program, "Console.WriteLine(json)", 2), Ct);
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.console", console!.HelpUrl);
        Assert.False(console.InSource);
        var writeLine = await fixture.Client.SymbolDetailsAsync(Uri(Program), fixture.At(Program, "WriteLine(json)", 2), Ct);
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.console.writeline", writeLine!.HelpUrl);
        var circle = await fixture.Client.SymbolDetailsAsync(Uri(Program), fixture.At(Program, "new Circle", 5), Ct);
        Assert.True(circle!.InSource);
        Assert.Null(circle.HelpUrl);
        Assert.Equal("M:Lib.Circle.#ctor(System.Double)", circle.DocumentationId);
    }
}
