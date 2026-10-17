using System.Diagnostics;
using Codalus.Data.Protocol;
using Codalus.Platform.Credentials;
using Codalus.Rpc;
using Codalus.Workbench.Data;
using Codalus.Workbench.Secrets;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A database server in a throwaway container (docker, or podman's docker command), removed afterwards. Without a
/// container runtime, or when the image can't run here (SQL Server needs 2 GB of memory), the test is skipped with the
/// reason; in CI it fails instead.
/// </summary>
internal sealed class ContainerDatabase : IAsyncDisposable
{
    private readonly string _id;

    private ContainerDatabase(string id, int port)
    {
        _id = id;
        Port = port;
    }

    public int Port { get; }

    public static async Task<ContainerDatabase> StartAsync(string image, int containerPort, string[] environment, string[]? command = null)
    {
        if (Run("version", "--format", "{{.Server.Version}}") is not (0, _))
        {
            Skip("No container runtime (docker or podman) is available");
        }

        var name = "codalus-test-" + Guid.NewGuid().ToString("N")[..12];
        var arguments = new List<string> { "run", "-d", "--rm", "--name", name, "-p", $"127.0.0.1::{containerPort}" };
        foreach (var variable in environment)
        {
            arguments.AddRange(["-e", variable]);
        }

        arguments.Add(image);
        arguments.AddRange(command ?? []);
        var (code, output) = Run([.. arguments], timeoutSeconds: 600);
        if (code != 0)
        {
            Run("rm", "-f", name); // a run that timed out (pulling) may still start it
            Skip($"The container {image} didn't start: {output.Trim()}");
        }

        var id = name; // not the printed id: a docker shim may print more than the id
        var (portCode, mapping) = Run("port", id, $"{containerPort}/tcp");
        var port = 0;
        if (portCode != 0 || !int.TryParse(mapping.Trim().Split('\n')[0].Split(':')[^1], out port))
        {
            Run("rm", "-f", id);
            Skip($"The container {image} has no port mapping: {mapping.Trim()}");
        }

        return new ContainerDatabase(id, port);
    }

    /// <summary>Waits until the server answers, or skips (with the container's log) after <paramref name="seconds"/>.</summary>
    public async Task WaitAsync(DataService data, string engine, string connectionString, int seconds = 180)
    {
        var watch = Stopwatch.StartNew();
        string? error = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var test = await data.TestAsync(engine, connectionString, CancellationToken.None);
            if (test.Succeeded)
            {
                return;
            }

            error = test.Error;
            if (Run("inspect", "-f", "{{.State.Running}}", _id) is not (0, var running) || running.Trim() != "true")
            {
                break;
            }

            await Task.Delay(2000);
        }

        var log = Run("logs", "--tail", "15", _id).Output;
        await DisposeAsync();
        Skip($"The {engine} container never accepted connections ({error}). Its log: {log.Trim()}");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Skip(string reason)
    {
        // CI runs Linux containers on its Linux runner only (M27's plan); the Windows runner's Docker runs Windows
        // containers, and the macOS runner has no engine.
        if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
        {
            Assert.Fail(reason);
        }

        Assert.Skip(reason);
        throw new InvalidOperationException(reason);
    }

    private static (int Code, string Output) Run(params string[] arguments) => Run(arguments, 60);

    private static (int Code, string Output) Run(string[] arguments, int timeoutSeconds)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            // podman's docker shim notes on stderr that it emulates docker: not output to parse.
            var notes = string.Join('\n', stderr.Result.Split('\n').Where(l => !l.StartsWith("Emulate Docker CLI using podman", StringComparison.Ordinal)));
            return (process.ExitCode, stdout.Result + notes);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        Run("rm", "-f", _id);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The data host against real servers in containers (M27b, ADR 0037): PostgreSQL, MySQL, MariaDB and SQL Server, each
/// with its schema, table edits, messages, plans and cancellation. Skipped where no container runtime can run them.
/// </summary>
public sealed class ContainerEngineTests : IAsyncLifetime
{
    private const string Password = "Codalus-Test-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-engines-" + Guid.NewGuid().ToString("N"));
    private DataService _data = null!;

    private static CancellationToken Timeout(int seconds = 120) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _data = new DataService(() => ProcessLaunchInfo.FromPath(DataHostTests.HostPath), _root, new SecretStorage(new InMemoryCredentialStore(), "Codalus IDE (tests)"));
        return ValueTask.CompletedTask;
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
    public async Task PostgreSql()
    {
        await using var server = await ContainerDatabase.StartAsync("docker.io/library/postgres:17-alpine", 5432, [$"POSTGRES_PASSWORD={Password}", "POSTGRES_DB=shop"]);
        var connectionString = $"Host=127.0.0.1;Port={server.Port};Database=shop;Username=postgres;Password={Password}";
        await server.WaitAsync(_data, DataEngines.PostgreSql, connectionString);
        await ExerciseAsync(DataEngines.PostgreSql, connectionString, """
            CREATE TABLE categories (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE products (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name varchar(100) NOT NULL, category_id integer NOT NULL REFERENCES categories (id), price numeric(10,2));
            CREATE UNIQUE INDEX ix_products_name ON products (name);
            CREATE VIEW cheap AS SELECT name FROM products WHERE price < 10;
            CREATE FUNCTION product_count() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM products';
            INSERT INTO categories VALUES (1, 'Tea'), (2, 'Coffee');
            INSERT INTO products (name, category_id, price) VALUES ('Chai', 1, 18), ('Earl Grey', 1, 9.5), ('Espresso', 2, 3);
            """,
            schema: "public",
            notice: "DO $$ BEGIN RAISE NOTICE 'hello from postgres'; END $$;",
            noticeText: "hello from postgres",
            slow: "SELECT pg_sleep(60)",
            insertId: false);
    }

    [Fact]
    public async Task MySql()
    {
        await using var server = await ContainerDatabase.StartAsync("docker.io/library/mysql:8.4", 3306, [$"MYSQL_ROOT_PASSWORD={Password}", "MYSQL_DATABASE=shop"]);
        var connectionString = $"Server=127.0.0.1;Port={server.Port};Database=shop;User ID=root;Password={Password};AllowPublicKeyRetrieval=true;SslMode=None";
        await server.WaitAsync(_data, DataEngines.MySql, connectionString);
        await ExerciseMySqlAsync(connectionString);
    }

    [Fact]
    public async Task MariaDb()
    {
        await using var server = await ContainerDatabase.StartAsync("docker.io/library/mariadb:11", 3306, [$"MARIADB_ROOT_PASSWORD={Password}", "MARIADB_DATABASE=shop"]);
        var connectionString = $"Server=127.0.0.1;Port={server.Port};Database=shop;User ID=root;Password={Password};SslMode=None";
        await server.WaitAsync(_data, DataEngines.MySql, connectionString);
        await ExerciseMySqlAsync(connectionString);
    }

    private Task ExerciseMySqlAsync(string connectionString) => ExerciseAsync(DataEngines.MySql, connectionString, """
        CREATE TABLE categories (id int PRIMARY KEY, name varchar(50) NOT NULL);
        CREATE TABLE products (id int AUTO_INCREMENT PRIMARY KEY, name varchar(100) NOT NULL, category_id int NOT NULL, price decimal(10,2), UNIQUE KEY ix_products_name (name), CONSTRAINT fk_category FOREIGN KEY (category_id) REFERENCES categories (id));
        CREATE VIEW cheap AS SELECT name FROM products WHERE price < 10;
        CREATE FUNCTION product_count() RETURNS int READS SQL DATA RETURN (SELECT count(*) FROM products);
        INSERT INTO categories VALUES (1, 'Tea'), (2, 'Coffee');
        INSERT INTO products (name, category_id, price) VALUES ('Chai', 1, 18), ('Earl Grey', 1, 9.5), ('Espresso', 2, 3);
        """,
        schema: null,
        notice: "SELECT * FROM no_such_table",
        noticeText: null,
        slow: "SELECT SLEEP(60)",
        insertId: false);

    [Fact]
    public async Task SqlServer()
    {
        await using var server = await ContainerDatabase.StartAsync("mcr.microsoft.com/mssql/server:2022-latest", 1433, ["ACCEPT_EULA=Y", $"MSSQL_SA_PASSWORD={Password}", "MSSQL_PID=Developer"]);
        var master = $"Server=127.0.0.1,{server.Port};User ID=sa;Password={Password};TrustServerCertificate=true";
        await server.WaitAsync(_data, DataEngines.SqlServer, master, seconds: 240);
        await _data.SaveAsync(new DataConnection("master", "master", DataEngines.SqlServer, master), null, Timeout());
        await _data.ExecuteAsync(new ExecuteParams("master", "CREATE DATABASE shop"), Timeout());
        await ExerciseAsync(DataEngines.SqlServer, master + ";Database=shop", """
            CREATE TABLE dbo.categories (id int PRIMARY KEY, name nvarchar(50) NOT NULL);
            CREATE TABLE dbo.products (id int IDENTITY PRIMARY KEY, name nvarchar(100) NOT NULL, category_id int NOT NULL REFERENCES dbo.categories (id), price decimal(10,2));
            CREATE UNIQUE INDEX ix_products_name ON dbo.products (name);
            GO
            CREATE VIEW dbo.cheap AS SELECT name FROM dbo.products WHERE price < 10;
            GO
            CREATE FUNCTION dbo.product_count() RETURNS int AS BEGIN RETURN (SELECT count(*) FROM dbo.products) END;
            GO
            INSERT INTO dbo.categories VALUES (1, 'Tea'), (2, 'Coffee');
            INSERT INTO dbo.products (name, category_id, price) VALUES ('Chai', 1, 18), ('Earl Grey', 1, 9.5), ('Espresso', 2, 3);
            """,
            schema: "dbo",
            notice: "PRINT 'hello from sql server'",
            noticeText: "hello from sql server",
            // A minute of short waits, not one WAITFOR of a minute: SQL Server may end a WAITFOR before its time (it
            // ends WAITFOR threads when it runs short of workers), and one that ended early would leave nothing to cancel.
            slow: "DECLARE @waits int = 0; WHILE @waits < 600 BEGIN WAITFOR DELAY '00:00:00.100'; SET @waits += 1; END",
            insertId: false);

        // Databases of the server.
        var databases = await _data.DatabasesAsync("master", Timeout());
        Assert.Contains("shop", databases.Databases);
    }

    /// <summary>The same checks on every engine.</summary>
    private async Task ExerciseAsync(string engine, string connectionString, string setup, string? schema, string notice, string? noticeText, string slow, bool insertId)
    {
        _ = insertId;
        await _data.SaveAsync(new DataConnection("db", engine, engine, connectionString), null, Timeout());
        var created = await _data.ExecuteAsync(new ExecuteParams("db", setup), Timeout());
        Assert.DoesNotContain(created.Messages, m => m.Kind == "error");

        var info = await _data.SchemaAsync("db", null, refresh: true, Timeout());
        var products = info.Objects.Single(o => o.Name == "products" && o.Kind == DbObjectKinds.Table);
        Assert.Equal(schema, products.Schema);
        Assert.Equal(["id", "name", "category_id", "price"], products.Columns.Select(c => c.Name));
        Assert.True(products.Columns[0] is { IsKey: true, IsIdentity: true });
        Assert.False(products.Columns[1].Nullable);
        Assert.Contains("100", products.Columns[1].Type, StringComparison.Ordinal);
        var foreign = Assert.Single(products.Keys, k => k.Kind == "foreign");
        Assert.Equal("categories", foreign.ReferencedTable);
        Assert.Equal(["id"], foreign.ReferencedColumns!);
        Assert.Contains(products.Indexes, i => i is { IsUnique: true, Columns: ["name"] });
        Assert.Contains(info.Objects, o => o is { Name: "cheap", Kind: DbObjectKinds.View });
        Assert.Contains(info.Objects, o => o is { Name: "product_count", Kind: DbObjectKinds.Function });

        // A page, an edit and a count.
        var table = new DbTableRef(schema, "products");
        var page = await _data.ReadAsync(new ReadParams("db", null, table, 1, 2), Timeout());
        Assert.Equal(["id"], page.KeyColumns);
        Assert.Equal(["Earl Grey", "Espresso"], page.Rows.Select(r => r[1]!));
        await _data.CommitAsync(new CommitParams("db", null, table,
        [
            new RowChange(RowChangeKinds.Update, new() { ["id"] = "1" }, new() { ["price"] = "19.25", ["name"] = "Masala Chai" }),
            new RowChange(RowChangeKinds.Insert, Values: new() { ["name"] = "Sencha", ["category_id"] = "1", ["price"] = null }),
        ]), Timeout());
        var after = await _data.ReadAsync(new ReadParams("db", null, table, 0, 10), Timeout());
        Assert.Contains(after.Rows, r => r[1] == "Masala Chai" && r[3] == "19.25");
        Assert.Contains(after.Rows, r => r[1] == "Sencha" && r[3] is null);
        Assert.Equal(4, (await _data.CountAsync(new CountParams("db", null, table), Timeout())).Count);

        // Messages: the engine's notices, or its error.
        var messages = await _data.ExecuteAsync(new ExecuteParams("db", notice, Session: "s"), Timeout());
        Assert.Contains(messages.Messages, m => noticeText is null ? m.Kind is "error" or "warning" or "info" : m.Text.Contains(noticeText, StringComparison.Ordinal));

        // Plans: estimated and actual.
        var query = $"SELECT p.name FROM {(schema is null ? string.Empty : schema + ".")}products p JOIN {(schema is null ? string.Empty : schema + ".")}categories c ON c.id = p.category_id WHERE c.name = 'Tea'";
        var estimated = await _data.ExecuteAsync(new ExecuteParams("db", query, Session: "s", Plan: PlanKinds.Estimated), Timeout());
        Assert.NotNull(estimated.Plan);
        Assert.NotEmpty(estimated.Plan!.Roots);
        Assert.NotEmpty(estimated.Plan.Raw);
        var actual = await _data.ExecuteAsync(new ExecuteParams("db", query, Session: "s", Plan: PlanKinds.Actual), Timeout());
        Assert.True(actual.Plan!.Actual);
        Assert.Contains(Flatten(actual.Plan.Roots), n => n.ActualRows is not null || n.EstimatedRows is not null);

        // Cancel a slow statement in the server.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        var run = _data.ExecuteAsync(new ExecuteParams("db", slow, Session: "slow"), cancellation.Token);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        try
        {
            var ended = await run;
            Assert.Fail($"The slow statement ended by itself {watch.Elapsed.TotalMilliseconds:0} ms after it was sent ({ended.ElapsedMilliseconds:0} ms in the host) instead of being cancelled; its messages: {string.Join(" | ", ended.Messages.Select(m => m.Kind + ": " + m.Text))}");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Cancel took {watch.Elapsed}.");
        Assert.Equal("1", (await _data.ExecuteAsync(new ExecuteParams("db", "SELECT 1"), Timeout())).ResultSets[0].Rows[0][0]);

        // Export.
        var csv = Path.Combine(_root, engine + ".csv");
        Assert.Equal(4, (await _data.ExportAsync(new ExportParams("db", null, "csv", csv, table), Timeout())).Rows);
        Assert.StartsWith("id,name,category_id,price", File.ReadAllLines(csv)[0], StringComparison.Ordinal);
    }

    private static IEnumerable<PlanNode> Flatten(IEnumerable<PlanNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
