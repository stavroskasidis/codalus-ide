using DaedalusStudio.Data.Host.Engines;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// The SQL language server (M27b, ADR 0038): <c>daedalus-datahost --lsp</c> through the workbench's LSP bridge, with a
/// schema sent as <c>daedalus/sql/context</c>; and the plan parsers and batch splitting of the data host.
/// </summary>
public sealed class SqlLanguageServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-sql-" + Guid.NewGuid().ToString("N"));
    private readonly LanguageRegistry _languages = new();
    private readonly LanguageServiceRegistry _servers = new();
    private readonly SolutionService _solutions = new(() => null);
    private LanguageService _service = null!;
    private int _version;

    private static readonly SqlSchemaObject[] s_schema =
    [
        new("dbo", "Products", "table", [new("ProductId", "int", true, false), new("Name", "nvarchar(100)", false, false), new("CategoryId", "int", false, false)]),
        new("dbo", "Categories", "table", [new("CategoryId", "int", true, false), new("Title", "nvarchar(50)", false, true)]),
        new("sales", "Orders", "table", [new("OrderId", "int", true, false)]),
        new("dbo", "ProductsInStock", "view", [new("Name", "nvarchar(100)", false, false)]),
    ];

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(DataHostTests.HostPath), $"Data host not built: {DataHostTests.HostPath}");
        Directory.CreateDirectory(_root);
        _languages.Register(new LanguageDefinition("sql", "SQL") { Extensions = [".sql"] });
        _servers.Register(new LanguageServiceDescriptor("sql", "SQL", [], null, () => ProcessLaunchInfo.FromPath(DataHostTests.HostPath, "--lsp")) { Languages = ["sql"] });
        _service = new LanguageService(_servers, _solutions, languages: _languages);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(string Path, string Uri, LanguageHostClient Client)> OpenAsync(string name, string text, string dialect)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        var version = ++_version;
        var client = (await _service.ClientAsync("sql"))!;
        var uri = DocumentUris.FromPath(path);
        await client.SqlContextAsync(new SqlContextParams(uri, dialect, "dbo", s_schema), Timeout());
        Assert.True(await _service.OpenAsync(new LanguageDocument(path, "sql", () => (text, version))));
        return (path, uri, client);
    }

    private static Position End(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var line = text[..offset].Count(c => c == '\n');
        return new Position(line, offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1));
    }

    [Fact]
    public async Task Completion_and_hover_come_from_the_schema()
    {
        var text = "SELECT p. FROM Products AS p JOIN sales.Orders o ON 1 = 1\nSELECT  FROM Categories\nSELECT * FROM sales.";
        var (_, uri, client) = await OpenAsync("query.sql", text, "sqlserver");

        // alias. → the aliased table's columns.
        var columns = await client.CompletionAsync(uri, End(text, "SELECT p."), null, Timeout());
        Assert.Equal(["ProductId", "Name", "CategoryId"], columns.Items.Select(i => i.Label));

        // In a statement: its tables' columns first, then objects (other schemas qualified), schemas and keywords.
        var all = await client.CompletionAsync(uri, End(text, "\nSELECT "), null, Timeout());
        var labels = all.Items.Select(i => i.Label).ToList();
        Assert.Contains("Title", labels);
        Assert.Contains("Products", labels);
        Assert.Contains("sales.Orders", labels);
        Assert.Contains("ProductsInStock", labels);
        Assert.Contains("SELECT", labels);
        Assert.Contains("TOP", labels); // T-SQL keywords
        Assert.True(labels.IndexOf("Title") < labels.IndexOf("Products"));

        // The roadmap's budget: completion from a cached schema within 100 ms (the median of 20, through the bridge).
        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.CompletionAsync(uri, End(text, "\nSELECT "), null, Timeout());
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        Assert.True(times[10] < 100, $"Schema completion took {times[10]:0} ms (median).");
        Console.WriteLine($"[budget] sql schema completion: median {times[10]:0.0} ms, max {times[^1]:0.0} ms");

        // schema. → its objects.
        var schema = await client.CompletionAsync(uri, End(text, "FROM sales."), null, Timeout());
        Assert.Equal("Orders", Assert.Single(schema.Items).Label);

        var hover = await client.HoverAsync(uri, End(text, "FROM Produ"), Timeout());
        Assert.Contains("CategoryId", hover!.Contents.Value, StringComparison.Ordinal);
        var column = await client.HoverAsync(uri, End(text, "JOIN sales.Orders o ON 1 = 1\nSELECT  FROM Cat"), Timeout());
        Assert.Contains("Title", column!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TSql_gets_parse_errors_and_formatting_and_other_dialects_only_completion()
    {
        var (path, _, _) = await OpenAsync("broken.sql", "SELEC Name FROM Products;\n", "sqlserver");
        Diagnostic[] diagnostics;
        while ((diagnostics = [.. _service.Diagnostics.For(path)]).Length == 0)
        {
            await Task.Delay(20, Timeout());
        }

        Assert.Equal(0, diagnostics[0].Range.Start.Line);
        Assert.Equal("T-SQL", diagnostics[0].Source);

        var text = "select p.Name,c.Title from Products p join Categories c on c.CategoryId=p.CategoryId where p.ProductId>10";
        var (_, uri, client) = await OpenAsync("format.sql", text, "sqlserver");
        var edits = await client.FormatAsync(uri, null, new FormattingOptions(4, true), Timeout());
        var formatted = Assert.Single(edits).NewText;
        Assert.Contains("SELECT p.Name,", formatted, StringComparison.Ordinal);
        Assert.Contains("\nFROM", formatted, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN", formatted, StringComparison.Ordinal);
        Assert.Contains("\nWHERE p.ProductId > 10;", formatted, StringComparison.Ordinal);

        // Comments would be lost: no formatting.
        var (_, commented, _) = await OpenAsync("commented.sql", "-- keep me\nselect 1", "sqlserver");
        Assert.Empty(await client.FormatAsync(commented, null, new FormattingOptions(4, true), Timeout()));

        // PostgreSQL: its keywords, no T-SQL errors.
        var pgText = "SELEC 1; SELECT * FROM Products LIM";
        var (pg, pgUri, _) = await OpenAsync("pg.sql", pgText, "postgresql");
        var keywords = await client.CompletionAsync(pgUri, End(pgText, "LIM"), null, Timeout());
        Assert.Contains(keywords.Items, i => i.Label == "LIMIT");
        Assert.DoesNotContain(keywords.Items, i => i.Label == "TOP");
        await Task.Delay(800, Timeout());
        Assert.Empty(_service.Diagnostics.For(pg));
    }

    [Fact]
    public void Plans_of_each_engine_become_trees()
    {
        var showplan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT * FROM t" StatementType="SELECT" StatementSubTreeCost="1.0" StatementEstRows="10">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimatedTotalSubtreeCost="1.0">
                      <NestedLoops>
                        <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="100" EstimatedTotalSubtreeCost="0.75">
                          <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="98" /></RunTimeInformation>
                          <IndexScan><Object Schema="[dbo]" Table="[t]" Index="[PK_t]" /></IndexScan>
                        </RelOp>
                        <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.2" />
                      </NestedLoops>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var statement = Assert.Single(Plans.SqlServer([showplan]));
        var join = Assert.Single(statement.Children);
        Assert.Equal("Nested Loops", join.Operation);
        Assert.Equal(5, join.CostPercent); // 1.0 - 0.75 - 0.2
        Assert.Equal(["Clustered Index Scan", "Index Seek"], join.Children.Select(c => c.Operation));
        Assert.Equal(98, join.Children[0].ActualRows);
        Assert.Equal(75, join.Children[0].CostPercent);
        Assert.Contains("dbo.t.PK_t", join.Children[0].Detail, StringComparison.Ordinal);

        var postgres = Plans.Postgres("""[{"Plan": {"Node Type": "Hash Join", "Total Cost": 10.0, "Plan Rows": 5, "Plans": [{"Node Type": "Seq Scan", "Relation Name": "products", "Schema": "public", "Total Cost": 4.0, "Plan Rows": 100, "Actual Rows": 50, "Actual Loops": 2}]}}]""");
        var hash = Assert.Single(postgres);
        Assert.Equal(60, hash.CostPercent);
        Assert.Equal("public.products", hash.Children[0].Detail);
        Assert.Equal(100, hash.Children[0].ActualRows);

        var tree = Plans.MySqlTree("-> Nested loop inner join  (cost=2.5 rows=3) (actual time=0.1..0.2 rows=3 loops=1)\n    -> Table scan on c  (cost=0.5 rows=3) (actual time=0.05..0.06 rows=3 loops=1)\n    -> Index lookup on p using fk (id=c.id)  (cost=0.3 rows=1)");
        var loop = Assert.Single(tree);
        Assert.Equal("Nested loop inner join", loop.Operation);
        Assert.Equal(3, loop.ActualRows);
        Assert.Equal(2, loop.Children.Length);

        var json = Plans.MySqlJson("""{"query_block": {"select_id": 1, "cost_info": {"query_cost": "1.2"}, "table": {"table_name": "products", "access_type": "ALL", "rows_examined_per_scan": 5}}}""");
        var table = Assert.Single(Assert.Single(Assert.Single(json).Children).Children);
        Assert.Equal("table", table.Operation);
        Assert.Contains("products", table.Detail, StringComparison.Ordinal);
        Assert.Equal(5, table.EstimatedRows);

        var sqlite = Plans.Sqlite([(2, 0, "SCAN p"), (5, 0, "SEARCH c USING INTEGER PRIMARY KEY (rowid=?)"), (7, 5, "USE TEMP B-TREE FOR ORDER BY")]);
        Assert.Equal(["Scan", "Search"], sqlite.Select(n => n.Operation));
        Assert.Equal("Use", Assert.Single(sqlite[1].Children).Operation);
    }

    [Fact]
    public void Sql_server_batches_split_on_go()
    {
        var batches = new SqlServerEngine().Batches("CREATE TABLE t (a int)\nGO\nINSERT t VALUES (1)\ngo 2\n-- tail\nSELECT * FROM t\n");
        Assert.Equal(4, batches.Count);
        Assert.Equal(batches[1], batches[2]);
        Assert.Contains("SELECT * FROM t", batches[3], StringComparison.Ordinal);
        Assert.Single(new SqlServerEngine().Batches("SELECT 'GO'"));
    }
}
