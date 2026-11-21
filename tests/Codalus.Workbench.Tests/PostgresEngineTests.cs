using Codalus.Data.Host.Engines;
using Npgsql;

namespace Codalus.Workbench.Tests;

/// <summary>Opening a PostgreSQL connection string that a project on an older Npgsql has (Npgsql 11 removed multiplexing).</summary>
public sealed class PostgresEngineTests
{
    [Theory]
    [InlineData("Host=db;Database=shop;Username=app;Multiplexing=true")]
    [InlineData("Host=db;Database=shop;Username=app;multiplexing=false")]
    [InlineData("Host=db;Database=shop;Username=app;Write Coalescing Buffer Threshold Bytes=1000")]
    [InlineData("Host=db;Database=shop;Username=app;WriteCoalescingBufferThresholdBytes=1000;Multiplexing=true")]
    public void A_connection_string_with_a_keyword_Npgsql_11_removed_still_opens(string connectionString)
    {
        Assert.Throws<ArgumentException>(() => new NpgsqlConnectionStringBuilder(connectionString));

        using var connection = new PostgresEngine().Create(connectionString, "orders");

        var opened = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("db", opened.Host);
        Assert.Equal("orders", opened.Database);
        Assert.Equal("app", opened.Username);
    }

    [Fact]
    public void A_connection_string_without_them_is_kept_as_written()
    {
        const string connectionString = "Host=db;Database=shop;Username=app;Password=\"a;b\"";

        Assert.Equal(connectionString, PostgresEngine.WithoutRemovedKeywords(connectionString));
    }
}
