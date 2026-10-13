using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Containers;

/// <summary>
/// What a <c>devcontainer.json</c> asks for, in the subset Daedalus Studio implements (M21b, ADR 0053): an image or a
/// Dockerfile, the workspace's place in the container, environment, run arguments, mounts, ports to forward, the user,
/// and the post-create command. What it doesn't implement (features, Docker Compose, lifecycle commands other than
/// <c>postCreateCommand</c>) is listed in <see cref="Unsupported"/>, so the user hears about it.
/// </summary>
public sealed record DevContainerConfig(string File, string Name)
{
    public string? Image { get; init; }

    public string? Dockerfile { get; init; }

    public string? BuildContext { get; init; }

    public IReadOnlyDictionary<string, string> BuildArguments { get; init; } = new Dictionary<string, string>();

    /// <summary>The workspace in the container (<c>workspaceFolder</c>; default <c>/workspaces/&lt;folder&gt;</c>).</summary>
    public string WorkspaceFolder { get; init; } = "/workspaces";

    public IReadOnlyList<int> ForwardPorts { get; init; } = [];

    /// <summary><c>postCreateCommand</c> as one shell command line, or null.</summary>
    public string? PostCreateCommand { get; init; }

    public IReadOnlyDictionary<string, string> ContainerEnv { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> RunArgs { get; init; } = [];

    /// <summary><c>mounts</c>, as <c>-v</c> or <c>--mount</c> values.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];

    public string? RemoteUser { get; init; }

    /// <summary>Properties this subset ignores (each said once to the user).</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];
}

/// <summary>
/// Dev Containers (M21b, ADR 0053), without the Dev Containers CLI: the folder's <c>devcontainer.json</c> is read here,
/// the image is built or used as named, and a container runs with the folder mounted at the workspace, kept alive
/// (<c>sleep infinity</c>, as the CLI's <c>overrideCommand</c>), labelled with the folder so that opening it again reuses
/// it. <c>postCreateCommand</c> runs once, when the container is created. A remote window then opens in it.
/// </summary>
public static class DevContainers
{
    public const string FolderLabel = "dev.daedalus.devcontainer";

    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly string[] s_known =
    [
        "name", "image", "build", "dockerFile", "dockerfile", "context", "workspaceFolder", "workspaceMount", "forwardPorts",
        "postCreateCommand", "containerEnv", "remoteEnv", "runArgs", "mounts", "remoteUser", "containerUser",
        "overrideCommand", "customizations", "$schema",
    ];

    /// <summary>The folder's <c>devcontainer.json</c>: <c>.devcontainer/devcontainer.json</c>, <c>.devcontainer.json</c>, or the first <c>.devcontainer/*/devcontainer.json</c>.</summary>
    public static string? Find(string folder)
    {
        var candidates = new List<string> { Path.Combine(folder, ".devcontainer", "devcontainer.json"), Path.Combine(folder, ".devcontainer.json") };
        var nested = Path.Combine(folder, ".devcontainer");
        if (Directory.Exists(nested))
        {
            candidates.AddRange(Directory.EnumerateDirectories(nested).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "devcontainer.json")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Reads <paramref name="file"/> for the workspace <paramref name="localFolder"/>.</summary>
    public static DevContainerConfig Parse(string file, string localFolder)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), s_jsonc);
        var root = document.RootElement;
        var basename = Path.GetFileName(Path.TrimEndingDirectorySeparator(localFolder));
        var workspace = String(root, "workspaceFolder") is { } w ? w : "/workspaces/" + basename;
        string Substitute(string text) => text
            .Replace("${localWorkspaceFolderBasename}", basename, StringComparison.Ordinal)
            .Replace("${localWorkspaceFolder}", localFolder, StringComparison.Ordinal)
            .Replace("${containerWorkspaceFolder}", workspace, StringComparison.Ordinal)
            .Replace("${containerWorkspaceFolderBasename}", Path.GetFileName(workspace.TrimEnd('/')), StringComparison.Ordinal);
        workspace = Substitute(workspace);

        var directory = Path.GetDirectoryName(file)!;
        string? dockerfile = null;
        string? context = null;
        var buildArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
        {
            dockerfile = String(build, "dockerfile") ?? String(build, "dockerFile");
            context = String(build, "context");
            if (build.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var arg in args.EnumerateObject())
                {
                    buildArguments[arg.Name] = Substitute(arg.Value.ToString());
                }
            }
        }

        dockerfile ??= String(root, "dockerFile") ?? String(root, "dockerfile");
        context ??= String(root, "context");

