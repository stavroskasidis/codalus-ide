using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Setup;

/// <summary>How a tool stands.</summary>
public enum ToolState
{
    Found,

    /// <summary>Not installed (or not found where it's looked for).</summary>
    Missing,

    /// <summary>Installed, but not what's needed (a solution's <c>global.json</c> asks for an SDK that isn't there).</summary>
    Mismatch,
}

/// <summary>One tool Codalus IDE uses, as found on this machine (M37).</summary>
/// <param name="Id">The tool: <c>dotnet</c>, <c>git</c>, <c>git-lfs</c>, <c>docker</c>, <c>podman</c> or <c>wsl</c>.</param>
/// <param name="Required">Whether the IDE needs it (the SDK and git), or only some features do.</param>
/// <param name="NeededFor">What it's needed for, as the user reads it.</param>
public sealed record ToolStatus(string Id, string Title, bool Required, ToolState State, string NeededFor)
{
    public string? Version { get; init; }

    public string? Path { get; init; }

    /// <summary>Why it's <see cref="ToolState.Mismatch"/> or missing, beyond "not found".</summary>
    public string? Detail { get; init; }

    /// <summary>For a mismatched SDK: the version <c>global.json</c> asks for.</summary>
    public string? WantedVersion { get; init; }

    public bool NeedsAttention => State != ToolState.Found;
}

/// <summary>Runs a program and gives its exit code and output (tests use a stand-in).</summary>
public delegate Task<(int ExitCode, string Output)> ProcessRunner(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken);

/// <summary>
/// What's installed of the tools Codalus IDE needs (M37): the .NET SDK (its hosts run on .NET, and MSBuild comes
/// from the SDK) and git, and what some features need: Git LFS, Docker or Podman, WSL. A solution's folder is checked
/// against its <c>global.json</c> by asking <c>dotnet</c> itself, so its roll-forward rules decide.
/// </summary>
public sealed class EnvironmentCheck(PlatformKind platform, Func<string, string?> environment, ProcessRunner? run = null, Func<string, bool>? exists = null)
{
    private readonly ProcessRunner _run = run ?? RunAsync;
    private readonly Func<string, bool> _exists = exists ?? File.Exists;

    /// <summary>Checks everything; <paramref name="solutionFolder"/> also checks its <c>global.json</c>.</summary>
    public async Task<IReadOnlyList<ToolStatus>> CheckAsync(string? solutionFolder, CancellationToken cancellationToken)
    {
        var dotnet = await DotnetAsync(solutionFolder, cancellationToken).ConfigureAwait(false);
        var git = await VersionOfAsync("git", environment("CODALUS_GIT") is { Length: > 0 } g ? g : FindOnPath("git"), ["--version"], "git version ", cancellationToken).ConfigureAwait(false);
        var tools = new List<ToolStatus>
        {
            dotnet,
            Status("git", "Git", required: true, git, "Source control, and cloning repositories"),
        };

        if (git.State == ToolState.Found)
        {
            var lfs = await VersionOfAsync("git-lfs", git.Path, ["lfs", "version"], "git-lfs/", cancellationToken).ConfigureAwait(false);
            tools.Add(Status("git-lfs", "Git LFS", required: false, lfs, "Repositories that keep large files in Git LFS"));
        }

        var docker = await VersionOfAsync("docker", FindOnPath("docker"), ["--version"], "Docker version ", cancellationToken).ConfigureAwait(false);
        var podman = await VersionOfAsync("podman", FindOnPath("podman"), ["--version"], "podman version ", cancellationToken).ConfigureAwait(false);
        tools.Add(Status("docker", "Docker", required: false, docker, "Containers, and Aspire's container resources (or Podman)"));
        tools.Add(Status("podman", "Podman", required: false, podman, "Containers, and Aspire's container resources (or Docker)"));
        if (platform == PlatformKind.Windows)
        {
            var wsl = FindOnPath("wsl") is { } wslPath ? new Probe(ToolState.Found, wslPath, null) : new Probe(ToolState.Missing, null, null);
            tools.Add(Status("wsl", "WSL", required: false, wsl, "Running, debugging and remote windows in Linux distributions"));
        }

        return tools;
    }

    private static ToolStatus Status(string id, string title, bool required, Probe probe, string neededFor) =>
        new(id, title, required, probe.State, neededFor) { Version = probe.Version, Path = probe.Path };

    private sealed record Probe(ToolState State, string? Path, string? Version);

