using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>samples/Data in the real build and language hosts.</summary>
public sealed class DataFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("Data", "Data.slnx", 1);
}

/// <summary><c>daedalus/ef/contexts</c> (M27a, ADR 0036): DbContext types with their provider and connection name, from Roslyn.</summary>
public sealed class EntityFrameworkContextsTests(DataFixture data) : IClassFixture<DataFixture>
{
    [Fact]
    public async Task Contexts_are_found_with_their_provider_connection_and_entities()
    {
        var result = await data.Client.EfContextsAsync(null, TestContext.Current.CancellationToken);
        var context = Assert.Single(Assert.IsType<EfDiscoveredContextsResult>(result).Contexts, c => c.Name == "BloggingContext");
        Assert.Equal("BloggingContext", context.Name);
        Assert.Equal("Blogging.Data.BloggingContext", context.FullName);
        Assert.Equal(data.File("Blogging", "Blogging.csproj"), context.Project, ignoreCase: OperatingSystem.IsWindows());

        // From the AddDbContext<BloggingContext>(o => o.UseSqlite(GetConnectionString("Blogging"))) in Program.cs.
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Provider);
        Assert.Equal("Blogging", context.ConnectionName);
        Assert.Equal(["Blog", "Post", "Tag"], context.Entities);
        Assert.False(context.HasDesignTimeFactory);

        // The location is the class declaration.
        Assert.EndsWith("BloggingContext.cs", DocumentUris.ToPath(context.Location.Uri), StringComparison.Ordinal);
        var line = Array.FindIndex(File.ReadAllLines(data.File("Blogging", "Data", "BloggingContext.cs")), l => l.Contains("class BloggingContext", StringComparison.Ordinal));
        Assert.Equal(line, context.Location.Range.Start.Line);

        // Filtered to one project.
        var none = await data.Client.EfContextsAsync(data.File("Other", "Other.csproj"), TestContext.Current.CancellationToken);
        Assert.Empty(none?.Contexts ?? []);
    }

    [Fact]
    public async Task OnConfiguring_factories_and_aspire_registrations_are_understood()
    {
        var file = data.File("Blogging", "Data", "MoreContexts.cs");
        await File.WriteAllTextAsync(file, """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Design;

            namespace Blogging.Data;

            public sealed class AuditContext : DbContext
            {
                public DbSet<Blog> Entries => Set<Blog>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) =>
                    options.UseSqlite("Data Source=audit.db");
            }

            public sealed class AuditContextFactory : IDesignTimeDbContextFactory<AuditContext>
            {
                public AuditContext CreateDbContext(string[] args) => new();
            }

            public abstract class BaseContext : DbContext;
            """, TestContext.Current.CancellationToken);
        try
        {
            await data.OpenAsync(file);
            EfDiscoveredContext[] contexts = [];
            await LanguageFixture.WaitUntilAsync(() =>
            {
                contexts = data.Client.EfContextsAsync(null, CancellationToken.None).GetAwaiter().GetResult()?.Contexts ?? [];
                return contexts.Length == 2;
            });
            var audit = contexts.Single(c => c.Name == "AuditContext");
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", audit.Provider);
            Assert.Null(audit.ConnectionName); // a literal connection string isn't reported
            Assert.True(audit.HasDesignTimeFactory);
            Assert.DoesNotContain(contexts, c => c.Name == "BaseContext");
        }
        finally
        {
            await data.Languages.CloseAsync(file);
            File.Delete(file);
        }
    }
}
