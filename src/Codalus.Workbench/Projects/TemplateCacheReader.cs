using System.Text.Json;

namespace Codalus.Workbench.Projects;

/// <summary>A parameter of a template, as the template cache has it.</summary>
public sealed record CachedParameter(
    string Name,
    string DataType,
    string? DefaultValue,
    string? DisplayName,
    string? Description,
    IReadOnlyList<TemplateChoice> Choices,
    string? EnabledCondition);

/// <summary>How <c>dotnet new</c> takes a symbol (the template's <c>dotnetcli.host.json</c>, inline in the cache).</summary>
public sealed record CliSymbol(string? LongName, bool IsHidden);

/// <summary>A template in <c>templatecache.json</c>: the template engine's record of every installed template.</summary>
public sealed record CachedTemplate
{
    public required string Identity { get; init; }

    public string? GroupIdentity { get; init; }

    public int Precedence { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> ShortNames { get; init; } = [];

    public IReadOnlyList<string> Classifications { get; init; } = [];

    /// <summary>The <c>language</c> tag (<c>C#</c>, <c>F#</c>, <c>VB</c>), or empty.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>The <c>type</c> tag (<c>project</c>, <c>item</c>, <c>solution</c>).</summary>
    public string Type { get; init; } = string.Empty;

    public string? DefaultName { get; init; }

    public string? MountPointUri { get; init; }

    public string? ConfigPlace { get; init; }

    public string? LocaleConfigPlace { get; init; }

    public IReadOnlyList<CachedParameter> Parameters { get; init; } = [];

    /// <summary>The CLI host file's symbols by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, CliSymbol> CliSymbols { get; init; } = new Dictionary<string, CliSymbol>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The operating systems an <c>os</c> constraint allows (empty: no such constraint).</summary>
    public IReadOnlyList<string> OperatingSystems { get; init; } = [];

    public string ShortName => ShortNames.Count > 0 ? ShortNames[0] : string.Empty;
}

/// <summary>
/// Reads the template engine's cache (<c>&lt;settings&gt;/dotnetcli/&lt;sdk&gt;/templatecache.json</c>, format
/// <c>1.0.0.x</c>). It is the engine's internal file, not a contract (ADR 0082): keys are read in any case, booleans as
/// booleans or strings, and an unknown format reads as nothing, so New Project falls back to the CLI's list and help.
/// </summary>
public static class TemplateCacheReader
{
    /// <summary>The cache's templates; null when the file is missing, isn't JSON or has another format.</summary>
    public static IReadOnlyList<CachedTemplate>? Read(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<CachedTemplate>? Read(Stream stream)
    {
        using var document = TemplateJson.Parse(stream);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.Text("Version") is not { } version || !version.StartsWith("1.0.0.", StringComparison.Ordinal))
        {
            return null;
        }

        var templates = new List<CachedTemplate>();
        foreach (var entry in root.Items("TemplateInfo"))
        {
            if (Template(entry) is { } template)
            {
                templates.Add(template);
            }
        }

        return templates;
    }

    private static CachedTemplate? Template(JsonElement entry)
    {
        if (entry.Text("Identity") is not { Length: > 0 } identity || entry.Text("Name") is not { } name)
        {
            return null;
        }

        var tags = entry.Object("TagsCollection");
        return new CachedTemplate
        {
            Identity = identity,
            GroupIdentity = entry.Text("GroupIdentity") is { Length: > 0 } group ? group : null,
            Precedence = entry.Int("Precedence") ?? 0,
            Name = name,
            Description = entry.Text("Description") ?? string.Empty,
            ShortNames = entry.Strings("ShortNameList"),
            Classifications = entry.Strings("Classifications"),
            Language = tags?.Text("language") ?? string.Empty,
            Type = tags?.Text("type") ?? string.Empty,
            DefaultName = entry.Text("DefaultName") is { Length: > 0 } defaultName ? defaultName : null,
            MountPointUri = entry.Text("MountPointUri"),
            ConfigPlace = entry.Text("ConfigPlace"),
            LocaleConfigPlace = entry.Text("LocaleConfigPlace"),
            Parameters = [.. entry.Items("Parameters").Select(Parameter).OfType<CachedParameter>()],
            CliSymbols = CliSymbols(entry.Text("HostData")),
            OperatingSystems = OperatingSystems(entry),
        };
    }

