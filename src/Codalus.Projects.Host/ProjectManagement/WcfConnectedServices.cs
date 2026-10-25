using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// WCF service references as the classic IDE keeps them (ADR 0069): <c>Connected Services/&lt;name&gt;/Reference.cs</c>
/// next to a <c>ConnectedService.json</c> whose <c>ExtendedData</c> holds <c>dotnet-svcutil</c>'s options (the keys of
/// its <c>dotnet-svcutil.params.json</c>: <c>inputs</c> first, then the others in alphabetical order), with the classic IDE's
/// provider id and version. <c>dotnet-svcutil --update</c> reads that file as well as its own, so references move between
/// the classic IDE, the command line and Codalus unchanged.
/// </summary>
internal static class WcfConnectedServices
{
    public const string ServicesFolder = "Connected Services";
    public const string ConnectedServiceFile = "ConnectedService.json";
    public const string SvcutilParamsFile = "dotnet-svcutil.params.json";
    public const string CodeFile = "Reference.cs";
    public const string ProviderId = "Microsoft.VisualStudio.ConnectedService.Wcf";

    /// <summary>The version the classic IDE's WCF Web Service Reference provider writes.</summary>
    public const string ProviderVersion = "15.0.40203.910";

    public const string DictionaryType = "System.Collections.Generic.Dictionary`2";

    /// <summary>Two-space indentation, and type names such as <c>Dictionary`2</c> written as they are (not <c>`</c>).</summary>
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The folder of a reference named <paramref name="name"/>.</summary>
    public static string FolderOf(string projectDirectory, string name) => Path.Combine(projectDirectory, ServicesFolder, name);

    /// <summary>A name is a single folder: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or <c>_</c>.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 100 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// The project's references: every <c>Connected Services/*/ConnectedService.json</c> of the WCF provider, and every
    /// <c>dotnet-svcutil.params.json</c> below the project (the tool's own layout, <c>ServiceReference/</c> by default).
    /// </summary>
    public static WcfReferenceInfo[] List(string projectDirectory)
    {
        var references = new List<WcfReferenceInfo>();
        var services = Path.Combine(projectDirectory, ServicesFolder);
        if (Directory.Exists(services))
        {
            foreach (var folder in Directory.EnumerateDirectories(services).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(folder, ConnectedServiceFile);
                if (File.Exists(file) && Read(file) is { } reference)
                {
                    references.Add(reference);
                }
            }
        }

        foreach (var file in ParamsFiles(projectDirectory, depth: 3))
        {
            if (!references.Any(r => string.Equals(r.Folder, Path.GetDirectoryName(file), StringComparison.Ordinal)) && Read(file) is { } reference)
            {
                references.Add(reference);
            }
        }

        return [.. references];
    }

    private static IEnumerable<string> ParamsFiles(string folder, int depth)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
            {
                continue;
            }

            var file = Path.Combine(directory, SvcutilParamsFile);
            if (File.Exists(file))
            {
                yield return file;
            }

