using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Workbench.Containers;

/// <summary>A compose service's <c>build</c>: its context folder, its Dockerfile (a full path) and the stage it builds.</summary>
public sealed record ComposeBuild(string Context, string Dockerfile, string? Target);

/// <summary>A service of a compose file, as much of it as the IDE reads (M63, ADR 0102).</summary>
public sealed record ComposeService(
    string Name,
    string? Image,
    ComposeBuild? Build,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment,
    string? ContainerName,
    bool HasCommand);

/// <summary>
/// A compose app's services (M63, ADR 0102): read from the engine's <c>compose config --format json</c> (the files
/// merged, variables filled in, paths made full: what compose itself runs), or, where that can't answer
/// (podman-compose), from the files with <see cref="ComposeYaml"/>, merged in their order.
/// </summary>
public sealed record ComposeModel(string? Name, IReadOnlyList<ComposeService> Services)
{
    /// <summary>The model of <c>compose config --format json</c>'s output; null when it isn't JSON of a compose app.</summary>
    public static ComposeModel? FromJson(string json, string projectFolder)
    {
        try
        {
            // The JSON may follow other lines (warnings about variables that aren't set).
            var start = json.IndexOf('{', StringComparison.Ordinal);
            return start < 0 ? null : FromNode(JsonNode.Parse(json[start..]), projectFolder);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The compose files merged as compose merges them (a later file's values over an earlier one's), read with <see cref="ComposeYaml"/>.</summary>
    public static ComposeModel ReadFiles(IReadOnlyList<string> files)
    {
        JsonObject? merged = null;
        foreach (var file in files)
        {
            if (ComposeYaml.Parse(File.ReadAllText(file)) is not JsonObject root)
            {
                continue;
            }

            if (root["services"] is JsonObject services)
            {
                foreach (var (_, service) in services)
                {
                    if (service is JsonObject map)
                    {
                        Normalize(map, "environment");
                        Normalize(map, "labels");
                    }
                }
            }

            merged = merged is null ? root : Merge(merged, root);
        }

        return FromNode(merged, Path.GetDirectoryName(Path.GetFullPath(files[0]))!);
    }

    private static JsonObject Merge(JsonObject into, JsonObject from)
    {
        foreach (var (key, value) in from.ToList())
        {
            from.Remove(key);
            if (into[key] is JsonObject existing && value is JsonObject other)
            {
                Merge(existing, other);
            }
            else
            {
                into[key] = value;
            }
        }

        return into;
    }

    /// <summary>A list of <c>NAME=value</c> as a map, so merging files merges its entries.</summary>
    private static void Normalize(JsonObject service, string key)
    {
        if (service[key] is not JsonArray list)
        {
            return;
        }

        var map = new JsonObject();
        foreach (var item in list)
        {
            if (item?.GetValueKind() == JsonValueKind.String && item.GetValue<string>() is var text)
            {
                var equals = text.IndexOf('=', StringComparison.Ordinal);
                map[equals < 0 ? text : text[..equals]] = equals < 0 ? null : text[(equals + 1)..];
            }
        }

        service[key] = map;
    }

    /// <summary>The model of a compose document (relative paths from <paramref name="projectFolder"/>).</summary>
    public static ComposeModel FromNode(JsonNode? root, string projectFolder)
    {
        var services = new List<ComposeService>();
        if (root?["services"] is JsonObject map)
        {
            foreach (var (name, node) in map)
            {
                if (node is not JsonObject service)
                {
                    continue;
                }

                ComposeBuild? build = null;
                switch (service["build"])
                {
                    case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                        var folder = Path.GetFullPath(Path.Combine(projectFolder, value.GetValue<string>()));
                        build = new ComposeBuild(folder, Path.Combine(folder, "Dockerfile"), null);
                        break;
                    case JsonObject spec:
                        var context = Path.GetFullPath(Path.Combine(projectFolder, Text(spec["context"]) ?? "."));
                        build = new ComposeBuild(context, Path.GetFullPath(Path.Combine(context, Text(spec["dockerfile"]) ?? "Dockerfile")), Text(spec["target"]));
                        break;
                }

                services.Add(new ComposeService(
                    name,
                    Text(service["image"]),
                    build,
                    Pairs(service["labels"]),
                    Pairs(service["environment"]),
                    Text(service["container_name"]),
                    service["command"] is not null));
            }
        }

        return new ComposeModel(Text(root?["name"]), services);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        } : null;

    /// <summary>A map, or a list of <c>NAME=value</c> (as labels and environment are written either way).</summary>
    private static Dictionary<string, string> Pairs(JsonNode? node)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    pairs[key] = Text(value) ?? string.Empty;
                }

                break;
            case JsonArray list:
                foreach (var item in list)
                {
                    if (Text(item) is { } text)
                    {
                        var equals = text.IndexOf('=', StringComparison.Ordinal);
                        pairs[equals < 0 ? text : text[..equals]] = equals < 0 ? string.Empty : text[(equals + 1)..];
                    }
                }

                break;
        }

