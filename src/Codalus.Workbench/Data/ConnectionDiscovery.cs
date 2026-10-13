using System.Text.Json;
using Codalus.Workbench.Web;

namespace Codalus.Workbench.Data;

/// <summary>The database engines the data tooling knows first (D15).</summary>
public static class DatabaseEngines
{
    public const string SqlServer = "sqlserver";
    public const string PostgreSql = "postgresql";
    public const string Sqlite = "sqlite";
    public const string MySql = "mysql";

    public static IReadOnlyList<string> All { get; } = [SqlServer, PostgreSql, MySql, Sqlite];

    public static string DisplayName(string? engine) => engine switch
    {
        SqlServer => "SQL Server",
        PostgreSql => "PostgreSQL",
        Sqlite => "SQLite",
        MySql => "MySQL/MariaDB",
        _ => "Unknown",
    };

    /// <summary>The engine of an EF Core provider package (<c>Microsoft.EntityFrameworkCore.Sqlite</c> → sqlite).</summary>
    public static string? FromEfProvider(string? provider) => provider switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => SqlServer,
        "Microsoft.EntityFrameworkCore.Sqlite" => Sqlite,
        "Npgsql.EntityFrameworkCore.PostgreSQL" => PostgreSql,
        "Pomelo.EntityFrameworkCore.MySql" or "MySql.EntityFrameworkCore" => MySql,
        _ => null,
    };

    /// <summary>The EF Core provider package that scaffolds an engine's databases.</summary>
    public static string? EfProvider(string? engine) => engine switch
    {
        SqlServer => "Microsoft.EntityFrameworkCore.SqlServer",
        Sqlite => "Microsoft.EntityFrameworkCore.Sqlite",
        PostgreSql => "Npgsql.EntityFrameworkCore.PostgreSQL",
        MySql => "Pomelo.EntityFrameworkCore.MySql",
        _ => null,
    };

    /// <summary>
    /// The engine a connection string most likely belongs to, from its keywords: a file data source is SQLite, <c>Host=</c>
    /// PostgreSQL, a MySQL port or MySQL-only keywords MySQL, and other <c>Server=</c>/<c>Data Source=</c> strings SQL Server.
    /// </summary>
    public static string? Guess(string connectionString)
    {
        var keys = Parse(connectionString);
        string? Value(params string[] names) => names.Select(n => keys.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);
        var source = Value("data source", "datasource", "filename");
        if (source is not null && (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || source.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || keys.ContainsKey("filename")))
        {
            return Sqlite;
        }

        if (keys.ContainsKey("host"))
        {
            return PostgreSql;
        }

        if (Value("port") == "3306" || keys.ContainsKey("uid") || keys.ContainsKey("sslmode") || keys.ContainsKey("allowuservariables") || keys.ContainsKey("allowpublickeyretrieval"))
        {
            return MySql;
        }

        if (keys.ContainsKey("server") || keys.ContainsKey("initial catalog") || source is not null)
        {
            return SqlServer;
        }

        return null;
    }

    /// <summary>A connection string's keywords, lower-cased, with their values (quotes removed).</summary>
    public static Dictionary<string, string> Parse(string connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            result[part[..equals].Trim().ToLowerInvariant()] = part[(equals + 1)..].Trim().Trim('"', '\'');
        }

        return result;
    }
}

/// <summary>
/// A connection string a project already has: a <c>ConnectionStrings</c> entry of its <c>appsettings*.json</c> or of
/// its user secrets. The value stays where the user put it; nothing is copied into settings.
/// </summary>
public sealed record DiscoveredConnection(string Name, string ConnectionString, string Source, string ProjectPath, string? Engine)
{
    /// <summary>"Blogging (appsettings.json)".</summary>
    public string Label => $"{Name} ({Source})";

    /// <summary>The connection string with any password replaced, for display.</summary>
    public string Masked => ConnectionDiscovery.Mask(ConnectionString);
}

/// <summary>Finds a project's connection strings (M27): appsettings files and user secrets.</summary>
public static class ConnectionDiscovery
{
    private static readonly JsonDocumentOptions s_options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<DiscoveredConnection> ForProject(string projectPath, Func<string, string?>? engineOfName = null)
    {
        var folder = Path.GetDirectoryName(projectPath)!;
        var result = new List<DiscoveredConnection>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "appsettings*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                result.AddRange(Read(file, Path.GetFileName(file), projectPath, engineOfName));
            }
        }

        if (WebDevelopmentService.UserSecretsId(projectPath) is { } id && File.Exists(WebDevelopmentService.SecretsPath(id)))
        {
            result.AddRange(Read(WebDevelopmentService.SecretsPath(id), "user secrets", projectPath, engineOfName));
        }

        return result;
    }

    /// <summary>The <c>ConnectionStrings</c> section of a JSON configuration file: nested, or flattened (<c>"ConnectionStrings:Name"</c>).</summary>
    public static IReadOnlyList<DiscoveredConnection> Read(string file, string source, string projectPath, Func<string, string?>? engineOfName = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), s_options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var entries = new List<(string Name, string Value)>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("ConnectionStrings") || property.Name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        entries.AddRange(property.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => (p.Name, p.Value.GetString()!)));
                    }
                }
                else if (property.Name.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    entries.Add((property.Name["ConnectionStrings:".Length..], property.Value.GetString()!));
                }
            }

            return [.. entries.Where(e => e.Value.Length > 0).Select(e => new DiscoveredConnection(e.Name, e.Value, source, projectPath, engineOfName?.Invoke(e.Name) ?? DatabaseEngines.Guess(e.Value)))];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Replaces password values (<c>Password</c>, <c>Pwd</c>) with <c>•••</c>.</summary>
    public static string Mask(string connectionString)
    {
        var parts = connectionString.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && parts[i][..equals].Trim().ToLowerInvariant() is "password" or "pwd")
            {
                parts[i] = parts[i][..(equals + 1)] + "•••";
            }
        }

        return string.Join(';', parts);
    }
}