    private static CachedParameter? Parameter(JsonElement parameter)
    {
        if (parameter.Text("Name") is not { Length: > 0 } name)
        {
            return null;
        }

        var choices = new List<TemplateChoice>();
        if (parameter.Object("Choices") is { } map)
        {
            foreach (var choice in map.EnumerateObject())
            {
                var label = choice.Value.Text("DisplayName");
                choices.Add(new TemplateChoice(choice.Name, string.IsNullOrEmpty(label) ? choice.Name : label, choice.Value.Text("Description") is { Length: > 0 } d ? d : null));
            }
        }

        return new CachedParameter(
            name,
            parameter.Text("DataType") ?? "string",
            parameter.Text("DefaultValue"),
            parameter.Text("DisplayName") is { Length: > 0 } display ? display : null,
            parameter.Text("Description") is { Length: > 0 } description ? description : null,
            choices,
            parameter.Object("Precedence")?.Text("IsEnabledCondition") is { Length: > 0 } condition ? condition : null);
    }

    /// <summary>The CLI host file (<c>HostData</c>, a JSON string): each symbol's long name and whether it's hidden.</summary>
    internal static IReadOnlyDictionary<string, CliSymbol> CliSymbols(string? hostData)
    {
        var symbols = new Dictionary<string, CliSymbol>(StringComparer.OrdinalIgnoreCase);
        using var document = TemplateJson.Parse(hostData);
        if (document?.RootElement.Object("symbolInfo") is { } info)
        {
            foreach (var symbol in info.EnumerateObject())
            {
                symbols[symbol.Name] = new CliSymbol(symbol.Value.Text("longName") is { Length: > 0 } longName ? longName : null, symbol.Value.Bool("isHidden") ?? false);
            }
        }

        return symbols;
    }

    /// <summary>The operating systems of an <c>os</c> constraint (its <c>Args</c> are JSON: a name or a list).</summary>
    private static IReadOnlyList<string> OperatingSystems(JsonElement entry)
    {
        foreach (var constraint in entry.Items("Constraints"))
        {
            if (!string.Equals(constraint.Text("Type"), "os", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (constraint.TryGet("Args", out var args))
            {
                if (args.ValueKind == JsonValueKind.Array)
                {
                    return [.. args.EnumerateArray().Select(TemplateJson.AsText).OfType<string>()];
                }

                var text = TemplateJson.AsText(args) ?? string.Empty;
                using var parsed = TemplateJson.Parse(text);
                return parsed?.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => [.. parsed.RootElement.EnumerateArray().Select(TemplateJson.AsText).OfType<string>()],
                    JsonValueKind.String => [parsed.RootElement.GetString()!],
                    _ => text.Length > 0 ? [text.Trim('"')] : [],
                };
            }
        }

        return [];
    }
}

/// <summary>
/// Where the template cache of the SDK New Project uses is. With a hive (<see cref="TemplateService.HiveVariable"/>) the
/// CLI keeps its settings there; otherwise in <c>$DOTNET_CLI_HOME/.templateengine</c>, else the home folder's. Either
/// way the cache is <c>dotnetcli/&lt;sdk version&gt;/templatecache.json</c> under it. Tests point it at recorded caches.
/// </summary>
public class TemplateCacheLocator
{
    private readonly Func<string, string?> _variable;
    private readonly string _home;

    public TemplateCacheLocator(Func<string, string?>? variable = null, string? home = null)
    {
        _variable = variable ?? Environment.GetEnvironmentVariable;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// The cache file for <paramref name="sdkVersion"/> (from <c>dotnet --version</c> in the neutral folder) under the
    /// hive or the settings folder; else the newest <c>templatecache.json</c> one level below <c>dotnetcli</c>; else null.
    /// </summary>
    public virtual string? Find(string? hive, string? sdkVersion)
    {
        var settings = hive ?? Path.Combine(_variable("DOTNET_CLI_HOME") is { Length: > 0 } cliHome ? cliHome : _home, ".templateengine");
        var cli = Path.Combine(settings, "dotnetcli");
        if (sdkVersion is { Length: > 0 } version && Path.Combine(cli, version.Trim(), "templatecache.json") is var exact && File.Exists(exact))
        {
            return exact;
        }

        try
        {
            return Directory.Exists(cli)
                ? Directory.EnumerateDirectories(cli).Select(d => Path.Combine(d, "templatecache.json")).Where(File.Exists).MaxBy(File.GetLastWriteTimeUtc)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