            if (depth > 1)
            {
                foreach (var nested in ParamsFiles(directory, depth - 1))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>A reference from its options file; null when the file isn't a WCF reference or can't be read.</summary>
    public static WcfReferenceInfo? Read(string optionsFile)
    {
        JsonObject? options;
        try
        {
            options = OptionsOf(JsonNode.Parse(File.ReadAllText(optionsFile)) as JsonObject);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        if (options is null)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(optionsFile)!;
        var collections = Strings(options["collectionTypes"]);
        var output = options["outputFile"]?.GetValue<string>() is { Length: > 0 } name ? name : CodeFile;
        var code = Path.GetFullPath(Path.Combine(folder, output));
        return new WcfReferenceInfo(
            Path.GetFileName(folder),
            folder,
            optionsFile,
            File.Exists(code) ? code : null,
            Strings(options["inputs"]),
            NamespaceOf(Strings(options["namespaceMappings"])),
            collections.FirstOrDefault(c => !IsDictionary(c)),
            collections.FirstOrDefault(IsDictionary),
            Bool(options["sync"]),
            !string.Equals(options["typeReuseMode"]?.GetValue<string>(), "None", StringComparison.OrdinalIgnoreCase),
            Bool(options["internalTypeAccess"]),
            options["targetFramework"]?.GetValue<string>());
    }

    /// <summary>The options object: <c>ExtendedData</c> of the WCF provider's file, or <c>options</c> of svcutil's.</summary>
    private static JsonObject? OptionsOf(JsonObject? root) =>
        root is null ? null
        : root["ExtendedData"] is JsonObject extended && (root["ProviderId"]?.GetValue<string>() is null or ProviderId) ? extended
        : root["options"] is JsonObject options && root["providerId"]?.GetValue<string>() is { } provider && provider.Contains("Svcutil", StringComparison.OrdinalIgnoreCase) ? options
        : null;

    /// <summary>The CLR namespace that <c>*</c> maps to (<c>"*, Contoso.Weather"</c>).</summary>
    internal static string? NamespaceOf(IEnumerable<string> mappings)
    {
        foreach (var mapping in mappings)
        {
            var parts = mapping.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 1 || parts[0] == "*")
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static bool IsDictionary(string type) => type.Contains("Dictionary", StringComparison.Ordinal);

    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string[] Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : [];

    /// <summary>
    /// Turns the <c>dotnet-svcutil.params.json</c> that a run wrote into the classic IDE's <c>ConnectedService.json</c>
    /// (the same options under <c>ExtendedData</c>) and deletes it. Returns the file written, or null when there was none.
    /// </summary>
    public static string? ConvertParams(string folder)
    {
        var parameters = Path.Combine(folder, SvcutilParamsFile);
        if (!File.Exists(parameters))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(parameters)) as JsonObject;
        if (root?["options"] is not JsonObject options)
        {
            throw new InvalidOperationException($"{parameters} has no options.");
        }

        var file = Path.Combine(folder, ConnectedServiceFile);
        Write(file, Ordered((JsonObject)options.DeepClone()));
        File.Delete(parameters);
        return file;
    }

    /// <summary>Writes the classic IDE's file around <paramref name="options"/>.</summary>
    public static void Write(string file, JsonObject options)
    {
        var root = new JsonObject
        {
            ["ExtendedData"] = options,
            ["ProviderId"] = ProviderId,
            ["Version"] = ProviderVersion,
        };
        File.WriteAllText(file, root.ToJsonString(s_write).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>
    /// The reference's options with <paramref name="parameters"/> applied to <paramref name="existing"/> (null for a new
    /// reference): inputs, the <c>*</c> namespace mapping, the collection types, sync, type reuse and access; the rest
    /// (references, target framework, serializer…) is kept.
    /// </summary>
    public static JsonObject Apply(JsonObject? existing, WcfReferenceParams parameters, string? targetFramework)
    {
        var options = (JsonObject?)existing?.DeepClone() ?? new JsonObject { ["outputFile"] = CodeFile };
        if (parameters.Input is { Length: > 0 } input)
        {
            options["inputs"] = new JsonArray(input);
        }

        var mappings = Strings(options["namespaceMappings"]).Where(m => m.Split(',', 2, StringSplitOptions.TrimEntries) is [var target, _] && target != "*").ToList();
        mappings.Insert(0, "*, " + (parameters.Namespace is { Length: > 0 } ns ? ns.Trim() : parameters.Name));
        options["namespaceMappings"] = new JsonArray([.. mappings.Select(m => (JsonNode)JsonValue.Create(m)!)]);

        var collections = new[] { parameters.CollectionType, parameters.DictionaryCollectionType }.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
        Set(options, "collectionTypes", collections.Count == 0 ? null : new JsonArray([.. collections.Select(c => (JsonNode)JsonValue.Create(c)!)]));
        Set(options, "sync", parameters.Sync ? true : null);
        Set(options, "internalTypeAccess", parameters.Internal ? true : null);
        var reuse = options["typeReuseMode"]?.GetValue<string>();
        options["typeReuseMode"] = !parameters.ReuseTypes ? "None" : reuse is "Specified" ? reuse : "All";
        if (targetFramework is { Length: > 0 } && options["targetFramework"] is null)
        {
            options["targetFramework"] = targetFramework;
        }

        return Ordered(options);
    }

    private static void Set(JsonObject options, string key, JsonNode? value)
    {
        if (value is null)
        {
            options.Remove(key);
        }
        else
        {
            options[key] = value;
        }
    }

    /// <summary><c>inputs</c> first, then the other keys in alphabetical order, as svcutil and the classic IDE write them.</summary>
    internal static JsonObject Ordered(JsonObject options)
    {
        var ordered = new JsonObject();
        foreach (var (key, value) in options.OrderBy(p => p.Key == "inputs" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal).ToList())
        {
            options.Remove(key);
            ordered[key] = value;
        }

        return ordered;
    }

    /// <summary>The existing options of a reference folder, from <c>ConnectedService.json</c> or svcutil's file.</summary>
    public static (string File, JsonObject Options)? Existing(string folder)
    {
        foreach (var name in new[] { ConnectedServiceFile, SvcutilParamsFile })
        {
            var file = Path.Combine(folder, name);
            if (File.Exists(file) && OptionsOf(JsonNode.Parse(File.ReadAllText(file)) as JsonObject) is { } options)
            {
                return (file, options);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>dotnet-svcutil</c>'s arguments for a new reference: the input, the project, the folder and file the classic IDE
    /// uses, and the options; then <c>--noLogo</c>.
    /// </summary>
    public static List<string> AddArguments(WcfReferenceParams parameters, string project)
    {
        var args = new List<string>
        {
            parameters.Input ?? throw new ArgumentException("A URL or a WSDL file is required."),
            "--projectFile", project,
            "--outputDir", ServicesFolder + "/" + parameters.Name,
            "--outputFile", CodeFile,
            "--namespace", "*," + (parameters.Namespace is { Length: > 0 } ns ? ns.Trim() : parameters.Name),
        };
        foreach (var collection in new[] { parameters.CollectionType, parameters.DictionaryCollectionType }.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            args.AddRange(["--collectionType", collection!.Trim()]);
        }

        if (parameters.Sync)
        {
            args.Add("--sync");
        }

        if (!parameters.ReuseTypes)
        {
            args.Add("--noTypeReuse");
        }

        if (parameters.Internal)
        {
            args.Add("--internal");
        }

        args.Add("--noLogo");
        return args;
    }

    /// <summary><c>dotnet-svcutil --update</c> on a reference folder, which reads its <c>ConnectedService.json</c>.</summary>
    public static List<string> UpdateArguments(string folderRelativeToProject, string project) =>
        ["--update", folderRelativeToProject.Replace('\\', '/'), "--projectFile", project, "--noLogo"];
}
