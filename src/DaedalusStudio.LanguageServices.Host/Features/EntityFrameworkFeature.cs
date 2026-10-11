using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// <c>daedalus/ef/contexts</c> (M27a, ADR 0036): every non-abstract class that derives from EF Core's <c>DbContext</c>,
/// with the provider and connection name its configuration uses. The configuration is looked for in the context's
/// <c>OnConfiguring</c> and in <c>AddDbContext&lt;T&gt;</c>-style registrations anywhere in the solution (the startup
/// project usually registers a library's context), including Aspire's <c>Add…DbContext&lt;T&gt;("name")</c>.
/// Nothing is built or run: this is the instant half of discovery; <c>dotnet ef dbcontext list</c> is the other.
/// </summary>
internal static class EntityFrameworkFeature
{
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";
    private const string FactoryInterface = "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1";

    /// <summary><c>Use…</c> option methods and the provider package each one comes from.</summary>
    private static readonly Dictionary<string, string> s_useMethods = new(StringComparer.Ordinal)
    {
        ["UseSqlServer"] = "Microsoft.EntityFrameworkCore.SqlServer",
        ["UseAzureSql"] = "Microsoft.EntityFrameworkCore.SqlServer",
        ["UseAzureSynapse"] = "Microsoft.EntityFrameworkCore.SqlServer",
        ["UseSqlite"] = "Microsoft.EntityFrameworkCore.Sqlite",
        ["UseNpgsql"] = "Npgsql.EntityFrameworkCore.PostgreSQL",
        ["UseMySql"] = "Pomelo.EntityFrameworkCore.MySql",
        ["UseMySQL"] = "MySql.EntityFrameworkCore",
        ["UseOracle"] = "Oracle.EntityFrameworkCore",
        ["UseCosmos"] = "Microsoft.EntityFrameworkCore.Cosmos",
        ["UseInMemoryDatabase"] = "Microsoft.EntityFrameworkCore.InMemory",
    };

    /// <summary>Registrations whose type argument is the context and whose lambda configures it.</summary>
    private static readonly HashSet<string> s_registrations = new(StringComparer.Ordinal)
    {
        "AddDbContext", "AddDbContextPool", "AddDbContextFactory", "AddPooledDbContextFactory",
    };

    /// <summary>Aspire client integrations: <c>builder.AddNpgsqlDbContext&lt;T&gt;("connectionName")</c>.</summary>
    private static readonly Dictionary<string, string> s_aspireRegistrations = new(StringComparer.Ordinal)
    {
        ["AddSqlServerDbContext"] = "Microsoft.EntityFrameworkCore.SqlServer",
        ["AddNpgsqlDbContext"] = "Npgsql.EntityFrameworkCore.PostgreSQL",
        ["AddMySqlDbContext"] = "Pomelo.EntityFrameworkCore.MySql",
        ["AddOracleDatabaseDbContext"] = "Oracle.EntityFrameworkCore",
        ["AddCosmosDbContext"] = "Microsoft.EntityFrameworkCore.Cosmos",
    };

