using System.Diagnostics;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Search;

namespace Codalus.Workbench.Tests;

/// <summary>Code Search (M47): the query's prefixes, ranking, the persistent index and its budget.</summary>
public sealed class CodeSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-codesearch-" + Guid.NewGuid().ToString("N"));

    public CodeSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string File(string name, string text = "// code\n")
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private static IndexedDocument Document(string path, string project, params IndexedSymbol[] symbols) => new(DocumentUris.FromPath(path), project, symbols);

    private static IndexedSymbol Symbol(string name, int kind, string? container, int line = 0) => new(name, kind, line, 4) { Container = container };

    [Theory]
    [InlineData("f:Program", CodeSearchFilter.Files, "Program")]
    [InlineData("t: Circle", CodeSearchFilter.Types, "Circle")]
    [InlineData("M:Area", CodeSearchFilter.Members, "Area")]
    [InlineData("x:TODO later", CodeSearchFilter.Text, "TODO later")]
    [InlineData("  Circle ", CodeSearchFilter.All, "Circle")]
    [InlineData("q:Circle", CodeSearchFilter.All, "q:Circle")]
    public void Prefixes_pick_the_filter(string input, CodeSearchFilter filter, string text)
    {
        var query = CodeSearchQuery.Parse(input);
        Assert.Equal(filter, query.Filter);
        Assert.Equal(text, query.Text);
        Assert.Equal(filter != CodeSearchFilter.All, query.FromPrefix);
    }

    [Fact]
    public void A_chip_filters_unless_a_prefix_overrides_it_and_colon_is_a_line()
    {
        Assert.Equal(CodeSearchFilter.Types, CodeSearchQuery.Parse("Circle", CodeSearchFilter.Types).Filter);
        Assert.Equal(CodeSearchFilter.Files, CodeSearchQuery.Parse("f:Circle", CodeSearchFilter.Types).Filter);
        Assert.True(CodeSearchQuery.Parse("Circle", CodeSearchFilter.Types).Wants(CodeSearchKind.Type));
        Assert.False(CodeSearchQuery.Parse("Circle", CodeSearchFilter.Types).Wants(CodeSearchKind.Member));
        Assert.True(CodeSearchQuery.Parse("Circle").Wants(CodeSearchKind.Text));
        Assert.False(CodeSearchQuery.Parse("Circle").Wants(CodeSearchKind.Line));

        var line = CodeSearchQuery.Parse(":42");
        Assert.True(line.IsLine);
        Assert.Equal(42, line.Line);
        Assert.Null(line.Column);
        Assert.Equal((12, 7), (CodeSearchQuery.Parse(":12:7").Line, CodeSearchQuery.Parse(":12,7").Column));
        Assert.Equal(0, CodeSearchQuery.Parse(":").Line); // a line still to type
        Assert.Equal("t:", CodeSearchQuery.PrefixOf(CodeSearchFilter.Types));
    }

    [Fact]
    public void The_index_finds_types_and_members_by_camel_case_with_highlights_kinds_and_scope()
    {
        var shapes = File("Lib/Shapes.cs");
        var program = File("App/Program.cs");
        var index = new CodeSearchIndex();
        index.Replace([
            Document(shapes, "Lib", Symbol("Circle", SymbolKind.Class, "Lib"), Symbol("Radius", SymbolKind.Property, "Circle"), Symbol("CalculateTotalArea", SymbolKind.Method, "Geometry")),
            Document(program, "App", Symbol("Program", SymbolKind.Class, null), Symbol("Main", SymbolKind.Method, "Program")),
        ]);
        Assert.True(index is { IsComplete: true, SymbolCount: 5, DocumentCount: 2 });

        var found = index.Search(CodeSearchQuery.Parse("CTA"), CodeSearchRankContext.Empty, null, 50, Ct);
        var total = Assert.Single(found);
        Assert.Equal(("CalculateTotalArea", CodeSearchKind.Member, "Geometry", "Lib"), (total.Name, total.Kind, total.Container, total.Project));
        Assert.Equal([0, 9, 14], total.Highlights);
        Assert.Equal(shapes, total.Path);

        Assert.Equal(["Circle"], index.Search(CodeSearchQuery.Parse("t:ci"), CodeSearchRankContext.Empty, null, 50, Ct).Select(r => r.Name));
        Assert.DoesNotContain(index.Search(CodeSearchQuery.Parse("m:ci"), CodeSearchRankContext.Empty, null, 50, Ct), r => r.Name == "Circle");
        Assert.Empty(index.Search(CodeSearchQuery.Parse("f:Circle"), CodeSearchRankContext.Empty, null, 50, Ct));
        Assert.Empty(index.Search(CodeSearchQuery.Parse("Circle"), CodeSearchRankContext.Empty, p => p == program, 50, Ct)); // the current document's scope

        // An edit re-indexes one document; a removed file goes.
        index.Update([Document(program, "App", Symbol("Program", SymbolKind.Class, null), Symbol("Run", SymbolKind.Method, "Program"))]);
        Assert.Contains(index.Search(CodeSearchQuery.Parse("Run"), CodeSearchRankContext.Empty, null, 50, Ct), r => r.Name == "Run");
        Assert.DoesNotContain(index.Search(CodeSearchQuery.Parse("Main"), CodeSearchRankContext.Empty, null, 50, Ct), r => r.Name == "Main");
        index.Update([], [shapes]);
        Assert.Empty(index.Search(CodeSearchQuery.Parse("Circle"), CodeSearchRankContext.Empty, null, 50, Ct));
    }

    [Fact]
    public void Ranking_prefers_exact_names_types_recent_files_and_the_active_project()
    {
        var a = File("A/Widget.cs");
        var b = File("B/WidgetFactory.cs");
        var c = File("C/Other.cs");
        var index = new CodeSearchIndex();
        index.Replace([
            Document(a, "A", Symbol("Widget", SymbolKind.Class, "A"), Symbol("Widget", SymbolKind.Property, "Holder")),
            Document(b, "B", Symbol("WidgetFactory", SymbolKind.Class, "B"), Symbol("Wide", SymbolKind.Method, "B")),
            Document(c, "C", Symbol("WidgetFactory", SymbolKind.Class, "C")),
        ]);

        var plain = index.Search(CodeSearchQuery.Parse("Widget"), CodeSearchRankContext.Empty, null, 50, Ct);
        Assert.Equal(("Widget", CodeSearchKind.Type), (plain[0].Name, plain[0].Kind)); // exact, and a type before the member of the same name
        Assert.Equal(("Widget", CodeSearchKind.Member), (plain[1].Name, plain[1].Kind));
        Assert.DoesNotContain(plain, r => r.Name == "Wide");

        // Of two equal matches, the recently opened file's and the active project's come first.
        var factories = index.Search(CodeSearchQuery.Parse("WidgetFactory"), new CodeSearchRankContext([c]), null, 50, Ct);
        Assert.Equal(c, factories[0].Path);
        factories = index.Search(CodeSearchQuery.Parse("WidgetFactory"), new CodeSearchRankContext([], ActiveProject: "B"), null, 50, Ct);
        Assert.Equal(b, factories[0].Path);

        // Files: by name, the folder as the container; a path query matches the relative path.
        var files = CodeSearchIndex.SearchFiles([a, b, c], CodeSearchQuery.Parse("wf"), CodeSearchRankContext.Empty, _root, _ => "B", 10, Ct);
        var factory = Assert.Single(files);
        Assert.Equal(("WidgetFactory.cs", Path.Combine("src", "B"), "B"), (factory.Name, factory.Container, factory.Project));
        Assert.Equal([0, 6], factory.Highlights);
        Assert.Equal([c], CodeSearchIndex.SearchFiles([a, b, c], CodeSearchQuery.Parse("f:c/oth"), CodeSearchRankContext.Empty, _root, null, 10, Ct).Select(r => r.Path));
        Assert.Empty(CodeSearchIndex.SearchFiles([a, b, c], CodeSearchQuery.Parse("t:Widget"), CodeSearchRankContext.Empty, _root, null, 10, Ct));
    }

    [Fact]
    public async Task The_index_is_saved_next_to_the_solution_and_read_without_the_files_that_changed()
    {
        var shapes = File("Lib/Shapes.cs", "class Circle {}\n");
        var program = File("App/Program.cs", "class Program {}\n");
        var store = new SolutionStateStore(Path.Combine(_root, "user-state"));
        var key = new SolutionStateKey(Path.Combine(_root, "All.slnx"), IsFolder: false);
        var index = new CodeSearchIndex();
        index.SetFiles([program, shapes]);
        index.Replace([Document(shapes, "Lib", Symbol("Circle", SymbolKind.Class, "Lib")), Document(program, "App", Symbol("Program", SymbolKind.Class, null))]);
        await index.SaveAsync(store, key);
        Assert.True(System.IO.File.Exists(Path.Combine(key.Directory, CodeSearchIndex.FileName)));

        // The next session: everything, before any language service, but not complete.
        var next = new CodeSearchIndex();
        var changed = 0;
        next.Changed += (_, _) => changed++;
        Assert.True(await next.LoadAsync(store, key, Ct));
        Assert.True(next is { IsLoaded: true, IsComplete: false, SymbolCount: 2 });
        Assert.Equal([program, shapes], next.Files);
        Assert.Equal(1, changed);
        Assert.Single(next.Search(CodeSearchQuery.Parse("Circle"), CodeSearchRankContext.Empty, null, 10, Ct));

        // A file changed meanwhile: its symbols wait for the language host.
        await System.IO.File.AppendAllTextAsync(shapes, "class Square {}\n", Ct);
        var third = new CodeSearchIndex();
        Assert.True(await third.LoadAsync(store, key, Ct));
        Assert.Empty(third.Search(CodeSearchQuery.Parse("Circle"), CodeSearchRankContext.Empty, null, 10, Ct));
        Assert.Single(third.Search(CodeSearchQuery.Parse("Program"), CodeSearchRankContext.Empty, null, 10, Ct));

        // None saved for another solution.
        Assert.False(await new CodeSearchIndex().LoadAsync(store, new SolutionStateKey(Path.Combine(_root, "Other.slnx"), IsFolder: false), Ct));
    }

    /// <summary>
    /// The roadmap's budget: results within 100 ms of typing once indexed, on the 96-project benchmark solution. This
    /// index has its shape: 96 projects of 60 files (FixtureGen's solution generator with 96 projects writes classes of
    /// about twenty members), 5,760 files and over 140,000 symbols.
    /// </summary>
    [Fact]
    public void A_query_on_a_96_project_index_answers_within_100_ms()
    {
        var documents = new List<IndexedDocument>();
        var files = new List<string>();
        var words = new[] { "Order", "Customer", "Invoice", "Payment", "Shipment", "Catalog", "Inventory", "Report", "Account", "Session", "Widget", "Ledger" };
        for (var p = 0; p < 96; p++)
        {
            var project = $"Company.Module{p:00}";
            for (var f = 0; f < 60; f++)
            {
                var type = $"{words[(p + f) % words.Length]}{words[f % words.Length]}Service{p}x{f}";
                var path = Path.Combine(_root, "solution", project, type + ".cs");
                files.Add(path);
                var symbols = new List<IndexedSymbol> { Symbol(type, SymbolKind.Class, project) };
                for (var m = 0; m < 24; m++)
                {
                    symbols.Add(Symbol($"{(m % 3 == 0 ? "Get" : m % 3 == 1 ? "Update" : "Validate")}{words[(m + f) % words.Length]}{m}Async", m % 4 == 0 ? SymbolKind.Property : SymbolKind.Method, type, m));
                }

                documents.Add(Document(path, project, [.. symbols]));
            }
        }

        var index = new CodeSearchIndex();
        index.Replace(documents);
        Assert.True(index.SymbolCount > 140_000, index.SymbolCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var context = new CodeSearchRankContext(files.Take(10).ToList(), files[0], "Company.Module00");

        string[] queries = ["Inv", "CustomerService", "GUA", "ValidateLedger7", "zzzq", "t:Order", "m:upd"];
        index.Search(CodeSearchQuery.Parse("warm"), context, null, 200, Ct); // JIT
        CodeSearchIndex.SearchFiles(files, CodeSearchQuery.Parse("warm"), context, _root, null, 200, Ct);
        foreach (var text in queries)
        {
            var times = new List<double>();
            for (var run = 0; run < 5; run++)
            {
                var watch = Stopwatch.StartNew();
                var query = CodeSearchQuery.Parse(text);
                var results = index.Search(query, context, null, 200, Ct);
                var fileResults = CodeSearchIndex.SearchFiles(files, query, context, _root, null, 50, Ct);
                times.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(text == "zzzq" ? results.Count == 0 : results.Count + fileResults.Count > 0, text);
            }

            var median = times.Order().ElementAt(times.Count / 2);
            Assert.True(median < 100, $"'{text}' took {median:F1} ms (median of {string.Join(", ", times.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))})");
        }
    }
}