        return pairs;
    }
}

/// <summary>
/// A compose service that runs a .NET project (M63): its project, the Dockerfile it is built from and that file's
/// <c>base</c> stage (null: the Dockerfile has none, so fast mode can't run it and it runs as the compose file says).
/// <see cref="IsStartup"/> is the <c>.dcproj</c>'s <c>DockerServiceName</c>.
/// </summary>
public sealed record ComposeDotnetService(string Service, string Project, string Dockerfile, string? BaseStage, bool IsStartup);

/// <summary>A .NET service's fast-mode run: its build output and program, and whether the debugger starts it.</summary>
public sealed record ComposeFastService(ComposeDotnetService Service, string Output, string Program, bool Debug)
{
    /// <summary>Folders of this machine mounted read-only (static web assets outside the build output) and where.</summary>
    public IReadOnlyList<(string Source, string Target)> Mounts { get; init; } = [];

    /// <summary>Variables the override adds (the service's own stay as the compose file has them).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The service's compose file sets a <c>command</c>, which the override clears.</summary>
    public bool ClearCommand { get; init; }

    /// <summary>
    /// The folder of this machine mounted at <see cref="ComposeFastMode.DiagnosticsMount"/> for the program's diagnostic
    /// port (M63b): null where the engine can't share a Unix socket with this machine (not Linux, or an engine in WSL).
    /// </summary>
    public string? DiagnosticsFolder { get; init; }

    /// <summary>The socket the diagnostics host listens on for this service's runtime (in <see cref="DiagnosticsFolder"/>), or null.</summary>
    public string? DiagnosticsSocket => DiagnosticsFolder is { } folder ? Path.Combine(folder, ComposeFastMode.SocketName(Service.Service)) : null;

    /// <summary>The program's path in the container: <c>/app/App.dll</c>.</summary>
    public string ContainerProgram => ContainerLaunch.AppFolder + "/" + Program;
}

/// <summary>
/// Compose in fast mode (M63, ADR 0102), as the classic IDE runs a compose app in Debug: each .NET service runs the
/// IDE's Debug build, mounted at <c>/app</c> into an image of its Dockerfile's <c>base</c> stage, so a code change
/// is a build and a restart of the service, never an image build. The IDE writes an override file
/// (<see cref="OverrideFileName"/> in the solution's state folder, never committed) that compose reads after the
/// user's own files. A debugged service's container idles (<c>tail -f /dev/null</c>) and the debugger starts its program
/// in it; a service run without the debugger starts <c>dotnet /app/App.dll</c> itself. Services that aren't .NET run as
/// the compose file says.
/// </summary>
public static class ComposeFastMode
{
    /// <summary>The override's name in the solution's state folder (D55).</summary>
    public const string OverrideFileName = "compose.override.yml";

    /// <summary>A service label naming its project (relative to the compose file's folder), where the Dockerfile's folder doesn't tell.</summary>
    public const string ProjectLabel = ContainerEngine.ProjectLabel;

    /// <summary>The label the override gives a .NET service: <c>debug</c> (the debugger starts it) or <c>run</c>.</summary>
    public const string ModeLabel = "dev.codalus.fastMode";

    /// <summary>Where a .NET service's container mounts the IDE's diagnostics folder (M63b, ADR 0102).</summary>
    public const string DiagnosticsMount = "/codalus-diag";

    /// <summary>The runtime's variable naming the diagnostic ports it opens besides its own (.NET 5 and later).</summary>
    public const string DiagnosticPortsVariable = "DOTNET_DiagnosticPorts";

    /// <summary>The diagnostics folder's name in the solution's state folder.</summary>
    public const string DiagnosticsFolderName = "diag";

    private static readonly string[] s_projectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>A service's socket in the diagnostics folder: its name, with what a file name can't hold made <c>_</c>.</summary>
    public static string SocketName(string service) =>
        new string([.. service.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]) + ".sock";