        var unsupported = root.EnumerateObject().Select(p => p.Name).Where(n => !s_known.Contains(n, StringComparer.Ordinal)).ToList();
        return new DevContainerConfig(file, String(root, "name") ?? basename)
        {
            Image = String(root, "image") is { } image ? Substitute(image) : null,
            Dockerfile = dockerfile is null ? null : Path.GetFullPath(Path.Combine(directory, dockerfile)),
            BuildContext = dockerfile is null ? null : Path.GetFullPath(Path.Combine(directory, context ?? ".")),
            BuildArguments = buildArguments,
            WorkspaceFolder = workspace,
            ForwardPorts = root.TryGetProperty("forwardPorts", out var ports) && ports.ValueKind == JsonValueKind.Array
                ? [.. ports.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out _)).Select(p => p.GetInt32())]
                : [],
            PostCreateCommand = Command(root, "postCreateCommand") is { } command ? Substitute(command) : null,
            ContainerEnv = Map(root, "containerEnv", Substitute),
            RunArgs = Strings(root, "runArgs").Select(Substitute).ToList(),
            Mounts = root.TryGetProperty("mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array
                ? [.. mounts.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String ? Substitute(m.GetString()!) : MountObject(m, Substitute)).OfType<string>()]
                : [],
            RemoteUser = String(root, "remoteUser") ?? String(root, "containerUser"),
            Unsupported = unsupported,
        };
    }

    /// <summary>The container's name for a folder: stable, so that opening the folder again finds it.</summary>
    public static string ContainerName(string localFolder)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(localFolder)).ToLowerInvariant())))[..10].ToLowerInvariant();
        var basename = new string([.. Path.GetFileName(Path.TrimEndingDirectorySeparator(localFolder)).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return $"daedalus-dev-{(basename.Length == 0 ? "folder" : basename)}-{hash}";
    }

    /// <summary>The local folder as the engine sees it: a container engine inside WSL (<c>wsl … podman</c>) needs the Linux path.</summary>
    public static string HostPath(string localFolder, ContainerEngine engine) =>
        OperatingSystem.IsWindows() && Path.GetFileNameWithoutExtension(engine.Tool[0]).Equals("wsl", StringComparison.OrdinalIgnoreCase)
            ? WslLaunch.ToLinuxPath(localFolder)
            : localFolder;

    /// <summary>The <c>run</c> of the container: the folder at the workspace, kept alive, labelled with the folder.</summary>
    public static ContainerRunSpec RunSpec(DevContainerConfig config, string localFolder, ContainerEngine engine, string image) =>
        new(ContainerName(localFolder), image)
        {
            Labels = [new(FolderLabel, Path.TrimEndingDirectorySeparator(Path.GetFullPath(localFolder))), new(ContainerEngine.ProjectLabel, Path.TrimEndingDirectorySeparator(Path.GetFullPath(localFolder)))],
            Mounts = [$"{HostPath(localFolder, engine)}:{config.WorkspaceFolder}", .. config.Mounts.Where(m => !m.Contains("type=", StringComparison.Ordinal))],
            Environment = [.. config.ContainerEnv],
            WorkingDirectory = config.WorkspaceFolder,
            ExtraArguments = [.. config.RunArgs, .. config.Mounts.Where(m => m.Contains("type=", StringComparison.Ordinal)).SelectMany(m => new[] { "--mount", m })],
            Entrypoint = "/bin/sh",
            Command = ["-c", "trap 'exit 0' TERM; while sleep 1000; do :; done"],
        };

    /// <summary>
    /// Brings the folder's dev container up: running already (reused), stopped (started), or created (the image built
    /// or used, the container run, <c>postCreateCommand</c> run in it). Returns the container's name.
    /// </summary>
    public static async Task<string> UpAsync(ContainerEngine engine, DevContainerConfig config, string localFolder, Action<string> log, CancellationToken cancellationToken)
    {
        var name = ContainerName(localFolder);
        if (await engine.InspectAsync(name, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!existing.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                log($"Starting the dev container {name}…");
                await engine.StartAsync(name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                log($"The dev container {name} is running.");
            }

            return name;
        }

        var image = config.Image;
        if (config.Dockerfile is { } dockerfile)
        {
            image = name + ":latest";
            log($"Building {Path.GetFileName(dockerfile)} as {image}…");
            var built = await engine.BuildAsync(HostPath(dockerfile, engine), HostPath(config.BuildContext!, engine), image, null, log, cancellationToken, config.BuildArguments).ConfigureAwait(false);
            if (!built.Succeeded)
            {
                throw new InvalidOperationException($"Building the dev container's image failed: {built.Message}");
            }
        }

        if (image is null)
        {
            throw new InvalidOperationException($"{config.File} names neither an image nor a Dockerfile (Docker Compose isn't supported yet).");
        }

        log($"Creating the dev container {name} from {image}…");
        var run = await engine.RunAsync(ContainerEngine.RunArguments(RunSpec(config, localFolder, engine, image)), cancellationToken, log).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            throw new InvalidOperationException($"The dev container didn't start: {run.Message}");
        }

        if (config.PostCreateCommand is { } command)
        {
            log($"postCreateCommand: {command}");
            var exec = await engine.RunAsync(["exec", "-w", config.WorkspaceFolder, .. config.RemoteUser is { } user ? new[] { "-u", user } : [], name, "sh", "-c", command], cancellationToken, log).ConfigureAwait(false);
            if (!exec.Succeeded)
            {
                log($"postCreateCommand failed (exit code {exec.ExitCode}): {exec.Message}");
            }
        }

        return name;
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            : [];

    private static Dictionary<string, string> Map(JsonElement element, string property, Func<string, string> substitute)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in value.EnumerateObject())
            {
                map[entry.Name] = substitute(entry.Value.ToString());
            }
        }

        return map;
    }

    /// <summary>A command as a string, an array (joined, quoted), or an object of commands (joined with <c>&amp;&amp;</c>).</summary>
    private static string? Command(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(' ', value.EnumerateArray().Select(a => WslLaunch.ShellQuote(a.ToString()))),
            JsonValueKind.Object => string.Join(" && ", value.EnumerateObject().Select(c => "(" + (Command(value, c.Name) ?? "true") + ")")),
            _ => null,
        };
    }

    /// <summary>A mount object (<c>{ "source", "target", "type" }</c>) as a <c>--mount</c> value.</summary>
    private static string? MountObject(JsonElement mount, Func<string, string> substitute) =>
        mount.ValueKind == JsonValueKind.Object && String(mount, "target") is { } target
            ? $"type={String(mount, "type") ?? "bind"},source={substitute(String(mount, "source") ?? string.Empty)},target={substitute(target)}"
            : null;
}
