using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Rpc;

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

    /// <summary>For SDKs whose .NET is older than the hosts' (M30b): the channel to install (<c>11.0</c>).</summary>
    public string? WantedChannel { get; init; }

    /// <summary>What was found instead (M49, the IDE's runtime: the versions under each .NET root), for a message.</summary>
    public string? Found { get; init; }

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
    private readonly ProcessRunner _run = run ?? RunProcessAsync;
    private readonly Func<string, bool> _exists = exists ?? File.Exists;

    /// <summary>
    /// The .NET the hosts need, or later (they roll forward, ADR 0025): the runtime the IDE was built for, a release
    /// candidate's full version included (<c>11.0.0-rc.2.26504.105</c>), since the hosts are built with the same SDK.
    /// </summary>
    public string HostsRuntime { get; init; } = DefaultHostsRuntime();

    /// <summary>
    /// The frameworks the IDE's hosts ask for (M49, <see cref="IdeRuntime.Requirements"/>); when not set, the
    /// <see cref="HostsRuntime"/> of <c>Microsoft.NETCore.App</c> with <c>rollForward</c> Major, as the hosts are built.
    /// </summary>
    public IReadOnlyList<FrameworkReference>? HostFrameworks { get; init; }

    /// <summary>The versions of a framework under a .NET root (tests describe fake roots).</summary>
    public Func<string, string, IReadOnlyList<FrameworkVersion>> InstalledFrameworks { get; init; } = (root, name) => HostRuntimeCheck.InstalledVersions(root, name);

    /// <summary>How the .NET host picks the root of the IDE's app hosts (tests describe a machine).</summary>
    public DotnetRootResolver? Roots { get; init; }

    private IReadOnlyList<FrameworkReference> Frameworks => HostFrameworks is { Count: > 0 } frameworks
        ? frameworks
        : [FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.TryParse(HostsRuntime, out var version) ? version : FrameworkVersion.Parse($"{Environment.Version.Major}.0.0"), RollForwardPolicy.Major)];

    private DotnetRootResolver Resolver => Roots ?? new DotnetRootResolver
    {
        OS = platform switch { PlatformKind.Windows => "windows", PlatformKind.MacOS => "macos", _ => "linux" },
        ProcessEnvironment = environment,
    };

    /// <summary>Whether every framework the IDE's hosts need resolves under <paramref name="root"/>.</summary>
    public bool HasHostRuntime(string root) => Frameworks.All(f => FrameworkResolver.Resolve(f, InstalledFrameworks(root, f.Name)) is not null);

    /// <summary>The root the IDE's app hosts start with, as the .NET host picks it in this environment (<c>DOTNET_ROOT</c> first).</summary>
    public string? HostsRoot() => Resolver.ForAppHost(environment);

    /// <summary>
    /// The IDE's own .NET runtime (M49): whether what its hosts ask for is installed under the root they'll use. When it
    /// isn't, the row says what is, under every root that has any, and the channel whose SDK brings it.
    /// </summary>
    public ToolStatus HostRuntimeStatus() => RuntimeStatus(Frameworks, anyRoot: false);

    /// <summary>
    /// Whether another set of frameworks (M49: an update's hosts, from its signed feed) would run here: under the hosts'
    /// root or any installers' folder, since the updated IDE prefers a root that has its runtime (<see cref="FindSdkRoot"/>).
    /// </summary>
    public ToolStatus RuntimeStatusFor(IReadOnlyList<FrameworkReference> frameworks) => RuntimeStatus(frameworks, anyRoot: true);

    private ToolStatus RuntimeStatus(IReadOnlyList<FrameworkReference> frameworks, bool anyRoot)
    {
        const string NeededFor = "Codalus IDE's helper processes: plugins, builds, language services and debugging";
        const string Title = ".NET runtime for Codalus IDE";
        var root = HostsRoot() ?? string.Empty;
        FrameworkVersion? Resolve(FrameworkReference reference, string folder) => folder.Length > 0 ? FrameworkResolver.Resolve(reference, InstalledFrameworks(folder, reference.Name)) : null;
        var satisfied = frameworks.Count == 0 ? root : (anyRoot ? RuntimeRoots(root) : [root]).FirstOrDefault(r => frameworks.All(f => Resolve(f, r) is not null));
        if (satisfied is not null)
        {
            return new("dotnet-runtime", Title, true, ToolState.Found, NeededFor)
            {
                Version = frameworks.Count > 0 ? Resolve(frameworks[0], satisfied)!.Text : null,
                Path = satisfied,
                Detail = $"Its hosts ask for {string.Join(", ", frameworks)}.",
            };
        }

        var missing = frameworks.First(f => Resolve(f, root) is null);
        var found = string.Join("; ", RuntimeRoots(root)
            .Select(r => (Root: r, Versions: InstalledFrameworks(r, missing.Name)))
            .Where(r => r.Versions.Count > 0)
            .Select(r => $"{FrameworkResolver.Describe(r.Versions)} in {r.Root}"));
        if (found.Length == 0)
        {
            found = "none";
        }

        return new("dotnet-runtime", Title, true, ToolState.Missing, NeededFor)
        {
            Path = root.Length > 0 ? root : null,
            WantedVersion = missing.Version.Text,
            WantedChannel = IdeRuntime.Channel(missing),
            Found = found,
            Detail = $"Codalus IDE's helper processes need {missing}, which isn't installed{(root.Length > 0 ? " in " + root : string.Empty)} (found: {found}). The .NET {IdeRuntime.Channel(missing)} SDK brings it.",
        };
    }

    /// <summary>The roots worth naming: the hosts' own, then where the installers put .NET.</summary>
    private IEnumerable<string> RuntimeRoots(string hostsRoot) =>
        new[] { hostsRoot }.Concat(InstallerFolders()).Where(r => r.Length > 0)
            .Select(r => System.IO.Path.TrimEndingDirectorySeparator(r)).Distinct(StringComparer.Ordinal);

    /// <summary>Where the installers put .NET on this OS.</summary>
    private string[] InstallerFolders()
    {
        var home = environment("HOME") ?? environment("USERPROFILE") ?? string.Empty;
        return platform switch
        {
            PlatformKind.Windows => [System.IO.Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "dotnet"), System.IO.Path.Combine(environment("LOCALAPPDATA") ?? string.Empty, "Microsoft", "dotnet")],
            PlatformKind.MacOS => ["/usr/local/share/dotnet", System.IO.Path.Combine(home, ".dotnet")],
            _ => [System.IO.Path.Combine(home, ".dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/local/share/dotnet", "/snap/dotnet-sdk/current"],
        };
    }

    /// <summary>The runtime the IDE itself was built for, from its framework description (<c>.NET 11.0.0-rc.2…</c>).</summary>
    public static string DefaultHostsRuntime()
    {
        var description = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var version = description.StartsWith(".NET ", StringComparison.Ordinal) ? description[".NET ".Length..].Split(' ', '+')[0] : string.Empty;
        return version.Length > 0 && char.IsAsciiDigit(version[0]) ? version : $"{Environment.Version.Major}.{Environment.Version.Minor}.{Environment.Version.Build}";
    }

    /// <summary>Checks everything; <paramref name="solutionFolder"/> also checks its <c>global.json</c>.</summary>
    public async Task<IReadOnlyList<ToolStatus>> CheckAsync(string? solutionFolder, CancellationToken cancellationToken)
    {
        var dotnet = await DotnetAsync(solutionFolder, cancellationToken).ConfigureAwait(false);
        var git = await VersionOfAsync("git", environment("CODALUS_GIT") is { Length: > 0 } g ? g : FindOnPath("git"), ["--version"], "git version ", cancellationToken).ConfigureAwait(false);
        var tools = new List<ToolStatus>
        {
            dotnet,
            HostRuntimeStatus(),
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

        // The runtime the hosts need is a row of its own since M49 (HostRuntimeStatus): hostfxr's rules, the hosts' root.
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

    /// <summary>
    /// Orders .NET versions as the host does when it rolls forward: by number, then a release after its previews and
    /// release candidates, which order by their labels' parts (numbers as numbers): <c>11.0.0-rc.1… &lt; 11.0.0-rc.2… &lt; 11.0.0</c>.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version Number, string? Label) Split(string v)
        {
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            var number = dash < 0 ? v : v[..dash];
            return (Version.TryParse(number, out var parsed) ? parsed : new Version(0, 0), dash < 0 ? null : v[(dash + 1)..]);
        }

        var (numberA, labelA) = Split(a);
        var (numberB, labelB) = Split(b);
        if (numberA.CompareTo(numberB) is var byNumber and not 0)
        {
            return byNumber;
        }

        if (labelA is null || labelB is null)
        {
            return labelA is null ? (labelB is null ? 0 : 1) : -1;
        }

        var partsA = labelA.Split('.');
        var partsB = labelB.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var part = long.TryParse(partsA[i], out var x) && long.TryParse(partsB[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(partsA[i], partsB[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return partsA.Length.CompareTo(partsB.Length);
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
            _ => [System.IO.Path.Combine(home, ".dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/local/share/dotnet", "/snap/dotnet-sdk/current"],
        };
        return candidates.Select(c => System.IO.Path.Combine(c, exe)).FirstOrDefault(_exists);
    }

    /// <summary>
    /// The .NET root the IDE's processes should use (M37, and the Finder launch on macOS): <c>DOTNET_ROOT</c>, or the
    /// <c>dotnet</c> on <c>PATH</c>, when their folder has SDKs; otherwise the installers' folder with the newest SDK.
    /// A <c>dotnet</c> whose real folder has no <c>sdk/</c>, such as Homebrew's wrapper script, doesn't count: the build
    /// host's MSBuildLocator can't use it. Null when no folder has an SDK.
    /// </summary>
    /// <remarks>
    /// M49: a root with SDKs but without the runtime the IDE's hosts need (<see cref="HasHostRuntime"/>) gives way to an
    /// installers' folder that has both: <c>/usr/local/share/dotnet</c> with only .NET 10 loses to a <c>~/.dotnet</c> with
    /// the .NET 11 release candidate the hosts ask for. When no root has the runtime, the choice is as before, and Check
    /// Environment offers it.
    /// </remarks>
    public string? FindSdkRoot()
    {
        var exe = platform == PlatformKind.Windows ? "dotnet.exe" : "dotnet";
        string? current = null;
        if (environment("DOTNET_ROOT") is { Length: > 0 } root && HasSdks(root))
        {
            current = root;
        }
        else if (FindOnPath("dotnet") is { } onPath && System.IO.Path.GetDirectoryName(RealPath(onPath)) is { } folder && HasSdks(folder))
        {
            current = folder;
        }

        if (current is not null && HasHostRuntime(current))
        {
            return current;
        }

        // The folder whose newest SDK is the newest: a global.json is likelier to find what it asks for there.
        var candidates = InstallerFolders().Where(c => _exists(System.IO.Path.Combine(c, exe)) && HasSdks(c)).OrderByDescending(NewestSdk).ToList();
        return candidates.FirstOrDefault(HasHostRuntime) ?? current ?? candidates.FirstOrDefault();
    }

    /// <summary>Whether a .NET root has at least one SDK (<c>sdk/&lt;version&gt;</c>).</summary>
    public Func<string, IReadOnlyList<string>> SdkVersions { get; init; } = folder =>
    {
        var sdk = System.IO.Path.Combine(folder, "sdk");
        return Directory.Exists(sdk) ? [.. Directory.EnumerateDirectories(sdk).Select(System.IO.Path.GetFileName).OfType<string>().Where(n => n.Length > 0 && char.IsDigit(n[0]))] : [];
    };

    private bool HasSdks(string folder) => SdkVersions(folder).Count > 0;

    private Version NewestSdk(string folder) =>
        SdkVersions(folder).Select(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0)).DefaultIfEmpty(new Version(0, 0)).Max()!;

    /// <summary>A path with its links resolved (Homebrew links <c>/opt/homebrew/bin/dotnet</c> into its Cellar).</summary>
    public Func<string, string> RealPath { get; init; } = path =>
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    };

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

    /// <summary>The default <see cref="ProcessRunner"/>: runs a program for at most 30 seconds, its output and errors together.</summary>
    public static Task<(int ExitCode, string Output)> RunProcessAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken) =>
        RunProcessAsync(program, arguments, directory, TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Runs a program to its end within <paramref name="limit"/> (installs that download take minutes).</summary>
    public static async Task<(int ExitCode, string Output)> RunProcessAsync(string program, IReadOnlyList<string> arguments, string? directory, TimeSpan limit, CancellationToken cancellationToken)
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
        timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{program} didn't answer within {limit.TotalSeconds:0} seconds.");
        }

        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