    /// <summary>
    /// The diagnostic port a service's runtime opens (M63b): it connects to the socket the diagnostics host listens on
    /// in the mounted folder, and doesn't wait for it (<c>nosuspend</c>: it retries while nothing listens, which costs
    /// next to nothing). A port the compose file already names stays, before it.
    /// </summary>
    public static string DiagnosticPorts(string service, string? existing) =>
        (existing is { Length: > 0 } ports ? ports.TrimEnd(';') + ";" : string.Empty) + DiagnosticsMount + "/" + SocketName(service) + ",connect,nosuspend";

    /// <summary>
    /// The services that run a .NET project: the project the service's label names, else the one project in its
    /// Dockerfile's folder (of the solution's, else on disk). Services without a <c>build</c> or a project aren't .NET.
    /// </summary>
    public static IReadOnlyList<ComposeDotnetService> DotnetServices(ComposeModel model, string composeFile, IReadOnlyCollection<string> solutionProjects, string? startupService = null)
    {
        var composeFolder = Path.GetDirectoryName(Path.GetFullPath(composeFile))!;
        var found = new List<ComposeDotnetService>();
        foreach (var service in model.Services)
        {
            if (service.Build is not { } build)
            {
                continue;
            }

            string? project = null;
            if (service.Labels.TryGetValue(ProjectLabel, out var labelled) && labelled.Length > 0)
            {
                var path = Path.GetFullPath(Path.Combine(composeFolder, labelled));
                project = File.Exists(path) ? path : null;
            }

            var folder = Path.GetDirectoryName(build.Dockerfile)!;
            if (project is null)
            {
                var inSolution = solutionProjects.Where(p => IsProject(p) && SamePath(Path.GetDirectoryName(Path.GetFullPath(p))!, folder)).ToList();
                project = inSolution.Count == 1 ? Path.GetFullPath(inSolution[0]) : null;
            }

            if (project is null && Directory.Exists(folder))
            {
                var onDisk = Directory.EnumerateFiles(folder).Where(IsProject).ToList();
                project = onDisk.Count == 1 ? onDisk[0] : null;
            }

            if (project is null)
            {
                continue;
            }

            string? baseStage = null;
            try
            {
                baseStage = File.Exists(build.Dockerfile) ? DockerfileInfo.Parse(File.ReadAllText(build.Dockerfile)).BaseStage?.Name : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            found.Add(new ComposeDotnetService(service.Name, project, build.Dockerfile, baseStage, string.Equals(service.Name, startupService, StringComparison.Ordinal)));
        }

        return found;
    }

    private static bool IsProject(string path) => s_projectExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A .NET service's fast-mode run from its built project (<paramref name="targetPath"/>: the project's
    /// <c>TargetPath</c>): the build output mounted at <c>/app</c>, its static web assets, and
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> unless the compose file sets it. With <paramref name="diagnosticsFolder"/>
    /// (M63b), the folder mounted at <see cref="DiagnosticsMount"/> and the diagnostic port that the profiler connects through.
    /// </summary>
    public static ComposeFastService Plan(ComposeDotnetService service, ComposeService compose, string targetPath, bool debug, string? diagnosticsFolder = null)
    {
        var output = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        var program = Path.GetFileName(targetPath);
        if (!program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            program = Path.ChangeExtension(program, ".dll");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!compose.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT") && !compose.Environment.ContainsKey("DOTNET_ENVIRONMENT"))
        {
            environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        }

        IReadOnlyList<(string Source, string Target)> mounts = [];
        if (ContainerLaunch.StaticWebAssetMounts(output, program) is { } assets)
        {
            mounts = assets.Mounts;
            environment[ContainerLaunch.StaticWebAssetsVariable] = assets.Manifest;
        }

        if (diagnosticsFolder is not null)
        {
            environment[DiagnosticPortsVariable] = DiagnosticPorts(service.Service, compose.Environment.GetValueOrDefault(DiagnosticPortsVariable));
        }

        return new ComposeFastService(service, output, program, debug)
        {
            Mounts = mounts,
            Environment = environment,
            ClearCommand = compose.HasCommand,
            DiagnosticsFolder = diagnosticsFolder,
        };
    }

    /// <summary>The image a .NET service's <c>base</c> stage is built as: the user's own image is left alone.</summary>
    public static string ImageName(string composeProject, string service) =>
        (composeProject + "-" + service).ToLowerInvariant() + ":dev";

    /// <summary>
    /// The override file: for each .NET service its <c>base</c> stage as <see cref="ImageName"/>, the build output and
    /// static web assets mounted, the diagnostics folder mounted (M63b), its entry point (idle for the debugger, else
    /// <c>dotnet /app/App.dll</c>), and labels saying it's the IDE's. <paramref name="hostPath"/> turns a local path into one the engine mounts (WSL engines).
    /// </summary>
    public static string Override(string composeFile, string composeProject, IReadOnlyList<ComposeFastService> services, Func<string, string>? hostPath = null)
    {
        hostPath ??= p => p;
        var yaml = new StringBuilder();
        yaml.Append("# Codalus IDE's fast-mode override of ").Append(Path.GetFileName(composeFile)).Append(" (ADR 0102): written on each run, never committed.\n");
        yaml.Append("# Each .NET service runs the IDE's Debug build, mounted at /app, on its Dockerfile's base stage.\n");
        if (services.Count == 0)
        {
            yaml.Append("services: {}\n");
            return yaml.ToString();
        }

        yaml.Append("services:\n");
        foreach (var service in services)
        {
            yaml.Append("  ").Append(Quote(service.Service.Service)).Append(":\n");
            yaml.Append("    image: ").Append(Quote(ImageName(composeProject, service.Service.Service))).Append('\n');
            yaml.Append("    build:\n");
            yaml.Append("      target: ").Append(Quote(service.Service.BaseStage ?? "base")).Append('\n');
            yaml.Append("    entrypoint: ").Append(service.Debug ? "[\"tail\", \"-f\", \"/dev/null\"]" : "[\"dotnet\", " + Quote(service.ContainerProgram) + "]").Append('\n');
            if (service.ClearCommand)
            {
                yaml.Append("    command: []\n");
            }

            yaml.Append("    working_dir: ").Append(Quote(ContainerLaunch.AppFolder)).Append('\n');
            if (service.Environment.Count > 0)
            {
                yaml.Append("    environment:\n");
                foreach (var (name, value) in service.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    yaml.Append("      ").Append(Quote(name)).Append(": ").Append(Quote(value)).Append('\n');
                }
            }

            yaml.Append("    volumes:\n");
            foreach (var (source, target, readOnly) in new[] { (service.Output, ContainerLaunch.AppFolder, false) }.Concat(service.Mounts.Select(m => (m.Source, m.Target, true))))
            {
                yaml.Append("      - type: bind\n");
                yaml.Append("        source: ").Append(Quote(hostPath(source))).Append('\n');
                yaml.Append("        target: ").Append(Quote(target)).Append('\n');
                if (readOnly)
                {
                    yaml.Append("        read_only: true\n");
                }
            }

            if (service.DiagnosticsFolder is { } diagnostics)
            {
                yaml.Append("      - type: bind\n");
                yaml.Append("        source: ").Append(Quote(hostPath(diagnostics))).Append('\n');
                yaml.Append("        target: ").Append(Quote(DiagnosticsMount)).Append('\n');
            }

            yaml.Append("    labels:\n");
            yaml.Append("      ").Append(Quote(ProjectLabel)).Append(": ").Append(Quote(service.Service.Project)).Append('\n');
            yaml.Append("      ").Append(Quote(ModeLabel)).Append(": ").Append(service.Debug ? "\"debug\"" : "\"run\"").Append('\n');
        }

        return yaml.ToString();
    }

    /// <summary>A double-quoted YAML string (backslashes and quotes escaped: Windows paths stay whole).</summary>
    public static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => builder.Append("\\\\"),
                '"' => builder.Append("\\\""),
                '\n' => builder.Append("\\n"),
                '\t' => builder.Append("\\t"),
                _ => builder.Append(c),
            };
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// A build output's stamp: its newest program file's time and how many there are. A build that changed nothing
    /// (the fast up-to-date check) leaves it as it was.
    /// </summary>
    public static string BuildStamp(string output)
    {
        try
        {
            var files = Directory.EnumerateFiles(output)
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.EndsWith(".staticwebassets.runtime.container.json", StringComparison.OrdinalIgnoreCase)) // written by the IDE each run
                .ToList();
            var newest = files.Count == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
            return string.Create(CultureInfo.InvariantCulture, $"{newest}:{files.Count}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>The services whose build changed since <paramref name="before"/> (a service new to it didn't: it just started).</summary>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        [.. after.Where(s => before.TryGetValue(s.Key, out var stamp) && stamp != s.Value).Select(s => s.Key).Order(StringComparer.Ordinal)];
}
