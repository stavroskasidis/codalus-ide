using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.EntityFramework.Agent;

/// <summary>
/// <c>daedalus-efagent model --startup-assembly &lt;dll&gt; --context &lt;type&gt; --output &lt;json&gt;</c>: creates the
/// DbContext the way the EF Core tools do (<c>Microsoft.EntityFrameworkCore.Design.DbContextActivator</c>: the app's host
/// builder or a design-time factory), then writes its design-time model (entities, keys, relationships, owned types) and
/// its debug view as JSON. It is started by the build host with the startup project's <c>deps.json</c> and
/// <c>runtimeconfig.json</c> (<c>dotnet exec</c>), so it runs on the app's runtime with the app's EF Core; everything
/// EF-specific is reached by reflection, by member names that are stable from EF Core 3.1 to 10.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Parse(args);
        if (args.Length == 0 || args[0] != "model" || !options.TryGetValue("--startup-assembly", out var startup)
            || !options.TryGetValue("--context", out var contextName) || !options.TryGetValue("--output", out var output))
        {
            Console.Error.WriteLine("Usage: daedalus-efagent model --startup-assembly <path> --context <type> --output <file> [--context-assembly <name>]");
            return 2;
        }

        try
        {
            var json = ModelReader.Read(Path.GetFullPath(startup), contextName, options.GetValueOrDefault("--context-assembly"));
            File.WriteAllText(output, json, new UTF8Encoding(false));
            return 0;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            Console.Error.WriteLine("error: " + inner.Message);
            Console.Error.WriteLine(inner.ToString());
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            options[args[i]] = args[i + 1];
        }

        return options;
    }
}

/// <summary>Creates the context and writes its model through reflection.</summary>
internal static class ModelReader
{
    public static string Read(string startupAssemblyPath, string contextName, string? contextAssembly)
    {
        var bin = Path.GetDirectoryName(startupAssemblyPath)!;

        // Assemblies of the app that the deps file doesn't place (or that resolve from its folder) load from the bin folder.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(bin, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        var startup = AssemblyLoadContext.Default.LoadFromAssemblyPath(startupAssemblyPath);
        var contextType = FindType(startup, contextName, contextAssembly)
            ?? throw new InvalidOperationException($"The DbContext type '{contextName}' was not found in {Path.GetFileName(startupAssemblyPath)} or its references.");

        var design = LoadAssembly("Microsoft.EntityFrameworkCore.Design")
            ?? throw new InvalidOperationException("Microsoft.EntityFrameworkCore.Design isn't referenced by the startup project; the EF Core tools need it.");
        var activator = design.GetType("Microsoft.EntityFrameworkCore.Design.DbContextActivator", throwOnError: true)!;
        var create = activator.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateInstance")
            .OrderByDescending(m => m.GetParameters().Length)
            .First();
        var arguments = create.GetParameters().Select(p => p.ParameterType == typeof(Type) ? contextType
            : p.ParameterType == typeof(Assembly) ? startup
            : p.ParameterType == typeof(string[]) ? (object)Array.Empty<string>()
            : null).ToArray();

        var context = create.Invoke(null, arguments) ?? throw new InvalidOperationException("The DbContext could not be created.");
        try
        {
            var model = DesignTimeModel(context) ?? Get(context, "Model") ?? throw new InvalidOperationException("The context has no model.");
            return Write(contextType, context, model);
        }
        finally
        {
            (context as IDisposable)?.Dispose();
        }
    }

    private static Type? FindType(Assembly startup, string name, string? assemblyName)
    {
        if (startup.GetType(name) is { } local)
        {
            return local;
        }

        if (assemblyName is { Length: > 0 } && LoadAssembly(assemblyName)?.GetType(name) is { } named)
        {
            return named;
        }

        foreach (var reference in startup.GetReferencedAssemblies())
        {
            try
            {
                if (AssemblyLoadContext.Default.LoadFromAssemblyName(reference).GetType(name) is { } found)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
            }
        }

        return null;
    }

    private static Assembly? LoadAssembly(string name)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>EF Core 6+: <c>IDesignTimeModel.Model</c> keeps the design-time annotations (table names) that the runtime model drops.</summary>
    private static object? DesignTimeModel(object context)
    {
        var infrastructure = context.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.Name.StartsWith("IInfrastructure", StringComparison.Ordinal) && i.GetGenericArguments()[0] == typeof(IServiceProvider));
        if (infrastructure?.GetProperty("Instance")?.GetValue(context) is not IServiceProvider services)
        {
            return null;
        }

        var designTimeModel = context.GetType().Assembly.GetType("Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel")
            ?? LoadAssembly("Microsoft.EntityFrameworkCore")?.GetType("Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel");
        if (designTimeModel is null)
        {
            return null;
        }

