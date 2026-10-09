using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>End-to-end C# features on samples/LanguageFeatures, through the real build host and language host.</summary>
public sealed class LanguageServiceTests(LanguageFixture fixture) : IClassFixture<LanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Program => fixture.File("src", "App", "Program.cs");

    private string Shapes => fixture.File("src", "Lib", "Shapes.cs");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task<IReadOnlyList<Diagnostic>> WaitForDiagnosticsAsync(string path, Func<IReadOnlyList<Diagnostic>, bool> condition)
    {
        await LanguageFixture.WaitUntilAsync(() => condition(fixture.Languages.Diagnostics.For(path)), TimeSpan.FromSeconds(60));
        return fixture.Languages.Diagnostics.For(path);
    }

    [Fact]
    public async Task Diagnostics_include_analyzers_and_follow_edits()
    {
        var reporter = fixture.File("src", "App", "Reporter.cs");

        // Closed files: the background pass publishes compiler diagnostics only (analyzers run for open documents).
        await fixture.OpenAsync(reporter);
        var diagnostics = await WaitForDiagnosticsAsync(reporter, d => d.Any(x => x.Code == "CA2200"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Single(d => d.Code == "CA2200").Severity);

        await fixture.EditAsync(reporter, "Console.WriteLine(int.Parse(", "int broken = \"text\"; Console.WriteLine(int.Parse(");
        diagnostics = await WaitForDiagnosticsAsync(reporter, d => d.Any(x => x.Code == "CS0029"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostics.Single(d => d.Code == "CS0029").Severity);

        await fixture.EditAsync(reporter, "int broken = \"text\"; ", string.Empty);
        await WaitForDiagnosticsAsync(reporter, d => d.All(x => x.Code != "CS0029"));
        await fixture.Languages.CloseAsync(reporter);
    }

    [Fact]
    public async Task Test_candidates_use_unsaved_text_and_files_on_disk()
    {
        var path = fixture.File("src", "Lib", "Square.Part.cs");
        var onDisk = Path.Combine(Path.GetDirectoryName(path)!, "OnDiskTests.cs");
        await File.WriteAllTextAsync(onDisk, "namespace Lib.Tests; public class OnDisk { [Fact] public void Works() { } }", Ct);
        try
        {
            await fixture.OpenAsync(path);
            await fixture.EditAsync(path, "public double Side { get; }", "public double Side { get; }" + Environment.NewLine + "    [Fact] public void Unsaved() { }");

            var result = await fixture.Client.TestCandidatesAsync([Uri(path), Uri(onDisk), Uri(Path.Combine(Path.GetDirectoryName(path)!, "Missing.cs"))], Ct);

            Assert.Equal(["Unsaved"], result.Documents[0].Candidates.Where(c => c.Kind == TestCandidateKinds.Method).Select(c => c.Name));
            Assert.Equal(["Lib.Tests.OnDisk.Works"], result.Documents[1].Candidates.Where(c => c.Kind == TestCandidateKinds.Method).Select(c => c.Type + "." + c.Name));
            Assert.Empty(result.Documents[2].Candidates);
        }
        finally
        {
            await fixture.EditAsync(path, Environment.NewLine + "    [Fact] public void Unsaved() { }", string.Empty);
            await fixture.Languages.CloseAsync(path);
            File.Delete(onDisk);
        }
    }

    [Fact]
    public async Task Editorconfig_severities_apply()
    {
        var path = fixture.File("src", "Lib", "Square.Part.cs");
        await fixture.OpenAsync(path);
        await fixture.EditAsync(path, "public double Side { get; }", "public double Side { get; }\n\n    public int Twice(int x) => x * 2;");
        var diagnostics = await WaitForDiagnosticsAsync(path, d => d.Any(x => x.Code == "CA1822"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.First(d => d.Code == "CA1822").Severity);
        await fixture.EditAsync(path, "\n\n    public int Twice(int x) => x * 2;", string.Empty);
        await fixture.Languages.CloseAsync(path);
    }

    [Fact]
    public async Task Completion_lists_members_and_resolves_edits()
    {
        await fixture.OpenAsync(Program);
        var list = await fixture.Client.CompletionAsync(Uri(Program), fixture.At(Program, "circle.Describe", "circle.".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);

        Assert.Contains(list.Items, i => i.Label == "Radius" && i.Kind == CompletionItemKind.Property);
        var describe = list.Items.Single(i => i.Label == "Describe");
        Assert.Equal(CompletionItemKind.Method, describe.Kind);
        Assert.NotNull(list.ItemDefaults?.EditRange);

        var resolved = await fixture.Client.ResolveCompletionAsync(describe, Ct);
        Assert.Equal("Describe", resolved.TextEdit?.NewText);
        Assert.Contains("Describes the shape", resolved.Documentation?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hover_shows_signature_and_documentation()
    {
        await fixture.OpenAsync(Program);
        var hover = await fixture.Client.HoverAsync(Uri(Program), fixture.At(Program, "Scale(circle", 2), Ct);

        Assert.NotNull(hover);
        Assert.Contains("```csharp", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Scale(IShape shape, double factor)", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Scales a shape's area.", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signature_help_lists_overloads_and_the_active_parameter()
    {
        await fixture.OpenAsync(Program);
        var help = await fixture.Client.SignatureHelpAsync(Uri(Program), fixture.At(Program, "Scale(circle, 3)", "Scale(circle, ".Length), null, Ct);

        Assert.NotNull(help);
        Assert.Equal(2, help.Signatures.Length);
        var active = help.Signatures[help.ActiveSignature];
        Assert.Contains("IShape shape", active.Label, StringComparison.Ordinal);
        Assert.Equal("double factor", active.Parameters[help.ActiveParameter].Label);
        Assert.Contains("scale factor", active.Parameters[1].Documentation?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definition_crosses_projects_and_reaches_generated_code()
    {
        await fixture.OpenAsync(Program);

        var circle = await fixture.Client.DefinitionAsync(Uri(Program), fixture.At(Program, "new Circle", "new ".Length), Ct);
        Assert.Equal(Uri(Shapes), Assert.Single(circle).Uri);

        var generated = await fixture.Client.DefinitionAsync(Uri(Program), fixture.At(Program, "Default.Person", "Default.".Length), Ct);
        var location = Assert.Single(generated);
        Assert.True(DocumentUris.IsGenerated(location.Uri), location.Uri);
        var text = await fixture.Client.SourceGeneratedTextAsync(location.Uri, Ct);
        Assert.NotNull(text);
        Assert.Contains("AppJsonContext", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Implementation_finds_overrides_and_implementations()
    {
        await fixture.OpenAsync(Shapes);
        var locations = await fixture.Client.ImplementationAsync(Uri(Shapes), fixture.At(Shapes, "double Area();", "double ".Length), Ct);

        // IShape.Area → Shape.Area is abstract (skipped); Circle.Area and Square.Area implement it.
        Assert.Equal(2, locations.Length);
        Assert.All(locations, l => Assert.Equal(Uri(Shapes), l.Uri));
    }

    [Fact]
    public async Task Folding_highlights_tokens_and_options()
    {
        await fixture.OpenAsync(Shapes);
        var folding = await fixture.Client.FoldingRangesAsync(Uri(Shapes), Ct);
        Assert.Contains(folding, f => f.Kind == "region" && f.CollapsedText == "Squares");
        Assert.Contains(folding, f => f.Kind == "comment");
        Assert.Contains(folding, f => f.Kind is null && f.StartLine == fixture.At(Shapes, "public interface IShape").Line);

        var highlights = await fixture.Client.DocumentHighlightsAsync(Uri(Shapes), fixture.At(Shapes, "Radius * Radius"), Ct);
        Assert.Equal(3, highlights.Length); // the declaration and two reads
        Assert.Single(highlights, h => h.Kind == DocumentHighlightKind.Write);

        var legend = fixture.Client.Legend!;
        var tokens = await fixture.Client.SemanticTokensAsync(Uri(Shapes), new Protocol.Range(new Position(0, 0), new Position(30, 0)), Ct);
        Assert.Contains(Decode(tokens.Data), t => legend.TokenTypes[t.Type] == "interface");

        var options = await fixture.Client.DocumentOptionsAsync(Uri(Shapes), Ct);
        Assert.Equal(new DocumentOptions(InsertSpaces: true, TabSize: 4, IndentSize: 4, NewLine: "\n"), options);
    }

    [Fact]
    public async Task Switching_the_active_framework_changes_symbols()
    {
        var conditional = fixture.File("src", "Lib", "Conditional.cs");
        var lib = fixture.File("src", "Lib", "Lib.csproj");
        await fixture.OpenAsync(Program);
        async Task<string[]> MembersAsync()
        {
            var list = await fixture.Client.CompletionAsync(Uri(Program), fixture.At(Program, "Conditional.Framework", "Conditional.".Length), null, Ct);
            return [.. list.Items.Select(i => i.Label)];
        }

        Assert.Contains("ModernOnly", await MembersAsync());
        await fixture.Solutions.SetActiveFrameworkAsync(lib, "netstandard2.0", Ct);
        await LanguageFixture.WaitUntilAsync(() => fixture.Solutions.CompilerInputs[lib].TargetFramework == "netstandard2.0");
        await LanguageFixture.WaitUntilAsync(() => MembersAsync().Result.Contains("LegacyOnly"));
        Assert.DoesNotContain("ModernOnly", await MembersAsync());

        await fixture.Solutions.SetActiveFrameworkAsync(lib, "net10.0", Ct);
        await LanguageFixture.WaitUntilAsync(() => MembersAsync().Result.Contains("ModernOnly"));
        _ = conditional;
    }

    [Fact]
    public async Task References_cross_projects()
    {
        await fixture.OpenAsync(Program);
        var references = await fixture.Client.ReferencesAsync(Uri(Program), fixture.At(Program, "new Circle", "new C".Length), includeDeclaration: true, Ct);

        Assert.Contains(references, r => r.Uri == Uri(Shapes)); // the declaration
        Assert.Contains(references, r => r.Uri == Uri(Program));
    }

    [Fact]
    public async Task Rename_returns_edits_for_every_reference()
    {
        await fixture.OpenAsync(Shapes);
        var at = fixture.At(Shapes, "Radius { get; }", 1);
        var prepared = await fixture.Client.PrepareRenameAsync(Uri(Shapes), at, Ct);
        Assert.Equal("Radius", prepared?.Placeholder);

        var edit = await fixture.Client.RenameAsync(Uri(Shapes), at, "Size", Ct);
        var shapes = Assert.Single(edit!.DocumentChanges, c => c.TextDocument.Uri == Uri(Shapes));
        Assert.Equal(3, shapes.Edits.Length); // the declaration and two uses
        Assert.All(shapes.Edits, e => Assert.Equal("Size", e.NewText));
        Assert.Null(await fixture.Client.PrepareRenameAsync(Uri(Shapes), fixture.At(Shapes, "namespace Lib", 2), Ct)); // a keyword
    }

    [Fact]
    public async Task Code_actions_fix_errors_and_offer_refactorings()
    {
        var path = fixture.File("src", "App", "Builder.cs");
        await File.WriteAllTextAsync(path, "internal static class Builder\n{\n    public static string Make() => new StringBuilder().ToString();\n}\n", Ct);
        await fixture.OpenAsync(path);
        var at = fixture.At(path, "StringBuilder", 3);
        var diagnostics = await WaitForDiagnosticsAsync(path, d => d.Any(x => x.Code == "CS0246"));

        var actions = await fixture.Client.CodeActionsAsync(Uri(path), new Protocol.Range(at, at), [.. diagnostics], Ct);
        var addUsing = actions.First(a => a.Title.Contains("using System.Text", StringComparison.Ordinal));
        Assert.Equal(CodeActionKind.QuickFix, addUsing.Kind);
        var resolved = await fixture.Client.ResolveCodeActionAsync(addUsing, Ct);
        var change = Assert.Single(resolved.Edit!.DocumentChanges);
        Assert.Equal(Uri(path), change.TextDocument.Uri);
        Assert.StartsWith("using System.Text;", Apply(fixture.Text(path).Text, change.Edits), StringComparison.Ordinal);

        // Refactorings at the method name ("Use block body", among others).
        var name = fixture.At(path, "Make()", 1);
        var refactorings = await fixture.Client.CodeActionsAsync(Uri(path), new Protocol.Range(name, name), [], Ct);
        Assert.True(refactorings.Any(a => a.Kind == CodeActionKind.Refactor), string.Join(", ", refactorings.Select(a => a.Title)));
        await fixture.Languages.CloseAsync(path);
    }

    [Fact]
    public async Task Formatting_follows_the_editorconfig()
    {
        var path = fixture.File("src", "App", "Messy.cs");
        await File.WriteAllTextAsync(path, "internal static class Messy\n{\n  public static int  Two( ) {return 1+1;}\n}\n", Ct);
        await fixture.OpenAsync(path);

        var edits = await fixture.Client.FormatAsync(Uri(path), null, new FormattingOptions(4, true), Ct);
        var text = Apply(fixture.Text(path).Text, edits);
        Assert.Contains("    public static int Two()", text, StringComparison.Ordinal);
        Assert.Contains("return 1 + 1;", text, StringComparison.Ordinal);
        await fixture.Languages.CloseAsync(path);
    }

    [Fact]
    public async Task Workspace_symbols_and_metadata_definitions()
    {
        var symbols = await fixture.Client.WorkspaceSymbolsAsync("Circ", Ct);
        Assert.Contains(symbols, s => s.Name == "Circle" && s.Kind == SymbolKind.Class && s.Location.Uri == Uri(Shapes));

        await fixture.OpenAsync(Program);
        var console = await fixture.Client.DefinitionAsync(Uri(Program), fixture.At(Program, "Console.WriteLine", 2), Ct);
        var location = Assert.Single(console);
        Assert.True(DocumentUris.IsMetadata(location.Uri), location.Uri);
        var text = await fixture.Client.MetadataTextAsync(location.Uri, Ct);
        Assert.Contains("class Console", text?.Text, StringComparison.Ordinal);
        Assert.True(location.Range.Start.Line > 0);
    }

    private static string Apply(string text, IEnumerable<Protocol.TextEdit> edits)
    {
        int Offset(Position p)
        {
            var lines = text.Split('\n');
            return lines.Take(p.Line).Sum(l => l.Length + 1) + p.Character;
        }

        foreach (var edit in edits.OrderByDescending(e => e.Range.Start.Line).ThenByDescending(e => e.Range.Start.Character))
        {
            var start = Offset(edit.Range.Start);
            text = text[..start] + edit.NewText + text[Offset(edit.Range.End)..];
        }

        return text;
    }

    [Fact]
    public async Task A_crashed_host_restarts_with_projects_and_documents()
    {
        await fixture.OpenAsync(Program);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRestarted(object? sender, string language) => restarted.TrySetResult();
        fixture.Languages.HostRestarted += OnRestarted;
        try
        {
            System.Diagnostics.Process.GetProcessById(fixture.Languages.HostProcessId("csharp")!.Value).Kill();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            // The replayed project data resolves Circle in Lib again.
            await LanguageFixture.WaitUntilAsync(() =>
                fixture.Client.DefinitionAsync(Uri(Program), fixture.At(Program, "new Circle", "new ".Length), Ct).Result.Length == 1, TimeSpan.FromSeconds(60));
        }
        finally
        {
            fixture.Languages.HostRestarted -= OnRestarted;
        }
    }

    private static List<(int Line, int Start, int Length, int Type)> Decode(int[] data)
    {
        var result = new List<(int, int, int, int)>();
        int line = 0, start = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            result.Add((line, start, data[i + 2], data[i + 3]));
        }

        return result;
    }
}

/// <summary>Untrusted folders: no design-time builds; files get syntax and framework semantics only.</summary>
public sealed class UntrustedLanguageServiceTests(UntrustedLanguageFixture fixture) : IClassFixture<UntrustedLanguageFixture>
{
    [Fact]
    public async Task Files_go_to_miscellaneous_files()
    {
        var program = fixture.File("src", "App", "Program.cs");
        await fixture.OpenAsync(program);
        Assert.Empty(fixture.Solutions.CompilerInputs);

        // JsonSerializer resolves (framework references); Lib does not, and there are no implicit usings.
        var hover = await fixture.Client.HoverAsync(DocumentUris.FromPath(program), fixture.At(program, "JsonSerializer.Serialize", 2), TestContext.Current.CancellationToken);
        Assert.Contains("JsonSerializer", hover?.Contents.Value, StringComparison.Ordinal);
        await LanguageFixture.WaitUntilAsync(() => fixture.Languages.Diagnostics.For(program).Any(d => d.Code == "CS0246"), TimeSpan.FromSeconds(60));
    }
}
