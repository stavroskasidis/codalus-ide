using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// WCF service references (M42, ADR 0066) as <c>dotnet-svcutil</c> and Visual Studio leave them: a folder (under
/// <c>Connected Services</c>, or the tool's default <c>ServiceReference</c>) with the generated <c>Reference.cs</c>,
/// the tool's <c>dotnet-svcutil.params.json</c>, and Visual Studio's <c>ConnectedService.json</c>, whose
/// <c>ExtendedData</c> holds the same options. Either file is enough for <c>dotnet-svcutil --update</c>.
/// </summary>
internal static class WcfReferences
{
    public const string Tool = "dotnet-svcutil";
    public const string ConnectedServicesFolder = "Connected Services";
    public const string ParamsFile = "dotnet-svcutil.params.json";
    public const string ConnectedServiceFile = "ConnectedService.json";
    public const string OutputFile = "Reference.cs";

    /// <summary>Visual Studio's WCF provider, which <c>ConnectedService.json</c> names.</summary>
    public const string VisualStudioProvider = "Microsoft.VisualStudio.ConnectedService.Wcf";

    private const string SvcutilProvider = "Microsoft.Tools.ServiceModel.Svcutil";

    /// <summary>The packages <c>dotnet-svcutil</c> adds, to report the ones a project has.</summary>
    public static readonly string[] Packages =
    [
        "System.ServiceModel.Http", "System.ServiceModel.Primitives", "System.ServiceModel.Duplex", "System.ServiceModel.NetTcp",
        "System.ServiceModel.Security", "System.ServiceModel.Federation",
    ];

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    /// <summary>A reference's name: a folder name that is also a namespace's last part.</summary>
    public static void RequireName(string name)
    {
        if (name.Length == 0 || name.Length > 100 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            throw new ArgumentException($"'{name}' isn't a valid reference name: use letters, digits, '_' and '.', starting with a letter.");
        }
    }

    public static string FolderOf(string projectDirectory, string name) => Path.Combine(projectDirectory, ConnectedServicesFolder, name);

    /// <summary>The project's WCF references: folders under <c>Connected Services</c> and its own folders that hold the options of either tool.</summary>
    public static ServiceReferenceInfo[] Read(string projectDirectory)
    {
        var candidates = new List<string>();
        var connected = Path.Combine(projectDirectory, ConnectedServicesFolder);
        if (Directory.Exists(connected))
        {
            candidates.AddRange(Directory.EnumerateDirectories(connected));
        }

        if (Directory.Exists(projectDirectory))
        {
            candidates.AddRange(Directory.EnumerateDirectories(projectDirectory)
                .Where(d => Path.GetFileName(d) is not ("bin" or "obj" or ConnectedServicesFolder) && !Path.GetFileName(d).StartsWith('.')));
        }

        var references = new List<ServiceReferenceInfo>();
        foreach (var folder in candidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadFolder(projectDirectory, folder) is { } reference)
            {
                references.Add(reference);
            }
        }

