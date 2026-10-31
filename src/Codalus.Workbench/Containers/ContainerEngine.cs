using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Containers;

/// <summary>A container as <c>ps -a</c> lists it. <see cref="State"/> is <c>running</c>, <c>exited</c>, <c>created</c>…</summary>
public sealed record ContainerSummary(string Id, string Name, string Image, string State, string Status, string Ports)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An image as <c>images</c> lists it.</summary>
public sealed record ImageSummary(string Repository, string Tag, string Id, string Size, string Created)
{
    /// <summary><c>repository:tag</c>, or the id for an untagged image.</summary>
    public string Reference => Repository is "<none>" or "" ? Id : Tag is "<none>" or "" ? Repository : Repository + ":" + Tag;
}

/// <summary>A published port: <see cref="ContainerPort"/> (<c>8080/tcp</c>) on <see cref="HostIp"/>:<see cref="HostPort"/>.</summary>
public sealed record ContainerPortMapping(string ContainerPort, string? HostIp, string? HostPort);

/// <summary>A mount: <see cref="Source"/> on the host (or a volume's name) at <see cref="Destination"/> in the container.</summary>
public sealed record ContainerMount(string Type, string Source, string Destination, bool ReadOnly);

/// <summary>What <c>inspect</c> says about a container, for the Containers window.</summary>
public sealed record ContainerDetails(
    string Id,
    string Name,
    string Image,
    string State,
    int? ProcessId,
    int? ExitCode,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    IReadOnlyList<ContainerPortMapping> Ports,
    IReadOnlyList<ContainerMount> Mounts,
    IReadOnlyList<KeyValuePair<string, string>> Labels,
    string? WorkingDirectory,
    string Command);

/// <summary>An entry of a folder in a container: <see cref="IsDirectory"/> from <c>ls -p</c>'s trailing slash.</summary>
public sealed record ContainerFile(string Name, bool IsDirectory);

/// <summary>The outcome of a container command: its exit code and its output (stdout, then stderr).</summary>
public sealed record ContainerResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// The user's container engine (M17a, ADR 0042): Docker or Podman, through their own CLI, one process per operation
/// with its output parsed (Go templates with tabs, and <c>inspect</c>'s JSON), never the Engine API and never a daemon
/// of ours. <see cref="Tool"/> is the CLI's command line (<c>docker</c>, <c>podman</c>, or <c>wsl -d Ubuntu --exec podman</c>).
/// Everything is cancellable: a cancelled command's process is ended.
/// </summary>
public sealed class ContainerEngine(IReadOnlyList<string> tool)
{
    /// <summary>The setting with the container CLI (empty: Docker if it is installed, else Podman).</summary>
    public const string ToolSetting = "containers.tool";

    /// <summary>The label of every container Codalus IDE creates, with the project (or compose file) it runs.</summary>
    public const string ProjectLabel = "dev.codalus.project";

    /// <summary>Podman's <c>docker</c> command says so on stderr; it isn't an error.</summary>
    private const string PodmanShimNote = "Emulate Docker CLI using podman";

    public IReadOnlyList<string> Tool { get; } = tool.Count > 0 ? tool : ["docker"];

    /// <summary>The CLI's name for messages: <c>docker</c> or <c>podman</c>.</summary>
    public string Name => Path.GetFileNameWithoutExtension(Tool[^1]);

    /// <summary>Variables for every command (tests point <c>DOCKER_HOST</c> elsewhere).</summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The engine of the setting's value (split like a command line); with none, <c>docker</c> when it is on the PATH,
    /// else <c>podman</c> when it is, else <c>docker</c> (whose absence the first command reports).
    /// </summary>
    public static ContainerEngine Resolve(string? setting, Func<string, bool>? onPath = null)
    {
        var configured = Debugging.CommandLine.Split(setting ?? string.Empty);
        if (configured.Count > 0)
        {
            return new ContainerEngine(configured);
        }

        onPath ??= IsOnPath;
        return new ContainerEngine([onPath("docker") ? "docker" : onPath("podman") ? "podman" : "docker"]);
    }

