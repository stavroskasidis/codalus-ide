using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

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
    public async Task Warnings_can_be_suppressed_in_source_or_configured_in_editorconfig()
    {
        var reporter = fixture.File("src", "App", "Reporter.cs");
        await fixture.OpenAsync(reporter);
        await LanguageFixture.WaitUntilAsync(() => fixture.Languages.Diagnostics.For(reporter).Any(d => d.Code == "CA2200"), TimeSpan.FromSeconds(60));
        var diagnostic = fixture.Languages.Diagnostics.For(reporter).First(d => d.Code == "CA2200");

        var actions = await fixture.Client.CodeActionsAsync(Uri(reporter), diagnostic.Range, [diagnostic], Ct);
        var pragma = actions.Single(a => a.Title == "Suppress CA2200: in source (#pragma)");
        Assert.Contains(actions, a => a.Title == "Configure CA2200 severity: none");

        var edit = (await fixture.Client.ResolveCodeActionAsync(pragma, Ct)).Edit!;
        var text = string.Concat(edit.DocumentChanges.Single().Edits.Select(e => e.NewText));
        Assert.Contains("#pragma warning disable CA2200", text, StringComparison.Ordinal);
        Assert.Contains("#pragma warning restore CA2200", text, StringComparison.Ordinal);

        // Configure: the solution's .editorconfig gets the rule's severity.
        var configure = actions.Single(a => a.Title == "Configure CA2200 severity: suggestion");
        var change = Assert.Single((await fixture.Client.ResolveCodeActionAsync(configure, Ct)).Edit!.DocumentChanges);
        Assert.EndsWith(".editorconfig", change.TextDocument.Uri, StringComparison.Ordinal);
        Assert.Contains(change.Edits, e => e.NewText.Contains("dotnet_diagnostic.CA2200.severity = suggestion", StringComparison.Ordinal));
        await fixture.Languages.CloseAsync(reporter);
    }

    [Fact]
    public async Task Refactorings_with_options_ask_then_apply_the_answers()
    {
        await fixture.OpenAsync(Shapes);
        async Task<(CodeAction Action, CodeActionOptions Options)> ActionAsync(string marker, int delta, string title)
        {
            var at = fixture.At(Shapes, marker, delta);
            var actions = await fixture.Client.CodeActionsAsync(Uri(Shapes), new Protocol.Range(at, at), [], Ct);
            var action = actions.Single(a => a.Title == title);
            return (action, (await fixture.Client.CodeActionOptionsAsync(action, Ct))!);
        }

        static CodeAction With(CodeAction action, CodeActionChoices choices) => choices.AttachTo(action);

        // Generate constructor for Square: its Side property (declared in the other partial file).
        var (generate, options) = await ActionAsync("partial class Square", 15, "Generate constructor…");
        Assert.Contains(options.Items, i => i.Id == "Side");
        var edit = (await fixture.Client.ResolveCodeActionAsync(With(generate, new CodeActionChoices(null, ["Side"])), Ct)).Edit!;
        var generated = Apply(fixture.Text(Shapes).Text, edit.DocumentChanges.Single(d => d.TextDocument.Uri.EndsWith("Shapes.cs", StringComparison.Ordinal)).Edits);
        Assert.Matches(@"public Square\(double side\)\s*\{\s*Side = side;\s*\}", generated);

        // Extract interface from Circle, with a chosen name and two of its members.
        var (extract, extractOptions) = await ActionAsync("class Circle", 7, "Extract interface…");
        Assert.Equal("ICircle", extractOptions.Name);
        var area = extractOptions.Items.Single(i => i.Label.Contains("Area", StringComparison.Ordinal)).Id;
        var radius = extractOptions.Items.Single(i => i.Label.Contains("Radius", StringComparison.Ordinal)).Id;
        var extracted = (await fixture.Client.ResolveCodeActionAsync(With(extract, new CodeActionChoices("IRound", [area, radius])), Ct)).Edit!;
        var newFile = extracted.DocumentChanges.Single(d => d.TextDocument.Uri.EndsWith("IRound.cs", StringComparison.Ordinal));
        var interfaceText = newFile.Edits.Single().NewText;
        Assert.Contains("public interface IRound", interfaceText, StringComparison.Ordinal);
        Assert.Contains("double Area();", interfaceText, StringComparison.Ordinal);
        Assert.Contains("double Radius { get; }", interfaceText, StringComparison.Ordinal);
        Assert.DoesNotContain("Describe", interfaceText, StringComparison.Ordinal);
        Assert.Contains(extracted.DocumentChanges.Single(d => d.TextDocument.Uri.EndsWith("Shapes.cs", StringComparison.Ordinal)).Edits, e => e.NewText.Contains("IRound", StringComparison.Ordinal));

        // Change signature: reorder Geometry.Scale(value, factor, round) and drop round.
        var (change, changeOptions) = await ActionAsync("Scale(double value", 7, "Change signature…");
        Assert.True(changeOptions.Reorderable);
        Assert.Equal(["value", "factor", "round"], changeOptions.Items.Select(i => i.Id));
        var changed = (await fixture.Client.ResolveCodeActionAsync(With(change, new CodeActionChoices(null, ["factor", "value"])), Ct)).Edit!;
        var newText = Apply(fixture.Text(Shapes).Text, changed.DocumentChanges.Single(d => d.TextDocument.Uri.EndsWith("Shapes.cs", StringComparison.Ordinal)).Edits);
        Assert.Contains("Scale(double factor, double value)", newText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_cleanup_removes_unused_usings_sorts_and_formats()
    {
        var path = fixture.File("src", "App", "Json.cs");
        var original = await fixture.OpenAsync(path);
        try
        {
            await fixture.EditAsync(path, original, "using System.Text;\nusing System.Collections.Generic;\nusing System;\n" + original.Replace("record Person(string Name);", "record  Person( string Name ) ;", StringComparison.Ordinal));
            var edit = await fixture.Client.CodeCleanupAsync(Uri(path), null, Ct);

            Assert.NotNull(edit);
            var cleaned = Apply(fixture.Text(path).Text, Assert.Single(edit.DocumentChanges).Edits);
            Assert.DoesNotContain("using System.Text;", cleaned, StringComparison.Ordinal);
            Assert.Contains("record Person(string Name);", cleaned, StringComparison.Ordinal);
            Assert.True(cleaned.IndexOf("using System;", StringComparison.Ordinal) < cleaned.IndexOf("using System.Collections", StringComparison.Ordinal) || !cleaned.Contains("using System.Collections", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.EditAsync(path, fixture.Text(path).Text, original);
            await fixture.Languages.CloseAsync(path);
        }
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

    [Fact]
    public async Task Syntax_visualizer_returns_the_tree_and_the_semantics_at_the_caret()
    {
        await fixture.OpenAsync(Shapes);
        var result = await fixture.Client.SyntaxTreeAsync(Uri(Shapes), fixture.At(Shapes, "round ? System", 1), Ct);

        Assert.NotNull(result);
        Assert.Equal("CompilationUnit", result.Root.Kind);
        var path = new List<SyntaxNodeItem>();
        for (var item = result.Root; item is not null; item = item.Children.FirstOrDefault(c => c.OnPath))
        {
            path.Add(item);
        }

        Assert.Contains(path, i => i.Kind == "ConditionalExpression");
        Assert.Equal(("IdentifierToken", "token", "round"), (path[^1].Kind, path[^1].Category, path[^1].Text));
        Assert.Equal("Parameter", result.Semantics!.SymbolKind);
        Assert.Equal("bool", result.Semantics.Type);
        Assert.Equal("ParameterReference", result.Semantics.Operation!.Kind);

        // Inside a documentation comment: the path goes through the trivia's structure.
        var doc = await fixture.Client.SyntaxTreeAsync(Uri(Shapes), fixture.At(Shapes, "name=\"round\"", 7), Ct);
        var trivia = new List<SyntaxNodeItem>();
        for (var item = doc!.Root; item is not null; item = item.Children.FirstOrDefault(c => c.OnPath))
        {
            trivia.Add(item);
        }

        Assert.Contains(trivia, i => i is { Kind: "SingleLineDocumentationCommentTrivia", Category: "trivia" });
        Assert.Contains(trivia, i => i.Kind == "XmlNameAttribute");
    }

    [Fact]
    public async Task Code_metrics_measure_members_types_and_projects()
    {
        await fixture.OpenAsync(Shapes);
        var result = await fixture.Client.CodeMetricsAsync(Uri(Shapes), Ct);

        var project = Assert.Single(result!.Projects);
        Assert.Equal("Lib", project.Name);
        var types = project.Children.Single(n => n.Name == "Lib").Children;
        var geometry = types.Single(t => t.Name == "Geometry");
        var scale = geometry.Children.Single(m => m.Name.Contains("Scale(double", StringComparison.Ordinal));
        Assert.Equal(2, scale.CyclomaticComplexity); // the conditional
        Assert.Equal(3, geometry.CyclomaticComplexity);
        Assert.InRange(scale.MaintainabilityIndex, 1, 100);
        Assert.Equal(2, types.Single(t => t.Name == "Circle").DepthOfInheritance);
        var shape = types.Single(t => t.Name == "Shape");
        Assert.True(shape.ClassCoupling >= 2, "Shape uses IShape and CultureInfo");
        Assert.NotNull(scale.Location);
        Assert.Equal(project.SourceLines, project.Children.Sum(n => n.SourceLines));

        // A program with top-level statements: one member of Program.
        var all = await fixture.Client.CodeMetricsAsync(null, Ct);
        var app = all!.Projects.Single(p => p.Name == "App");
        var main = app.Children.SelectMany(n => n.Children).Single(t => t.Name == "Program").Children.Single(m => m.Name == "<top-level statements>");
        Assert.True(main.SourceLines > 0 && main.ExecutableLines > 0, $"{main.SourceLines} lines, {main.ExecutableLines} executable");
    }
}