        return [.. references];
    }

    /// <summary>A folder's reference: its options from <c>ConnectedService.json</c> (Visual Studio's WCF provider) or the tool's own file.</summary>
    public static ServiceReferenceInfo? ReadFolder(string projectDirectory, string folder)
    {
        JsonNode? options = null;
        string? file = null;
        var connectedService = Path.Combine(folder, ConnectedServiceFile);
        var parameters = Path.Combine(folder, ParamsFile);
        try
        {
            if (File.Exists(connectedService) && JsonNode.Parse(File.ReadAllText(connectedService)) is JsonObject cs
                && (string?)cs["ProviderId"] == VisualStudioProvider && cs["ExtendedData"] is JsonObject extended)
            {
                (options, file) = (extended, connectedService);
            }
            else if (File.Exists(parameters) && JsonNode.Parse(File.ReadAllText(parameters)) is JsonObject p && p["options"] is JsonObject own)
            {
                (options, file) = (own, parameters);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (options is null || file is null)
        {
            return null;
        }

        var input = options["inputs"] is JsonArray { Count: > 0 } inputs ? (string?)inputs[0] : null;
        var mapping = options["namespaceMappings"] is JsonArray { Count: > 0 } mappings ? (string?)mappings[0] : null;
        var ns = mapping?.Split(',', 2) is [_, var clr] ? clr.Trim() : null;
        var resolved = input is null || IsUrl(input) ? input : Path.GetFullPath(Path.Combine(folder, input));
        var wcf = new WcfOptions(
            Operations: Bool(options, "syncOnly") ? WcfOperationKinds.Sync : Bool(options, "sync") ? WcfOperationKinds.Both : WcfOperationKinds.Async,
            CollectionType: options["collectionTypes"] is JsonArray { Count: > 0 } collections ? (string?)collections[0] : null,
            Internal: Bool(options, "internalTypeAccess"),
            Serializer: (string?)options["serializerMode"],
            ReuseTypes: (string?)options["typeReuseMode"] is not "None",
            MessageContracts: Bool(options, "messageContract"),
            DataBinding: Bool(options, "enableDataBinding"));
        return new ServiceReferenceInfo(
            ServiceReferenceKinds.Wcf,
            file,
            Path.GetRelativePath(projectDirectory, folder),
            resolved is not null && IsUrl(resolved) ? resolved : null,
            ns,
            Path.GetFileName(folder),
            Tool,
            null,
            null,
            wcf.Internal ? "Internal" : "Public",
            resolved,
            wcf);
    }

    private static bool Bool(JsonNode options, string name) => options[name] is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    public static bool IsUrl(string input) => Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>
    /// The tool's command line for a new reference (run in the project's folder): the input, the folder, the file, the
    /// namespace, the project and the options. <paramref name="input"/> is a full path or an address.
    /// </summary>
    public static List<string> AddArguments(string project, string name, string input, string ns, WcfOptions? options)
    {
        var o = options ?? new WcfOptions();
        var args = new List<string>
        {
            input,
            "--outputDir", Path.Combine(ConnectedServicesFolder, name),
            "--outputFile", OutputFile,
            "--namespace", "*," + ns,
            "--projectFile", project,
            "--noLogo",
        };
        switch (o.Operations)
        {
            case WcfOperationKinds.Both:
                args.Add("--sync");
                break;
            case WcfOperationKinds.Sync:
                args.Add("--syncOnly");
                break;
            case WcfOperationKinds.Async or null:
                break;
            default:
                throw new ArgumentException($"'{o.Operations}' isn't an operations choice: use {WcfOperationKinds.Async}, {WcfOperationKinds.Both} or {WcfOperationKinds.Sync}.");
        }

        if (o.CollectionType is { Length: > 0 } collection)
        {
            args.AddRange(["--collectionType", collection.Trim()]);
        }

        if (o.Serializer is { Length: > 0 } serializer && serializer != "Auto")
        {
            if (serializer is not ("DataContractSerializer" or "XmlSerializer"))
            {
                throw new ArgumentException($"'{serializer}' isn't a serializer: use Auto, DataContractSerializer or XmlSerializer.");
            }

            args.AddRange(["--serializer", serializer]);
        }

        if (o.Internal)
        {
            args.Add("--internal");
        }

        if (!o.ReuseTypes)
        {
            args.Add("--noTypeReuse");
        }

        if (o.MessageContracts)
        {
            args.Add("--messageContract");
        }

        if (o.DataBinding)
        {
            args.Add("--enableDataBinding");
        }

        return args;
    }

    /// <summary>
    /// Writes Visual Studio's <c>ConnectedService.json</c> from the tool's options file, so Visual Studio lists and
    /// updates the reference too. Returns the file, or null when the tool wrote no options.
    /// </summary>
    public static string? WriteConnectedService(string folder)
    {
        var parameters = Path.Combine(folder, ParamsFile);
        if (!File.Exists(parameters) || JsonNode.Parse(File.ReadAllText(parameters)) is not JsonObject p
            || p["options"] is not JsonObject options || (string?)p["providerId"] is { } provider && provider != SvcutilProvider)
        {
            return null;
        }

        var connectedService = new JsonObject
        {
            ["ExtendedData"] = options.DeepClone(),
            ["ProviderId"] = VisualStudioProvider,
            ["Version"] = (string?)p["version"] ?? "8.0.0",
        };
        var file = Path.Combine(folder, ConnectedServiceFile);
        File.WriteAllText(file, connectedService.ToJsonString(s_indented) + Environment.NewLine);
        return file;
    }
}
