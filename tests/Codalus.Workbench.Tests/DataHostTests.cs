using System.Diagnostics;
using System.Reflection;
using Codalus.Data.Protocol;
using Codalus.Platform.Credentials;
using Codalus.Rpc;
using Codalus.Workbench.Data;
using Codalus.Workbench.Secrets;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The data host (M27b, ADR 0037) through <see cref="DataService"/> on SQLite: samples/Data's inventory database for the
/// schema, table data, edits, queries, plans, export and import; a million-row table for the first-page budget.
/// </summary>
public sealed class DataHostTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-data-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCredentialStore _credentials = new();
    private DataService _data = null!;

    internal static string Metadata(string key) =>
        typeof(DataHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    internal static string HostPath => Path.Combine(Metadata("CodalusRepoRoot"), "src", "Codalus.Data.Host", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-datahost.dll");

    private string Inventory => Path.Combine(_root, "inventory.db");

    private DataConnection Connection => new("inventory", "Inventory", DataEngines.Sqlite, "Data Source=" + Inventory);

    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(HostPath), $"Data host not built: {HostPath}");
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(Metadata("CodalusRepoRoot"), "samples", "Data", "Inventory", "inventory.db"), Inventory);
        _data = new DataService(() => ProcessLaunchInfo.FromPath(HostPath), _root, new SecretStorage(_credentials, "Codalus IDE (tests)"));
        await _data.SaveAsync(Connection, null, Timeout());
    }

    public async ValueTask DisposeAsync()
    {
        await _data.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Schema_of_tables_views_keys_and_indexes()
    {
        var connected = await _data.ConnectAsync(Connection, Timeout());
        Assert.StartsWith("3.", connected.ServerVersion, StringComparison.Ordinal);
        var schema = await _data.SchemaAsync("inventory", null, refresh: false, Timeout());
        Assert.Equal(["Categories", "Products", "StockMovements", "Suppliers"], schema.Objects.Where(o => o.Kind == DbObjectKinds.Table).Select(o => o.Name));
        Assert.Equal("ProductsInStock", Assert.Single(schema.Objects, o => o.Kind == DbObjectKinds.View).Name);
        var products = schema.Objects.Single(o => o.Name == "Products");
        Assert.Equal(["ProductId", "Name", "Sku", "CategoryId", "SupplierId", "UnitPrice", "UnitsInStock", "Discontinued"], products.Columns.Select(c => c.Name));
        Assert.True(products.Columns[0] is { IsKey: true, IsIdentity: true });
        Assert.False(products.Columns.Single(c => c.Name == "Name").Nullable);
        Assert.True(products.Columns.Single(c => c.Name == "SupplierId").Nullable);
        var category = Assert.Single(products.Keys, k => k is { Kind: "foreign", ReferencedTable: "Categories" });
        Assert.Equal(["CategoryId"], category.Columns);
        Assert.Contains(products.Indexes, i => i is { IsUnique: true, Columns: ["Sku"] });
        Assert.Contains(products.Indexes, i => i is { Name: "IX_Products_CategoryId", IsUnique: false });
        Assert.Same(schema, _data.CachedSchema("inventory"));
    }

    [Fact]
    public async Task Table_pages_edits_and_conflicts()
    {
        var table = new DbTableRef(null, "Products");
        var page = await _data.ReadAsync(new ReadParams("inventory", null, table, 0, 2), Timeout());
        Assert.Equal(["ProductId"], page.KeyColumns);
        Assert.False(page.ReadOnly);
        Assert.Equal(2, page.Rows.Length);
        Assert.Equal("1|Chai|BEV-001|1|1|18|39|0", string.Join('|', page.Rows[0]));
        Assert.Equal(5, (await _data.CountAsync(new CountParams("inventory", null, table), Timeout())).Count);
        var second = await _data.ReadAsync(new ReadParams("inventory", null, table, 2, 2), Timeout());
        Assert.Equal("3", second.Rows[0][0]);
        var filtered = await _data.ReadAsync(new ReadParams("inventory", null, table, 0, 10, Filter: "CategoryId = 2"), Timeout());
        Assert.Equal(["3", "4"], filtered.Rows.Select(r => r[0]));
        Assert.True((await _data.ReadAsync(new ReadParams("inventory", null, new DbTableRef(null, "ProductsInStock"), 0, 10), Timeout())).ReadOnly);

        // Update, insert and delete in one transaction; NULL is kept apart from an empty string.
        var result = await _data.CommitAsync(new CommitParams("inventory", null, table,
        [
            new RowChange(RowChangeKinds.Update, new() { ["ProductId"] = "1" }, new() { ["Name"] = "Masala Chai", ["SupplierId"] = null }),
            new RowChange(RowChangeKinds.Insert, Values: new() { ["Name"] = "Ikura", ["Sku"] = "SEA-001", ["CategoryId"] = "3", ["UnitPrice"] = "31.5" }),
            new RowChange(RowChangeKinds.Delete, new() { ["ProductId"] = "5" }),
        ]), Timeout());
        Assert.Equal(3, result.Affected);
        var after = await _data.ReadAsync(new ReadParams("inventory", null, table, 0, 10), Timeout());
        Assert.Equal("Masala Chai", after.Rows[0][1]);
        Assert.Null(after.Rows[0][4]);
        Assert.Contains(after.Rows, r => r[1] == "Ikura" && r[5] == "31.5");
        Assert.DoesNotContain(after.Rows, r => r[0] == "5");

        // A row deleted by someone else: nothing is committed.
        var conflict = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _data.CommitAsync(new CommitParams("inventory", null, table,
        [
            new RowChange(RowChangeKinds.Update, new() { ["ProductId"] = "2" }, new() { ["Name"] = "Changed" }),
            new RowChange(RowChangeKinds.Delete, new() { ["ProductId"] = "5" }),
        ]), Timeout()));
        Assert.Equal(DataErrorCodes.Conflict, conflict.Code);
        Assert.Equal("Chang", (await _data.ReadAsync(new ReadParams("inventory", null, table, 1, 1), Timeout())).Rows[0][1]);

        // A value that isn't the column's type.
        var bad = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _data.CommitAsync(new CommitParams("inventory", null, table, [new RowChange(RowChangeKinds.Update, new() { ["ProductId"] = "2" }, new() { ["UnitsInStock"] = "many" })]), Timeout()));
        Assert.Contains("'many' isn't a valid whole number", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queries_messages_sessions_plans_and_history()
    {
        var result = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT Name, UnitPrice FROM Products ORDER BY ProductId; UPDATE Products SET UnitsInStock = UnitsInStock + 1 WHERE CategoryId = 1;", Session: "q1"), Timeout());
        var set = Assert.Single(result.ResultSets);
        Assert.Equal(["Name", "UnitPrice"], set.Columns.Select(c => c.Name));
        Assert.Equal(5, set.Rows.Length);
        Assert.Contains(result.Messages, m => m.Text == "2 rows affected.");
        Assert.True(result.ElapsedMilliseconds >= 0);

        // A session keeps its connection: a temporary table lives on.
        await _data.ExecuteAsync(new ExecuteParams("inventory", "CREATE TEMP TABLE Scratch (Value INTEGER); INSERT INTO Scratch VALUES (42);", Session: "q1"), Timeout());
        var scratch = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT Value FROM Scratch", Session: "q1"), Timeout());
        Assert.Equal("42", scratch.ResultSets[0].Rows[0][0]);
        var other = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT Value FROM Scratch", Session: "q2"), Timeout());
        Assert.Contains(other.Messages, m => m.Kind == "error" && m.Text.Contains("no such table", StringComparison.Ordinal));

        // Truncated results.
        var cut = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT * FROM Products", MaxRows: 2), Timeout());
        Assert.True(cut.ResultSets[0].Truncated);
        Assert.Equal(2, cut.ResultSets[0].Rows.Length);

        // The estimated plan; SQLite has no actual one.
        var plan = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT p.Name FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId WHERE c.Name = 'Beverages'", Plan: PlanKinds.Estimated), Timeout());
        Assert.NotNull(plan.Plan);
        Assert.Contains(Flatten(plan.Plan!.Roots), n => n.Operation is "Scan" or "Search");
        var actual = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT 1", Plan: PlanKinds.Actual), Timeout()));
        Assert.Equal(DataErrorCodes.NotSupported, actual.Code);

        Assert.Equal("SELECT 1", _data.History[0].Text);
        Assert.False(_data.History[0].Succeeded);
        Assert.True(_data.History.Count >= 5);
        _data.ClearHistory();
        Assert.Empty(_data.History);
    }

    [Fact]
    public async Task A_running_query_is_canceled_in_the_database()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        var run = _data.ExecuteAsync(new ExecuteParams("inventory", "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n"), cancellation.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Cancel took {watch.Elapsed}.");

        // The host goes on.
        Assert.Equal("1", (await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT 1"), Timeout())).ResultSets[0].Rows[0][0]);
    }

    [Fact]
    public async Task Export_and_import_as_csv_and_json()
    {
        var table = new DbTableRef(null, "Categories");
        var csv = Path.Combine(_root, "categories.csv");
        var json = Path.Combine(_root, "categories.json");
        Assert.Equal(3, (await _data.ExportAsync(new ExportParams("inventory", null, "csv", csv, table), Timeout())).Rows);
        Assert.Equal(3, (await _data.ExportAsync(new ExportParams("inventory", null, "json", json, table), Timeout())).Rows);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("CategoryId,Name,Description", lines[0]);
        Assert.Equal("1,Beverages,\"Soft drinks, coffees, teas\"", lines[1]);
        Assert.Contains("\"CategoryId\": 1", File.ReadAllText(json), StringComparison.Ordinal);

        await _data.ExecuteAsync(new ExecuteParams("inventory", "CREATE TABLE Copy1 (CategoryId INTEGER PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NULL); CREATE TABLE Copy2 (CategoryId INTEGER PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NULL);"), Timeout());
        Assert.Equal(3, (await _data.ImportAsync(new ImportParams("inventory", null, new DbTableRef(null, "Copy1"), "csv", csv), Timeout())).Rows);
        Assert.Equal(3, (await _data.ImportAsync(new ImportParams("inventory", null, new DbTableRef(null, "Copy2"), "json", json), Timeout())).Rows);
        var copied = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT a.Description FROM Copy1 a JOIN Copy2 b USING (CategoryId) WHERE a.CategoryId = 1 AND a.Description = b.Description"), Timeout());
        Assert.Equal("Soft drinks, coffees, teas", copied.ResultSets[0].Rows[0][0]);

        // A failing row imports nothing.
        File.WriteAllText(Path.Combine(_root, "bad.csv"), "CategoryId,Name\n10,Ten\n11,\n");
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _data.ImportAsync(new ImportParams("inventory", null, new DbTableRef(null, "Copy1"), "csv", Path.Combine(_root, "bad.csv")), Timeout()));
        Assert.Equal(3, (await _data.CountAsync(new CountParams("inventory", null, new DbTableRef(null, "Copy1")), Timeout())).Count);
    }

    /// <summary>The roadmap's budget: the first page of a 1M-row table within 500 ms of the server's answer.</summary>
    [Fact]
    public async Task The_first_page_of_a_million_rows_is_fast()
    {
        await _data.ExecuteAsync(new ExecuteParams("inventory", """
            CREATE TABLE Big (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Amount REAL NOT NULL);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000000)
            INSERT INTO Big SELECT i, 'Row ' || i, i * 0.5 FROM n;
            """), Timeout(300));
        var table = new DbTableRef(null, "Big");
        await _data.ReadAsync(new ReadParams("inventory", null, table, 0, 200), Timeout()); // warm
        var watch = Stopwatch.StartNew();
        var page = await _data.ReadAsync(new ReadParams("inventory", null, table, 0, 200), Timeout());
        var roundTrip = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(200, page.Rows.Length);
        var plan = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT * FROM \"Big\" ORDER BY \"Id\" LIMIT 200 OFFSET 0", Plan: PlanKinds.Estimated), Timeout());
        // The host's time is the budget everywhere; the round trip also counts the test process's thread pool, which a
        // loaded CI runner stalls (1.8 s with 2 ms in the host on the Windows runner), so it is held to it locally only.
        Assert.True(page.ElapsedMilliseconds < 500 && (roundTrip < 500 || Environment.GetEnvironmentVariable("CI") == "true"), $"The first page took {roundTrip:0} ms ({page.ElapsedMilliseconds:0} ms in the host; plan {string.Join(", ", plan.Plan!.Roots.Select(r => r.Detail))}).");
        var last = await _data.ReadAsync(new ReadParams("inventory", null, table, 999_800, 200), Timeout());
        Assert.Equal("1000000", last.Rows[^1][0]);
        Assert.Equal(1_000_000, (await _data.CountAsync(new CountParams("inventory", null, table), Timeout())).Count);
        Console.WriteLine($"[budget] data first page: {roundTrip:0} ms round trip, {page.ElapsedMilliseconds:0} ms in the host");
    }

    [Fact]
    public async Task The_host_restarts_after_a_crash_and_connections_come_back()
    {
        await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT 1"), Timeout());
        var pid = _data.HostProcessId!.Value;
        // Kill returns before the process is gone: wait, or the next query can still reach it (seen on the Mac).
        using (var host = Process.GetProcessById(pid))
        {
            host.Kill();
            host.WaitForExit(10_000);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                var result = await _data.ExecuteAsync(new ExecuteParams("inventory", "SELECT 2"), Timeout());
                Assert.Equal("2", result.ResultSets[0].Rows[0][0]);
                break;
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotEqual(pid, _data.HostProcessId);
    }

    [Fact]
    public async Task Passwords_stay_out_of_the_connections_file()
    {
        var server = new DataConnection("pg", "Shop", DataEngines.PostgreSql, "Host=db;Database=shop;Username=app;Password=s3cret", SavePassword: true);
        await _data.SaveAsync(server, null, Timeout());
        var file = File.ReadAllText(Path.Combine(_root, "data-connections.json"));
        Assert.DoesNotContain("s3cret", file, StringComparison.Ordinal);
        Assert.Contains("host=db", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("s3cret", await _credentials.GetAsync("Codalus IDE (tests)", "codalus.data/connection/pg", Timeout()));
        Assert.True(DataService.NeedsPassword(_data.Find("pg")!));
        Assert.Contains("Password=s3cret", await _data.ConnectionStringAsync(_data.Find("pg")!, Timeout()), StringComparison.OrdinalIgnoreCase);

        // Not saved: asked for, then remembered for the session only.
        var unsaved = server with { Id = "pg2", SavePassword = false, ConnectionString = "Host=db;Username=app" };
        await _data.SaveAsync(unsaved, null, Timeout());
        await Assert.ThrowsAsync<DataPasswordRequiredException>(() => _data.ConnectionStringAsync(_data.Find("pg2")!, Timeout()));
        _data.RememberPassword("pg2", "typed");
        Assert.Contains("Password=typed", await _data.ConnectionStringAsync(_data.Find("pg2")!, Timeout()), StringComparison.OrdinalIgnoreCase);
        Assert.Null(await _credentials.GetAsync("Codalus IDE (tests)", "codalus.data/connection/pg2", Timeout()));

        await _data.RemoveAsync("pg", Timeout());
        Assert.Null(await _credentials.GetAsync("Codalus IDE (tests)", "codalus.data/connection/pg", Timeout()));
        Assert.False(DataService.NeedsPassword(new DataConnection("x", "x", DataEngines.SqlServer, "Server=.;Integrated Security=true;User ID=me")));
    }

    private static IEnumerable<PlanNode> Flatten(IEnumerable<PlanNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