    /// <summary>The .NET SDK: <c>dotnet</c> where it's looked for, its SDKs, and the solution folder's <c>global.json</c>.</summary>
    private async Task<ToolStatus> DotnetAsync(string? solutionFolder, CancellationToken cancellationToken)
    {
        const string NeededFor = "Opening, building, running and debugging .NET projects; Codalus IDE's own helper processes";
        var dotnet = FindDotnet();
        if (dotnet is null)
        {
            return new("dotnet", ".NET SDK", true, ToolState.Missing, NeededFor) { Detail = "No `dotnet` was found." };
        }

        var (code, output) = await TryRunAsync(dotnet, ["--list-sdks"], null, cancellationToken).ConfigureAwait(false);
        var sdks = code == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')[0]).Where(v => v.Length > 0).ToList()
            : [];
        if (sdks.Count == 0)
        {
            return new("dotnet", ".NET SDK", true, ToolState.Missing, NeededFor) { Path = dotnet, Detail = "`dotnet` is there, but no SDK is installed (only runtimes)." };
        }

        var newest = sdks[^1];
        if (solutionFolder is not null && GlobalJson(solutionFolder) is { } globalJson)
        {
            // dotnet applies global.json's version and roll-forward rules; it fails when no SDK matches.
            var (resolveCode, resolved) = await TryRunAsync(dotnet, ["--version"], solutionFolder, cancellationToken).ConfigureAwait(false);
            if (resolveCode != 0)
            {
                return new("dotnet", ".NET SDK", true, ToolState.Mismatch, NeededFor)
                {
                    Path = dotnet,
                    Version = newest,
                    WantedVersion = globalJson.Version,
                    Detail = $"{globalJson.Path} asks for the .NET SDK {globalJson.Version}, which isn't installed (installed: {string.Join(", ", sdks)}).",
                };
            }

            return new("dotnet", ".NET SDK", true, ToolState.Found, NeededFor) { Path = dotnet, Version = resolved.Trim(), Detail = $"Pinned by {globalJson.Path}" };
        }

        return new("dotnet", ".NET SDK", true, ToolState.Found, NeededFor) { Path = dotnet, Version = newest };
    }

    /// <summary><c>dotnet</c>: <c>DOTNET_ROOT</c>, then <c>PATH</c>, then where the installers put it.</summary>
    public string? FindDotnet()
    {
        var exe = platform == PlatformKind.Windows ? "dotnet.exe" : "dotnet";
        if (environment("DOTNET_ROOT") is { Length: > 0 } root && _exists(System.IO.Path.Combine(root, exe)))
        {
            return System.IO.Path.Combine(root, exe);
        }

        if (FindOnPath("dotnet") is { } onPath)
        {
            return onPath;
        }

        var home = environment("HOME") ?? environment("USERPROFILE") ?? string.Empty;
        string[] candidates = platform switch
        {
            PlatformKind.Windows => [System.IO.Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "dotnet"), System.IO.Path.Combine(environment("LOCALAPPDATA") ?? string.Empty, "Microsoft", "dotnet")],
            PlatformKind.MacOS => ["/usr/local/share/dotnet", System.IO.Path.Combine(home, ".dotnet")],
            _ => [System.IO.Path.Combine(home, ".dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet"],
        };
        return candidates.Select(c => System.IO.Path.Combine(c, exe)).FirstOrDefault(_exists);
    }

    /// <summary>A program on <c>PATH</c> (with Windows' extensions), or null.</summary>
    public string? FindOnPath(string name)
    {
        string[] extensions = platform == PlatformKind.Windows ? [".exe", ".cmd", ".bat"] : [string.Empty];
        var separator = platform == PlatformKind.Windows ? ';' : ':';
        foreach (var folder in (environment("PATH") ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = System.IO.Path.Combine(folder.Trim('"'), name + extension);
                if (_exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<Probe> VersionOfAsync(string id, string? program, IReadOnlyList<string> arguments, string prefix, CancellationToken cancellationToken)
    {
        if (program is null)
        {
            return new(ToolState.Missing, null, null);
        }

        var (code, output) = await TryRunAsync(program, arguments, null, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return new(ToolState.Missing, program, null);
        }

        var line = output.Split('\n').FirstOrDefault(l => l.Contains(prefix, StringComparison.OrdinalIgnoreCase))?.Trim() ?? output.Trim();
        var at = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        var version = at >= 0 ? line[(at + prefix.Length)..].Split(' ', ',', '(')[0] : line;
        return new(ToolState.Found, program, version.Length == 0 ? null : version);
    }

    private async Task<(int, string)> TryRunAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
    {
        try
        {
            return await _run(program, arguments, directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>The nearest <c>global.json</c> with an SDK version at or above <paramref name="folder"/>.</summary>
    public static (string Path, string Version)? GlobalJson(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            var file = System.IO.Path.Combine(current.FullName, "global.json");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return document.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } v
                    ? (file, v)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (directory is not null)
        {
            start.WorkingDirectory = directory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} didn't start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{program} didn't answer within 30 seconds.");
        }

        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