    public static async Task<EfDiscoveredContextsResult> FindAsync(Solution solution, string? projectPath, CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var contexts = new Dictionary<string, (Project Project, INamedTypeSymbol Type)>(StringComparer.Ordinal);
        var configurations = new Dictionary<string, (string? Provider, string? Connection)>(StringComparer.Ordinal);
        var factories = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(comparer);

        // A multi-targeted project appears once per framework: its first one is enough.
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null))
        {
            if (!seen.Add(project.FilePath!)
                || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation
                || compilation.GetTypeByMetadataName(DbContextType) is not { } dbContext)
            {
                continue;
            }

            var factory = compilation.GetTypeByMetadataName(FactoryInterface);
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case ClassDeclarationSyntax declaration when model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol { IsAbstract: false } type:
                            if (DerivesFrom(type, dbContext) && (projectPath is null || comparer.Equals(project.FilePath, projectPath)))
                            {
                                contexts.TryAdd(type.ToDisplayString(), (project, type));
                                if (OnConfiguring(type, model.Compilation, cancellationToken) is { } own)
                                {
                                    configurations.TryAdd(type.ToDisplayString(), own);
                                }
                            }

                            if (factory is not null && type.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, factory)) is { } implemented
                                && implemented.TypeArguments[0] is INamedTypeSymbol created)
                            {
                                factories.Add(created.ToDisplayString());
                            }

                            break;
                        case InvocationExpressionSyntax invocation when Registration(invocation, model, dbContext, cancellationToken) is { } registration:
                            configurations[registration.Context] = (registration.Provider ?? configurations.GetValueOrDefault(registration.Context).Provider,
                                registration.Connection ?? configurations.GetValueOrDefault(registration.Context).Connection);
                            break;
                    }
                }
            }
        }

        var result = new List<EfDiscoveredContext>();
        foreach (var (fullName, (project, type)) in contexts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            var document = location?.SourceTree is { } tree ? solution.GetDocument(tree) : null;
            var at = document is not null && location is not null
                ? new Location(WorkspaceManager.UriOf(document), Positions.ToRange(location.GetLineSpan().Span))
                : new Location(DocumentUris.FromPath(project.FilePath!), new Protocol.Range(new Position(0, 0), new Position(0, 0)));
            var (provider, connection) = configurations.GetValueOrDefault(fullName);
            result.Add(new EfDiscoveredContext(project.FilePath!, type.Name, fullName, at, provider, connection, Entities(type), factories.Contains(fullName)));
        }

        return new EfDiscoveredContextsResult([.. result]);
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entity types of the context's <c>DbSet&lt;T&gt;</c> properties (inherited ones too).</summary>
    private static string[] Entities(INamedTypeSymbol type)
    {
        var entities = new List<string>();
        for (var current = type; current is not null && current.ToDisplayString() != DbContextType; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.Type is INamedTypeSymbol { Name: "DbSet", TypeArguments: [var entity] } && !entities.Contains(entity.Name))
                {
                    entities.Add(entity.Name);
                }
            }
        }

        return [.. entities];
    }

    /// <summary>The provider and connection name of <c>Use…</c> calls in the context's own <c>OnConfiguring</c>.</summary>
    private static (string? Provider, string? Connection)? OnConfiguring(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        foreach (var method in type.GetMembers("OnConfiguring").OfType<IMethodSymbol>())
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var body = reference.GetSyntax(cancellationToken);
                if (Configuration(body, compilation.GetSemanticModel(body.SyntaxTree)) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>The first <c>Use…</c> provider call under <paramref name="node"/>, and the name given to a <c>GetConnectionString</c> in it.</summary>
    private static (string? Provider, string? Connection)? Configuration(SyntaxNode node, SemanticModel model)
    {
        foreach (var invocation in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (Name(invocation) is { } name && s_useMethods.TryGetValue(name, out var provider))
            {
                return (provider, ConnectionName(invocation, model));
            }
        }

        return null;
    }

    private static string? ConnectionName(InvocationExpressionSyntax use, SemanticModel model)
    {
        foreach (var call in use.ArgumentList.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (Name(call) == "GetConnectionString" && call.ArgumentList.Arguments is [var argument, ..]
                && model.GetConstantValue(argument.Expression) is { HasValue: true, Value: string connection })
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>services.AddDbContext&lt;T&gt;(o => o.UseX(…))</c> and friends, and Aspire's <c>builder.AddXDbContext&lt;T&gt;("name")</c>:
    /// the context's full name, provider and connection name.
    /// </summary>
    private static (string Context, string? Provider, string? Connection)? Registration(InvocationExpressionSyntax invocation, SemanticModel model, INamedTypeSymbol dbContext, CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArgument, ..] } generic })
        {
            return null;
        }

        var method = generic.Identifier.ValueText;
        var aspire = s_aspireRegistrations.TryGetValue(method, out var aspireProvider);
        if (!aspire && !s_registrations.Contains(method))
        {
            return null;
        }

        if (model.GetTypeInfo(typeArgument, cancellationToken).Type is not INamedTypeSymbol context || !DerivesFrom(context, dbContext))
        {
            return null;
        }

        if (aspire)
        {
            var name = invocation.ArgumentList.Arguments is [var first, ..] && model.GetConstantValue(first.Expression, cancellationToken) is { HasValue: true, Value: string value } ? value : null;
            return (context.ToDisplayString(), aspireProvider, name);
        }

        var configuration = Configuration(invocation.ArgumentList, model);
        return (context.ToDisplayString(), configuration?.Provider, configuration?.Connection);
    }

    private static string? Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };
}
