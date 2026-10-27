using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Data;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// EF Core through the real build host (M27a, ADR 0036): the user's dotnet-ef (the fixture's local tool, restored here)
/// and the model agent on a copy of samples/Data, on SQLite.
/// </summary>
public sealed class EntityFrameworkIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-ef-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;
    private BuildService _builds = null!;
    private EntityFrameworkService _ef = null!;

    private string Fixture => Path.Combine(_root, "Data");

    private string Blogging => Path.Combine(Fixture, "Blogging", "Blogging.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Data"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _builds = new BuildService(_solutions, _output);
        _ef = new EntityFrameworkService(_solutions, _builds, _output, () => Blogging, _ => Task.FromResult<Codalus.LanguageServices.Protocol.EfDiscoveredContextsResult?>(null));
        var solution = await _solutions.OpenAsync(Path.Combine(Fixture, "Data.slnx"), trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length);

        // The fixture's local dotnet-ef, as the Entity Framework window restores it.
        var restored = await _solutions.RequestAsync(PackageMethods.ToolCommand, new ToolCommandParams(ToolActions.Restore), PackageJsonContext.Default.ToolCommandParams, PackageJsonContext.Default.ToolCommandResult, Timeout());
        Assert.True(restored.Succeeded, restored.Output);
    }

    public async ValueTask DisposeAsync()
    {
        _ef.Dispose();
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Status_contexts_migrations_scripts_apply_revert_add_and_remove()
    {
        var status = await _ef.StatusAsync(Blogging, Timeout());
        Assert.Equal("10.0.12", status.ToolVersion);
        Assert.Equal("local", status.ToolSource);
        Assert.Equal("10.0.12", status.EfCoreVersion);
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite", status.Providers);
        Assert.True(status.HasDesignPackage);
        Assert.True(EntityFrameworkService.ReferencesEfCore(_solutions.Projects[Blogging]));

        var contexts = await _ef.ToolContextsAsync(Blogging, Timeout());
        var context = Assert.Single(contexts);
        Assert.Equal("Blogging.Data.BloggingContext", context.FullName);
        var target = new EfTarget(Blogging, Blogging, context.FullName);

        var info = await _ef.ContextInfoAsync(target, Timeout());
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", info.ProviderName);
        Assert.Equal("blogging.db", info.DataSource);

        // Nothing applied yet: the update script creates the tables, and is what "Update Database" shows first.
        var migrations = (await _ef.MigrationsAsync(target, Timeout())).Migrations;
        var initial = Assert.Single(migrations);
        Assert.Equal("InitialCreate", initial.Name);
        Assert.NotEqual(true, initial.Applied);
        Assert.Equal(("0", initial.Id), EntityFrameworkService.UpdateRange(migrations, null));
        var script = await _ef.UpdateScriptAsync(target, migrations, null, Timeout());
        Assert.Contains("CREATE TABLE \"Blogs\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"PostTag\"", script, StringComparison.Ordinal);

        await _ef.UpdateDatabaseAsync(target, null, Timeout());
        Assert.True(File.Exists(Path.Combine(Fixture, "Blogging", "blogging.db")));
        migrations = (await _ef.MigrationsAsync(target, Timeout())).Migrations;
        Assert.True(Assert.Single(migrations).Applied);
        Assert.Equal(string.Empty, await _ef.UpdateScriptAsync(target, migrations, null, Timeout()));

        // A model change becomes a migration; the editor's files are reported.
        var contextPath = Path.Combine(Fixture, "Blogging", "Data", "BloggingContext.cs");
        File.WriteAllText(contextPath, File.ReadAllText(contextPath).Replace("public DateTime Published { get; set; }", "public DateTime Published { get; set; }\n\n    public int Rating { get; set; }", StringComparison.Ordinal));
        _ef.Invalidate();
        var files = await _ef.AddMigrationAsync(target, "AddRating", null, Timeout());
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(File.Exists(f), f));
        Assert.Contains(files, f => f.EndsWith("_AddRating.cs", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<Exception>(() => _ef.AddMigrationAsync(target, "Not a name", null, Timeout()));

        migrations = (await _ef.MigrationsAsync(target, Timeout())).Migrations;
        Assert.Equal(2, migrations.Length);
        Assert.Equal([true, false], migrations.Select(m => m.Applied == true));
        script = await _ef.UpdateScriptAsync(target, migrations, null, Timeout());
        Assert.Contains("ALTER TABLE \"Posts\" ADD \"Rating\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE \"Blogs\"", script, StringComparison.Ordinal);
        await _ef.UpdateDatabaseAsync(target, null, Timeout());

        // Revert to the first migration: the Down script, then the update.
        migrations = (await _ef.MigrationsAsync(target, Timeout())).Migrations;
        Assert.All(migrations, m => Assert.True(m.Applied));
        Assert.Equal((migrations[1].Id, migrations[0].Id), EntityFrameworkService.UpdateRange(migrations, "InitialCreate"));
        var down = await _ef.UpdateScriptAsync(target, migrations, "InitialCreate", Timeout());
        Assert.Contains("DELETE FROM \"__EFMigrationsHistory\"", down, StringComparison.Ordinal);
        await _ef.UpdateDatabaseAsync(target, "InitialCreate", Timeout());
        migrations = (await _ef.MigrationsAsync(target, Timeout())).Migrations;
        Assert.Equal([true, false], migrations.Select(m => m.Applied == true));

        // The unapplied migration goes away with its files.
        var removed = await _ef.RemoveMigrationAsync(target, force: false, Timeout());
        Assert.NotEmpty(removed);
        Assert.False(File.Exists(files.Single(f => f.EndsWith("_AddRating.cs", StringComparison.Ordinal))));
        Assert.Single((await _ef.MigrationsAsync(target, Timeout())).Migrations);

        // Another connection: the database the user picked, not the app's (with the model back to the migrations' one:
        // EF Core 9+ refuses to update a database while the model has changes no migration covers).
        File.WriteAllText(contextPath, File.ReadAllText(contextPath).Replace("\n\n    public int Rating { get; set; }", string.Empty, StringComparison.Ordinal));
        _ef.Invalidate();
        var other = Path.Combine(Fixture, "Blogging", "other.db");
        await _ef.UpdateDatabaseAsync(target with { Connection = "Data Source=" + other }, null, Timeout());
        Assert.True(File.Exists(other));
        Assert.True(Assert.Single((await _ef.MigrationsAsync(target with { Connection = "Data Source=" + other }, Timeout())).Migrations).Applied);
        Assert.Contains("dotnet ef", _ef.Channel.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_view_compiled_model_bundle_and_reverse_engineering()
    {
        var target = new EfTarget(Blogging, Blogging, "Blogging.Data.BloggingContext");

        // The design-time model, read by the agent on the app's runtime.
        var model = await _ef.ModelAsync(target, Timeout());
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", model.Provider);
        var blog = model.Entities.Single(e => e.Name == "Blogging.Data.Blog");
        Assert.Equal("Blogs", blog.Table);
        Assert.Contains(blog.Properties, p => p is { Name: "Id", IsKey: true });
        Assert.Contains(blog.Navigations, n => n is { Name: "Posts", IsCollection: true, Target: "Blogging.Data.Post" });
        var owner = model.Entities.Single(e => e.Name == "Blogging.Data.Person");
        Assert.True(owner.IsOwned);
        Assert.Equal("Blogging.Data.Blog", owner.Owner);
        var post = model.Entities.Single(e => e.Name == "Blogging.Data.Post");
        var fk = Assert.Single(post.ForeignKeys);
        Assert.Equal(["BlogId"], fk.Properties);
        Assert.Equal("Blogging.Data.Blog", fk.Principal);
        Assert.Equal("Cascade", fk.DeleteBehavior);
        Assert.Contains(post.Navigations, n => n is { Name: "Tags", IsSkip: true });
        Assert.Contains(model.Entities, e => e.Name == "PostTag");
        Assert.Contains(blog.Indexes, i => i is { IsUnique: true, Properties: ["Url"] });
        Assert.Contains("EntityType: Blog", model.DebugView, StringComparison.Ordinal);

        // A compiled model.
        var compiled = await _ef.OptimizeAsync(target, "CompiledModels", "Blogging.CompiledModels", Timeout());
        Assert.Contains(compiled, f => f.EndsWith("BloggingContextModel.cs", StringComparison.Ordinal));

        // Reverse engineering from the inventory database into the same project.
        var inventory = Path.Combine(Fixture, "Inventory", "inventory.db");
        var scaffolded = await _ef.ScaffoldAsync(
            new EfScaffoldParams(Blogging, Blogging, "Data Source=" + inventory, "Microsoft.EntityFrameworkCore.Sqlite", OutputDir: "Inventory", ContextName: "InventoryContext", Tables: ["Products", "Categories"]),
            Timeout());
        Assert.Contains(scaffolded, f => f.EndsWith("InventoryContext.cs", StringComparison.Ordinal));
        Assert.Contains(scaffolded, f => f.EndsWith("Product.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(scaffolded, f => f.EndsWith("Supplier.cs", StringComparison.Ordinal));
        Assert.DoesNotContain("Data Source=", File.ReadAllText(scaffolded.Single(f => f.EndsWith("InventoryContext.cs", StringComparison.Ordinal))), StringComparison.Ordinal); // no OnConfiguring
        Assert.DoesNotContain(inventory, _ef.Channel.GetText(), StringComparison.Ordinal); // the connection string isn't echoed

        // A migrations bundle.
        var bundle = await _ef.BundleAsync(target, Path.Combine(_root, "bundle", "efbundle"), selfContained: false, runtime: null, Timeout());
        Assert.NotNull(bundle);
        Assert.True(File.Exists(bundle));
    }

    /// <summary>
    /// The IDE closing while dotnet ef runs (the Entity Framework window asks for the context's database without a
    /// cancellation): the command ends with the host. The host used to exit on the shell's shutdown and leave it running;
    /// on macOS launchd adopted it and it stayed for hours after the Shell tests.
    /// </summary>
    [Fact]
    public async Task Closing_the_solution_ends_a_running_dotnet_ef()
    {
        // Straight to the host, without the window's build first: the host's one new child is dotnet ef (which builds).
        var host = _solutions.HostProcessId ?? throw new InvalidOperationException("No build host.");
        var before = Codalus.Platform.ProcessTree.Children(host).ToHashSet();
        var json = EntityFrameworkJsonContext.Default;
        var running = _solutions.RequestAsync(EfMethods.ContextInfo, new EfTarget(Blogging, Blogging, "Blogging.Data.BloggingContext"), json.EfTarget, json.EfContextInfoResult, CancellationToken.None);
        int[] ef = [];
        await WaitUntilAsync(() => (ef = [.. Codalus.Platform.ProcessTree.Children(host).Where(c => !before.Contains(c))]).Length > 0 || running.IsCompleted);
        Assert.False(running.IsCompleted, "dotnet ef finished before the solution closed.");
        Assert.Contains("dbcontext info", _ef.Channel.GetText(), StringComparison.Ordinal);

        // Ended before the host exits. Left running, it went on for about a second on Windows (until it wrote to the
        // closed pipe) and for hours on macOS.
        await _solutions.CloseAsync();
        var closed = DateTime.UtcNow;
        while (ef.Any(IsRunning))
        {
            Assert.True(DateTime.UtcNow - closed < TimeSpan.FromMilliseconds(300), "dotnet ef outlived the build host.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        await Record.ExceptionAsync(() => running);
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