    /// <summary>Whether <paramref name="program"/> (with the OS's executable extensions) is in a folder of <c>PATH</c>.</summary>
    public static bool IsOnPath(string program)
    {
        var extensions = OperatingSystem.IsWindows() ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [string.Empty];
        foreach (var folder in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(folder.Trim('"'), program + extension)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ running the CLI

    /// <summary>The program and full argument list of a command.</summary>
    public (string Program, IReadOnlyList<string> Arguments) Command(params IReadOnlyList<string> arguments) => (Tool[0], [.. Tool.Skip(1), .. arguments]);

    /// <summary>Runs one command to its end. <paramref name="onLine"/> hears each line as it arrives (builds, pulls).</summary>
    public async Task<ContainerResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<string>? onLine = null, string? workingDirectory = null)
    {
        // docker or podman on the login shell's PATH (M56): a restored Containers window lists them while it comes in.
        await Codalus.Rpc.ProcessEnvironment.WhenReadyAsync(cancellationToken).ConfigureAwait(false);
        var (program, all) = Command(arguments);
        var start = new ProcessStartInfo(program, all)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var (name, value) in Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{Name} isn't installed or isn't on the PATH ({ex.Message}). Install Docker or Podman, or set '{ToolSetting}'.", ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = new StringBuilder();
            var error = new StringBuilder();
            async Task ReadAsync(StreamReader reader, StringBuilder into)
            {
                while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith(PodmanShimNote, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lock (into)
                    {
                        into.Append(line).Append('\n');
                    }

                    onLine?.Invoke(line);
                }
            }

            var reading = Task.WhenAll(ReadAsync(process.StandardOutput, output), ReadAsync(process.StandardError, error));
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    ProcessTree.Kill(process);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                throw;
            }

            await reading.ConfigureAwait(false);
            return new ContainerResult(process.ExitCode, output.ToString(), error.ToString());
        }
    }

    private async Task<string> RequireAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{Name} {arguments[0]} failed: {result.Message}");
        }

        return result.Output;
    }

    /// <summary>
    /// The engine's server OS (<c>linux</c>, <c>windows</c>), or null with the reason when it doesn't answer (not
    /// installed, or its daemon or machine isn't running).
    /// </summary>
    public async Task<(string? Os, string? Error)> ServerOsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(["version", "--format", "{{.Server.Os}}"], cancellationToken).ConfigureAwait(false);
            var os = result.Output.Trim().Split('\n')[^1].Trim();
            return result.Succeeded && os.Length > 0 ? (os, null) : (null, result.Message.Length > 0 ? result.Message : $"{Name} version failed.");
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
    }

    // ------------------------------------------------------------------ listing

    private const string ContainerFormat = "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}\t{{.Ports}}";

    /// <summary>Every container (running or not), or only those with <paramref name="label"/> (<c>key=value</c>).</summary>
    public async Task<IReadOnlyList<ContainerSummary>> ContainersAsync(CancellationToken cancellationToken, string? label = null)
    {
        List<string> arguments = ["ps", "-a", "--no-trunc", "--format", ContainerFormat];
        if (label is not null)
        {
            arguments.AddRange(["--filter", "label=" + label]);
        }

        return ParseContainers(await RequireAsync(arguments, cancellationToken).ConfigureAwait(false));
    }

    public static IReadOnlyList<ContainerSummary> ParseContainers(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(f => f.Length >= 5 && f[0].Length > 0)
            .Select(f => new ContainerSummary(
                f[0].Length > 12 ? f[0][..12] : f[0],
                f[1].Trim('[', ']').Split(',', ' ')[0].TrimStart('/'),
                f[2],
                f[3].ToLowerInvariant(),
                f[4],
                f.Length > 5 ? f[5] : string.Empty))];

    private const string ImageFormat = "{{.Repository}}\t{{.Tag}}\t{{.ID}}\t{{.Size}}\t{{.CreatedSince}}";

    public async Task<IReadOnlyList<ImageSummary>> ImagesAsync(CancellationToken cancellationToken) =>
        ParseImages(await RequireAsync(["images", "--format", ImageFormat], cancellationToken).ConfigureAwait(false));

    public static IReadOnlyList<ImageSummary> ParseImages(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(f => f.Length >= 4)
            .Select(f => new ImageSummary(f[0], f[1], f[2].Replace("sha256:", string.Empty, StringComparison.Ordinal) is var id && id.Length > 12 ? id[..12] : f[2], f[3], f.Length > 4 ? f[4] : string.Empty))];

    /// <summary>A container's configuration and state (<c>inspect</c>); null when it doesn't exist.</summary>
    public async Task<ContainerDetails?> InspectAsync(string container, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["inspect", "--type", "container", container], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ParseInspect(result.Output) : null;
    }

    /// <summary>Reads <c>inspect</c>'s JSON (an array with one container), as Docker and Podman write it.</summary>
    public static ContainerDetails? ParseInspect(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var config = Get(root, "Config");
            var state = Get(root, "State");
            List<KeyValuePair<string, string>> environment = [];
            foreach (var entry in Array(config, "Env").Where(e => e.ValueKind == JsonValueKind.String))
            {
                var text = entry.GetString() ?? string.Empty;
                var equals = text.IndexOf('=', StringComparison.Ordinal);
                environment.Add(equals < 0 ? new(text, string.Empty) : new(text[..equals], text[(equals + 1)..]));
            }

            List<ContainerPortMapping> ports = [];
            if (Get(Get(root, "NetworkSettings"), "Ports") is { ValueKind: JsonValueKind.Object } portMap)
            {
                foreach (var port in portMap.EnumerateObject())
                {
                    if (port.Value.ValueKind != JsonValueKind.Array || port.Value.GetArrayLength() == 0)
                    {
                        ports.Add(new ContainerPortMapping(port.Name, null, null));
                        continue;
                    }

                    foreach (var binding in port.Value.EnumerateArray())
                    {
                        ports.Add(new ContainerPortMapping(port.Name, String(binding, "HostIp"), String(binding, "HostPort")));
                    }
                }
            }

            List<ContainerMount> mounts = [];
            foreach (var mount in Array(root, "Mounts"))
            {
                mounts.Add(new ContainerMount(
                    String(mount, "Type") ?? "bind",
                    String(mount, "Source") ?? String(mount, "Name") ?? string.Empty,
                    String(mount, "Destination") ?? string.Empty,
                    mount.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.False));
            }

            List<KeyValuePair<string, string>> labels = [];
            if (Get(config, "Labels") is { ValueKind: JsonValueKind.Object } labelMap)
            {
                labels.AddRange(labelMap.EnumerateObject().Select(l => new KeyValuePair<string, string>(l.Name, l.Value.ValueKind == JsonValueKind.String ? l.Value.GetString()! : l.Value.ToString())));
            }

            var command = string.Join(' ', Array(config, "Entrypoint").Concat(Array(config, "Cmd")).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            var image = String(config, "Image") ?? String(root, "ImageName") ?? String(root, "Image") ?? string.Empty;
            var id = String(root, "Id") ?? string.Empty;
            return new ContainerDetails(
                id.Length > 12 ? id[..12] : id,
                (String(root, "Name") ?? string.Empty).TrimStart('/'),
                image,
                (String(state, "Status") ?? string.Empty).ToLowerInvariant(),
                Int(state, "Pid") is > 0 and var pid ? pid : null,
                Int(state, "ExitCode"),
                environment,
                ports,
                mounts,
                [.. labels.OrderBy(l => l.Key, StringComparer.Ordinal)],
                String(config, "WorkingDir") is { Length: > 0 } workingDirectory ? workingDirectory : null,
                command);
        }

        static JsonElement Get(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

        static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
            Get(element, name) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

        static string? String(JsonElement element, string name) =>
            Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        static int? Int(JsonElement element, string name) =>
            Get(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
    }

    /// <summary>The last <paramref name="tail"/> lines of a container's output (stdout and stderr), with timestamps off.</summary>
    public async Task<string> LogsAsync(string container, int tail, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), container], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Output + result.Error : throw new InvalidOperationException($"{Name} logs failed: {result.Message}");
    }

    /// <summary>
    /// The entries of <paramref name="folder"/> in a running container (<c>ls -1Ap</c>, which BusyBox has too), folders
    /// first.
    /// </summary>
    public async Task<IReadOnlyList<ContainerFile>> ListFilesAsync(string container, string folder, CancellationToken cancellationToken)
    {
        var output = await RequireAsync(["exec", container, "ls", "-1Ap", folder], cancellationToken).ConfigureAwait(false);
        return ParseFiles(output);
    }

    public static IReadOnlyList<ContainerFile> ParseFiles(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && l is not "./" and not "../")
            .Select(l => new ContainerFile(l.TrimEnd('/'), l.EndsWith('/')))
            .OrderBy(f => !f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.Ordinal)];

    /// <summary>Copies a file out of a container (<c>cp</c>) into <paramref name="destination"/>.</summary>
    public async Task CopyOutAsync(string container, string path, string destination, CancellationToken cancellationToken) =>
        await RequireAsync(["cp", container + ":" + path, destination], cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ actions

    public Task StartAsync(string container, CancellationToken cancellationToken) => RequireAsync(["start", container], cancellationToken);

    public Task StopAsync(string container, CancellationToken cancellationToken) => RequireAsync(["stop", "-t", "5", container], cancellationToken);

    public Task RestartAsync(string container, CancellationToken cancellationToken) => RequireAsync(["restart", "-t", "5", container], cancellationToken);

    /// <summary>Removes a container, running or not; one that doesn't exist is no error.</summary>
    public async Task RemoveAsync(string container, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["rm", "-f", container], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !NotFound(result))
        {
            throw new InvalidOperationException($"{Name} rm failed: {result.Message}");
        }
    }

    public Task RemoveImageAsync(string image, CancellationToken cancellationToken) => RequireAsync(["rmi", image], cancellationToken);

    private static bool NotFound(ContainerResult result) =>
        result.Message.Contains("no such container", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("no container with name or ID", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds an image (<c>build</c>): <paramref name="dockerfile"/> with <paramref name="context"/>, tagged
    /// <paramref name="tag"/>, up to the stage <paramref name="target"/> when given. Output goes to <paramref name="onLine"/>.
    /// </summary>
    public Task<ContainerResult> BuildAsync(string dockerfile, string context, string tag, string? target, Action<string>? onLine, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? buildArguments = null)
    {
        List<string> arguments = ["build", "-f", dockerfile, "-t", tag];
        if (target is not null)
        {
            arguments.AddRange(["--target", target]);
        }

        foreach (var (name, value) in buildArguments ?? new Dictionary<string, string>())
        {
            arguments.AddRange(["--build-arg", name + "=" + value]);
        }

        arguments.Add(context);
        return RunAsync(arguments, cancellationToken, onLine);
    }

    /// <summary>The arguments of <c>run -d</c> for <paramref name="spec"/> (detached; the container's id is printed).</summary>
    public static List<string> RunArguments(ContainerRunSpec spec)
    {
        List<string> arguments = ["run", "-d", "--name", spec.Name, .. spec.ExtraArguments];
        foreach (var (name, value) in spec.Labels)
        {
            arguments.AddRange(["--label", name + "=" + value]);
        }

        foreach (var port in spec.Ports)
        {
            arguments.AddRange(["-p", port]);
        }

        foreach (var mount in spec.Mounts)
        {
            arguments.AddRange(["-v", mount]);
        }

        foreach (var (name, value) in spec.Environment)
        {
            arguments.AddRange(["-e", name + "=" + value]);
        }

        if (spec.WorkingDirectory is { } workingDirectory)
        {
            arguments.AddRange(["-w", workingDirectory]);
        }

        if (spec.User is { } user)
        {
            arguments.AddRange(["--user", user]);
        }

        if (spec.Entrypoint is { } entrypoint)
        {
            arguments.AddRange(["--entrypoint", entrypoint]);
        }

        arguments.Add(spec.Image);
        arguments.AddRange(spec.Command);
        return arguments;
    }

    /// <summary>Starts a detached container; throws with the engine's message when it can't.</summary>
    public async Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken cancellationToken)
    {
        var result = await RunAsync(RunArguments(spec), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await RemoveAsync(spec.Name, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"The container {spec.Name} didn't start: {result.Message}");
        }
    }

    /// <summary>The host port <paramref name="containerPort"/> (<c>8080/tcp</c>) is published on, or null.</summary>
    public async Task<int?> HostPortAsync(string container, string containerPort, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["port", container, containerPort], cancellationToken).ConfigureAwait(false);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim().Split(':')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return port;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ compose

    /// <summary>The arguments of a compose command on <paramref name="file"/> as project <paramref name="project"/>.</summary>
    public static List<string> ComposeArguments(string file, string project, params IReadOnlyList<string> command) =>
        ["compose", "-f", file, "-p", project, .. command];

    /// <summary>A compose project's name from its folder, as compose itself derives it (lower case, letters, digits, - and _).</summary>
    public static string ComposeProjectName(string composeFile)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(composeFile))) ?? "app";
        var name = new string([.. folder.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return name.Length == 0 ? "app" : name;
    }
}

/// <summary>A container to start: everything <c>run</c> takes that Codalus IDE uses.</summary>
public sealed record ContainerRunSpec(string Name, string Image)
{
    public IReadOnlyList<KeyValuePair<string, string>> Labels { get; init; } = [];

    /// <summary><c>-p</c> values: <c>127.0.0.1:5000:8080</c>.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary><c>-v</c> values: <c>C:\src\bin:/app:ro</c>.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? User { get; init; }

    public string? Entrypoint { get; init; }

    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>More <c>run</c> options, from the launch profile's <c>containerRunArguments</c>.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