        try
        {
            return services.GetService(designTimeModel) is { } service ? Get(service, "Model") : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ writing

    private static string Write(Type contextType, object context, object model)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("context", contextType.FullName);
            w.WriteString("provider", ProviderName(context));
            w.WriteString("productVersion", Annotation(model, "ProductVersion")?.ToString());
            w.WriteStartArray("entities");
            foreach (var entity in Enumerate(Call(model, "GetEntityTypes")).OrderBy(e => Get(e, "Name") as string, StringComparer.Ordinal))
            {
                WriteEntity(w, entity);
            }

            w.WriteEndArray();
            w.WriteString("debugView", DebugView(model));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteEntity(Utf8JsonWriter w, object entity)
    {
        var foreignKeys = Enumerate(Call(entity, "GetForeignKeys")).ToList();
        var ownership = foreignKeys.FirstOrDefault(fk => Get(fk, "IsOwnership") is true);
        var keyProperties = new HashSet<string>(StringComparer.Ordinal);
        var primaryKey = Call(entity, "FindPrimaryKey");
        foreach (var property in Enumerate(primaryKey is null ? null : Get(primaryKey, "Properties")))
        {
            keyProperties.Add((string)Get(property, "Name")!);
        }

        var foreignKeyProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fk in foreignKeys)
        {
            foreach (var property in Enumerate(Get(fk, "Properties")))
            {
                foreignKeyProperties.Add((string)Get(property, "Name")!);
            }
        }

        w.WriteStartObject();
        w.WriteString("name", Get(entity, "Name") as string);
        w.WriteString("clrType", ShortType(Get(entity, "ClrType") as Type));
        w.WriteString("table", Relational(entity, "RelationalEntityTypeExtensions", "GetTableName") ?? Annotation(entity, "Relational:TableName")?.ToString());
        w.WriteString("schema", Relational(entity, "RelationalEntityTypeExtensions", "GetSchema") ?? Annotation(entity, "Relational:Schema")?.ToString());
        w.WriteString("view", Relational(entity, "RelationalEntityTypeExtensions", "GetViewName") ?? Annotation(entity, "Relational:ViewName")?.ToString());
        w.WriteBoolean("isOwned", ownership is not null);
        w.WriteString("owner", ownership is null ? null : Get(Get(ownership, "PrincipalEntityType"), "Name") as string);
        w.WriteString("baseType", Get(Get(entity, "BaseType"), "Name") as string);
        w.WriteBoolean("isKeyless", primaryKey is null);

        w.WriteStartArray("properties");
        foreach (var property in Enumerate(Call(entity, "GetDeclaredProperties") ?? Call(entity, "GetProperties")))
        {
            var name = (string)Get(property, "Name")!;
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("clrType", ShortType(Get(property, "ClrType") as Type));
            w.WriteBoolean("nullable", Get(property, "IsNullable") is true);
            w.WriteBoolean("isKey", keyProperties.Contains(name));
            w.WriteBoolean("isForeignKey", foreignKeyProperties.Contains(name));
            w.WriteBoolean("isShadow", Call(property, "IsShadowProperty") is true || Get(property, "PropertyInfo") is null && Get(property, "FieldInfo") is null);
            w.WriteString("column", Relational(property, "RelationalPropertyExtensions", "GetColumnName") ?? Annotation(property, "Relational:ColumnName")?.ToString());
            w.WriteString("columnType", Relational(property, "RelationalPropertyExtensions", "GetColumnType") ?? Annotation(property, "Relational:ColumnType")?.ToString());
            if (Call(property, "GetMaxLength") is int maxLength)
            {
                w.WriteNumber("maxLength", maxLength);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("keys");
        foreach (var key in Enumerate(Call(entity, "GetDeclaredKeys") ?? Call(entity, "GetKeys")))
        {
            w.WriteStartObject();
            WriteNames(w, "properties", Get(key, "Properties"));
            w.WriteBoolean("isPrimary", Call(key, "IsPrimaryKey") is true || ReferenceEquals(key, primaryKey));
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("foreignKeys");
        foreach (var fk in foreignKeys.Where(fk => ReferenceEquals(Get(fk, "DeclaringEntityType"), entity) || Get(fk, "DeclaringEntityType") is null))
        {
            w.WriteStartObject();
            WriteNames(w, "properties", Get(fk, "Properties"));
            w.WriteString("principal", Get(Get(fk, "PrincipalEntityType"), "Name") as string);
            WriteNames(w, "principalProperties", Get(Get(fk, "PrincipalKey"), "Properties"));
            w.WriteString("deleteBehavior", Get(fk, "DeleteBehavior")?.ToString());
            w.WriteBoolean("isUnique", Get(fk, "IsUnique") is true);
            w.WriteBoolean("isRequired", Get(fk, "IsRequired") is true);
            w.WriteBoolean("isOwnership", Get(fk, "IsOwnership") is true);
            w.WriteString("dependentToPrincipal", Get(Get(fk, "DependentToPrincipal"), "Name") as string);
            w.WriteString("principalToDependent", Get(Get(fk, "PrincipalToDependent"), "Name") as string);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("navigations");
        foreach (var navigation in Enumerate(Call(entity, "GetDeclaredNavigations") ?? Call(entity, "GetNavigations")))
        {
            WriteNavigation(w, navigation, isSkip: false);
        }

        foreach (var navigation in Enumerate(Call(entity, "GetDeclaredSkipNavigations") ?? Call(entity, "GetSkipNavigations")))
        {
            WriteNavigation(w, navigation, isSkip: true);
        }

        w.WriteEndArray();

        w.WriteStartArray("indexes");
        foreach (var index in Enumerate(Call(entity, "GetDeclaredIndexes") ?? Call(entity, "GetIndexes")))
        {
            w.WriteStartObject();
            WriteNames(w, "properties", Get(index, "Properties"));
            w.WriteBoolean("isUnique", Get(index, "IsUnique") is true);
            w.WriteString("name", Get(index, "Name") as string ?? Relational(index, "RelationalIndexExtensions", "GetDatabaseName"));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteNavigation(Utf8JsonWriter w, object navigation, bool isSkip)
    {
        w.WriteStartObject();
        w.WriteString("name", Get(navigation, "Name") as string);
        w.WriteString("target", Get(Get(navigation, "TargetEntityType"), "Name") as string);
        w.WriteBoolean("isCollection", (Get(navigation, "IsCollection") ?? Call(navigation, "IsCollection")) is true);
        w.WriteBoolean("isOnDependent", (Get(navigation, "IsOnDependent") ?? Call(navigation, "IsDependentToPrincipal")) is true);
        w.WriteString("inverse", Get(Get(navigation, "Inverse") ?? Call(navigation, "FindInverse"), "Name") as string);
        w.WriteBoolean("isSkip", isSkip);
        w.WriteEndObject();
    }

    private static void WriteNames(Utf8JsonWriter w, string name, object? properties)
    {
        w.WriteStartArray(name);
        foreach (var property in Enumerate(properties))
        {
            w.WriteStringValue(Get(property, "Name") as string);
        }

        w.WriteEndArray();
    }

    private static string? ProviderName(object context)
    {
        // context.Database.ProviderName (EF Core 2.0+).
        return Get(Get(context, "Database"), "ProviderName") as string;
    }

    private static string? DebugView(object model)
    {
        try
        {
            if (Get(model, "DebugView") is { } view && Get(view, "LongView") is string longView)
            {
                return longView;
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException)
        {
        }

        // EF Core 3.1: ModelExtensions.ToDebugString(IModel, ...).
        var extensions = model.GetType().Assembly.GetType("Microsoft.EntityFrameworkCore.ModelExtensions");
        var method = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "ToDebugString" && m.GetParameters()[0].ParameterType.IsInstanceOfType(model));
        if (method is null)
        {
            return null;
        }

        var arguments = method.GetParameters().Select((p, i) => i == 0 ? model : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        return method.Invoke(null, arguments) as string;
    }

    // ------------------------------------------------------------------ reflection helpers

    /// <summary>
    /// A relational extension method (<c>GetTableName</c>, <c>GetColumnName</c>…) of EF Core Relational, called with
    /// the one-parameter overload that takes this metadata object.
    /// </summary>
    private static string? Relational(object target, string typeName, string methodName)
    {
        var relational = LoadAssembly("Microsoft.EntityFrameworkCore.Relational")?.GetType("Microsoft.EntityFrameworkCore." + typeName);
        var method = relational?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(target))
            .FirstOrDefault();
        try
        {
            return method?.Invoke(null, [target]) as string;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static object? Annotation(object target, string name)
    {
        if (Call(target, "FindAnnotation", name) is not { } annotation)
        {
            return null;
        }

        return Get(annotation, "Value");
    }

    /// <summary>A property of the object, or of one of its interfaces (explicit implementations).</summary>
    private static object? Get(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        // Properties hidden with 'new' (covariant metadata types) appear more than once: the most derived one wins.
        var type = target.GetType();
        var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name == name)
                           .OrderByDescending(p => Depth(p.DeclaringType)).FirstOrDefault()
            ?? Interfaces(type).Select(i => i.GetProperties().FirstOrDefault(p => p.Name == name)).FirstOrDefault(p => p is not null);
        try
        {
            return property?.GetIndexParameters().Length == 0 ? property.GetValue(target) : null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>A method of the object or of one of its interfaces (explicit and default interface methods), by name and argument count.</summary>
    private static object? Call(object? target, string name, params object[] arguments)
    {
        if (target is null)
        {
            return null;
        }

        var type = target.GetType();
        bool Fits(MethodInfo m) => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == arguments.Length
            && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(arguments[i])).All(ok => ok);
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(Fits)
            ?? Interfaces(type).SelectMany(i => i.GetMethods()).FirstOrDefault(Fits);
        try
        {
            return method?.Invoke(target, arguments);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (; type is not null; type = type.BaseType)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>The read-only metadata interfaces first (EF Core 6+), then the others.</summary>
    private static IEnumerable<Type> Interfaces(Type type) =>
        type.GetInterfaces().OrderBy(i => i.Name.StartsWith("IReadOnly", StringComparison.Ordinal) ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal);

    private static IEnumerable<object> Enumerate(object? value) =>
        value is System.Collections.IEnumerable items and not string ? items.Cast<object>() : [];

    private static string? ShortType(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return ShortType(underlying) + "?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(ShortType)) + ">";
    }
}
