using Codalus.Projects.Protocol;
using Codalus.Workbench.Data;

namespace Codalus.Workbench.Tests;

/// <summary>Connection strings a project already has (M27), the engine guessed from them, and the EF update range.</summary>
public sealed class ConnectionDiscoveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-connections-" + Guid.NewGuid().ToString("N"));

    public ConnectionDiscoveryTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("Data Source=app.db", DatabaseEngines.Sqlite)]
    [InlineData("Filename=:memory:", DatabaseEngines.Sqlite)]
    [InlineData("Host=localhost;Database=shop;Username=postgres;Password=secret", DatabaseEngines.PostgreSql)]
    [InlineData("Server=localhost;Port=3306;Database=shop;Uid=root;Pwd=secret", DatabaseEngines.MySql)]
    [InlineData("Server=(localdb)\\mssqllocaldb;Database=Shop;Trusted_Connection=True", DatabaseEngines.SqlServer)]
    [InlineData("Data Source=.;Initial Catalog=Shop;Integrated Security=true", DatabaseEngines.SqlServer)]
    [InlineData("nonsense", null)]
    public void The_engine_is_guessed_from_the_keywords(string connection, string? engine) =>
        Assert.Equal(engine, DatabaseEngines.Guess(connection));

    [Fact]
    public void Appsettings_nested_and_flattened_entries_are_read_and_passwords_masked()
    {
        var project = Path.Combine(_folder, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(_folder, "appsettings.json"), """
            {
              // comments are allowed
              "ConnectionStrings": { "Main": "Host=db;Database=app;Username=u;Password=p4ss", "Empty": "" },
              "Logging": {},
            }
            """);
        File.WriteAllText(Path.Combine(_folder, "appsettings.Development.json"), """{ "ConnectionStrings:Local": "Data Source=local.db" }""");

        var found = ConnectionDiscovery.ForProject(project);
        Assert.Equal(["Main (appsettings.json)", "Local (appsettings.Development.json)"], found.Select(c => c.Label).OrderByDescending(l => l.StartsWith("Main", StringComparison.Ordinal)));
        var main = found.Single(c => c.Name == "Main");
        Assert.Equal(DatabaseEngines.PostgreSql, main.Engine);
        Assert.Equal("Host=db;Database=app;Username=u;Password=•••", main.Masked);
        Assert.Equal(DatabaseEngines.Sqlite, found.Single(c => c.Name == "Local").Engine);

        // An engine known for the name (the DbContext's provider) wins over the guess.
        Assert.Equal(DatabaseEngines.MySql, ConnectionDiscovery.ForProject(project, name => name == "Main" ? DatabaseEngines.MySql : null).Single(c => c.Name == "Main").Engine);
    }

    [Fact]
    public void Broken_files_are_ignored() =>
        Assert.Empty(ConnectionDiscovery.Read(WriteFile("broken.json", "{ not json"), "broken.json", "App.csproj"));

    [Fact]
    public void Providers_map_to_engines_and_back()
    {
        foreach (var engine in DatabaseEngines.All)
        {
            Assert.Equal(engine, DatabaseEngines.FromEfProvider(DatabaseEngines.EfProvider(engine)));
        }

        Assert.Equal(DatabaseEngines.MySql, DatabaseEngines.FromEfProvider("MySql.EntityFrameworkCore"));
        Assert.Null(DatabaseEngines.FromEfProvider("Microsoft.EntityFrameworkCore.Cosmos"));
    }

    [Fact]
    public void The_update_range_goes_from_the_last_applied_migration_to_the_target()
    {
        EfMigrationItem[] none = [new("1_A", "A", false), new("2_B", "B", false)];
        Assert.Equal(("0", "2_B"), EntityFrameworkService.UpdateRange(none, null));
        EfMigrationItem[] one = [new("1_A", "A", true), new("2_B", "B", false)];
        Assert.Equal(("1_A", "2_B"), EntityFrameworkService.UpdateRange(one, null));
        Assert.Equal(("1_A", "1_A"), EntityFrameworkService.UpdateRange(one, "A"));
        EfMigrationItem[] both = [new("1_A", "A", true), new("2_B", "B", true)];
        Assert.Equal(("2_B", "1_A"), EntityFrameworkService.UpdateRange(both, "1_A"));
        Assert.Equal(("2_B", "0"), EntityFrameworkService.UpdateRange(both, "0"));
        Assert.Equal(("0", "0"), EntityFrameworkService.UpdateRange([], null));
    }

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
