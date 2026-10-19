using System.Runtime.InteropServices;
using System.Text.Json;

namespace Codalus.Rpc;

/// <summary>
/// The frameworks a <c>runtimeconfig.json</c> asks for, with the roll-forward settings the .NET host would apply, in its
/// order of precedence (<c>runtime_config.cpp</c>): the muxer's <c>--roll-forward</c>, then <c>DOTNET_ROLL_FORWARD</c>,
/// then the framework's own <c>rollForward</c>, then <c>runtimeOptions.rollForward</c>, else Minor;
/// <c>DOTNET_ROLL_FORWARD_TO_PRERELEASE=1</c> drops a release reference's preference for releases.
/// </summary>
public sealed record RuntimeConfig(string Path, IReadOnlyList<FrameworkReference> Frameworks)
{
    public const string RollForwardVariable = "DOTNET_ROLL_FORWARD";
    public const string RollForwardToPrereleaseVariable = "DOTNET_ROLL_FORWARD_TO_PRERELEASE";

    /// <summary>A self-contained app's file names no framework.</summary>
    public bool IsFrameworkDependent => Frameworks.Count > 0;

    /// <summary>A <c>rollForward</c> value by its name, ignoring case, as the host reads it.</summary>
    public static bool TryParsePolicy(string? text, out RollForwardPolicy policy)
    {
        foreach (var value in Enum.GetValues<RollForwardPolicy>())
        {
            if (string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                policy = value;
                return true;
            }
        }

        policy = default;
        return false;
    }

    /// <summary>
    /// Reads <paramref name="path"/>; null when it can't be read, or holds what the host itself would refuse (an unknown
    /// <c>rollForward</c>, a version that isn't one): then only starting the app tells.
    /// </summary>
    /// <param name="overrideFxVersion">The muxer's <c>--fx-version</c>: the first framework at exactly that version.</param>
    public static RuntimeConfig? Read(string path, Func<string, string?> environment, RollForwardPolicy? overrideRollForward = null, string? overrideFxVersion = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object)
            {
                return new RuntimeConfig(path, []);
            }

            RollForwardPolicy? fromOptions = null;
            if (options.TryGetProperty("rollForward", out var optionsRoll))
            {
                if (!TryParsePolicy(optionsRoll.ValueKind == JsonValueKind.String ? optionsRoll.GetString() : null, out var policy))
                {
                    return null;
                }

                fromOptions = policy;
            }

            var hasFramework = options.TryGetProperty("framework", out var framework) && framework.ValueKind == JsonValueKind.Object;
            var hasFrameworks = options.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Array;
            var overrideRoll = overrideRollForward;
            if ((hasFramework || hasFrameworks) && overrideRoll is null && environment(RollForwardVariable) is { Length: > 0 } fromEnvironment)
            {
                if (!TryParsePolicy(fromEnvironment, out var policy))
                {
                    return null;
                }

                overrideRoll = policy;
            }

            var toPrerelease = environment(RollForwardToPrereleaseVariable) is { Length: > 0 } flag && int.TryParse(flag.Trim(), out var value) && value == 1;
            var references = new List<FrameworkReference>();
            var entries = new List<JsonElement>();
            if (hasFramework)
            {
                entries.Add(framework);
            }

            if (hasFrameworks)
            {
                entries.AddRange(frameworks.EnumerateArray());
            }

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } fxName
                    || !entry.TryGetProperty("version", out var version) || !FrameworkVersion.TryParse(version.GetString(), out var fxVersion))
                {
                    return null;
                }

                var policy = RollForwardPolicy.Minor;
                if (overrideRoll is { } forced)
                {
                    policy = forced;
                }
                else if (entry.TryGetProperty("rollForward", out var own))
                {
                    if (!TryParsePolicy(own.ValueKind == JsonValueKind.String ? own.GetString() : null, out policy))
                    {
                        return null;
                    }
                }
                else if (fromOptions is { } fromRuntimeOptions)
                {
                    policy = fromRuntimeOptions;
                }

                references.Add(FrameworkReference.Create(fxName, fxVersion, policy, toPrerelease));
            }

            if (overrideFxVersion is not null && references.Count > 0)
            {
                if (!FrameworkVersion.TryParse(overrideFxVersion, out var exact))
                {
                    return null;
                }

                references[0] = references[0] with { Version = exact, RollForward = RollForwardPolicy.Disable };
            }

            return new RuntimeConfig(path, references);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Where the .NET host looks for the shared frameworks of a framework-dependent app (hostfxr's rules):
/// <list type="bullet">
/// <item><c>dotnet app.dll</c> (the muxer): the folder of the <c>dotnet</c> that runs, its links resolved.</item>
/// <item>An app host (<c>codalus-pluginhost</c>): <c>DOTNET_ROOT_&lt;ARCH&gt;</c>, <c>DOTNET_ROOT(x86)</c> for a 32-bit
/// Windows process, <c>DOTNET_ROOT</c>; then the registered location (Windows' registry,
/// <c>/etc/dotnet/install_location_&lt;arch&gt;</c> or <c>/etc/dotnet/install_location</c>); then the default one
/// (<c>%ProgramFiles%\dotnet</c>, <c>/usr/local/share/dotnet</c>, <c>/usr/share/dotnet</c>).</item>
/// </list>
/// Every input is a property, so tests describe any machine.
/// </summary>
public sealed class DotnetRootResolver
{
    public static DotnetRootResolver Default { get; } = new();

    /// <summary><c>windows</c>, <c>macos</c> or <c>linux</c>.</summary>
    public string OS { get; init; } = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public Architecture Architecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    /// <summary>The OS's architecture: an x64 process on Arm64 (emulated) looks in the default location's <c>x64</c> folder.</summary>
    public Architecture OSArchitecture { get; init; } = RuntimeInformation.OSArchitecture;

    /// <summary>This process's environment: where the launching process looks for <c>dotnet</c> on <c>PATH</c>.</summary>
    public Func<string, string?> ProcessEnvironment { get; init; } = Environment.GetEnvironmentVariable;

    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;

    /// <summary>A file's first line, or null (the install location files).</summary>
    public Func<string, string?> ReadFirstLine { get; init; } = path =>
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    /// <summary>Windows: <c>HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\&lt;arch&gt;\InstallLocation</c> (32-bit view).</summary>
    public Func<string, string?> WindowsRegisteredLocation { get; init; } = RegisteredLocation;

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

    /// <summary>The variable an app host of this architecture reads before <c>DOTNET_ROOT</c>: <c>DOTNET_ROOT_ARM64</c>.</summary>
    public string ArchVariable => "DOTNET_ROOT_" + ArchName.ToUpperInvariant();

    /// <summary>An x64 process on Arm64 (the win-x64 build on Windows on Arm): it can't use the native .NET's hostfxr.</summary>
    public bool EmulatedX64 => Architecture == Architecture.X64 && OSArchitecture == Architecture.Arm64;

    /// <summary>The OS's native .NET location (<c>%ProgramFiles%\dotnet</c>, <c>/usr/local/share/dotnet</c>), which an emulated x64 process can't use.</summary>
    public string NativeLocation => OS switch
    {
        "windows" => Path.Combine((Architecture == Architecture.X86 ? ProcessEnvironment("ProgramFiles(x86)") : null) ?? ProcessEnvironment("ProgramFiles") ?? @"C:\Program Files", "dotnet"),
        "macos" => "/usr/local/share/dotnet",
        _ => "/usr/share/dotnet",
    };

    private string ArchName => Architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => Architecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The root an app host uses, given the environment it starts with. A variable whose folder doesn't exist is passed
    /// over, as hostfxr does (it takes a variable's full path only when it resolves).
    /// </summary>
    public string? ForAppHost(Func<string, string?> environment)
    {
        if (environment(ArchVariable) is { Length: > 0 } byArch && DirectoryExists(byArch))
        {
            return byArch;
        }

        if (OS == "windows" && Architecture == Architecture.X86 && environment("DOTNET_ROOT(x86)") is { Length: > 0 } x86 && DirectoryExists(x86))
        {
            return x86;
        }

        if (environment("DOTNET_ROOT") is { Length: > 0 } root && DirectoryExists(root))
        {
            return root;
        }

        if (OS == "windows")
        {
            if (WindowsRegisteredLocation(ArchName) is { Length: > 0 } registered)
            {
                return registered;
            }

            return DefaultLocation;
        }

        if ((ReadFirstLine("/etc/dotnet/install_location_" + ArchName) ?? ReadFirstLine("/etc/dotnet/install_location")) is { Length: > 0 } location)
        {
            return location;
        }

        return DefaultLocation;
    }

    /// <summary>
    /// Where .NET's installer puts this architecture's .NET: <c>%ProgramFiles%\dotnet</c>, <c>/usr/local/share/dotnet</c>
    /// or <c>/usr/share/dotnet</c>; its <c>x64</c> folder for an x64 process on Arm64 (Windows and macOS).
    /// </summary>
    public string DefaultLocation
    {
        get
        {
            if (OS == "windows")
            {
                var programFiles = (Architecture == Architecture.X86 ? ProcessEnvironment("ProgramFiles(x86)") : null) ?? ProcessEnvironment("ProgramFiles") ?? @"C:\Program Files";
                return EmulatedX64 ? Path.Combine(programFiles, "dotnet", "x64") : Path.Combine(programFiles, "dotnet");
            }

            return OS == "macos" ? EmulatedX64 ? "/usr/local/share/dotnet/x64" : "/usr/local/share/dotnet" : "/usr/share/dotnet";
        }
    }

    /// <summary>
    /// The root of the muxer that <paramref name="fileName"/> (<c>dotnet</c> or a path to it) starts; null when it isn't
    /// found, or when its folder isn't a .NET root (no <c>host/fxr</c>, which the muxer needs): Homebrew's wrapper script
    /// and snap's launcher run the real muxer from elsewhere, which can't be told from here.
    /// </summary>
    public string? ForMuxer(string fileName)
    {
        var path = fileName;
        if (!Path.IsPathRooted(path))
        {
            var exe = OS == "windows" && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path + ".exe" : path;
            path = (ProcessEnvironment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder.Trim('"'), exe)).FirstOrDefault(FileExists) ?? string.Empty;
        }

        return path.Length > 0 && FileExists(path) && Path.GetDirectoryName(RealPath(path)) is { } folder && DirectoryExists(Path.Combine(folder, "host", "fxr"))
            ? folder
            : null;
    }

    private static string? RegisteredLocation(string arch)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var key = hive.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}");
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>A framework reference and the version that would run it under a root (null: none).</summary>
public sealed record FrameworkResolution(FrameworkReference Reference, FrameworkVersion? Resolved, IReadOnlyList<FrameworkVersion> Installed);

/// <summary>Whether a framework-dependent program's frameworks are installed under the .NET root it would use.</summary>
public sealed record HostRuntimeCheckResult(string Program, string RuntimeConfigPath, string DotnetRoot, IReadOnlyList<FrameworkResolution> Frameworks)
{
    public bool IsSatisfied => Frameworks.All(f => f.Resolved is not null);

    public IEnumerable<FrameworkResolution> Missing => Frameworks.Where(f => f.Resolved is null);

    /// <summary>"codalus-pluginhost needs Microsoft.NETCore.App 11.0.0-rc.2… (rollForward Major), which isn't installed in /usr/local/share/dotnet (found: 8.0.20, 10.0.0–10.0.9)."</summary>
    public string Describe() =>
        string.Join(" ", Missing.Select(m =>
            $"{Path.GetFileNameWithoutExtension(Program)} needs the .NET runtime {m.Reference}, which isn't installed in {DotnetRoot} (found: {(m.Installed.Count == 0 ? "none" : FrameworkResolver.Describe(m.Installed))})."));
}

/// <summary>
/// The check before a framework-dependent host starts (M49, ADR 0080): its <c>runtimeconfig.json</c> against the shared
/// frameworks under the .NET root it would use, by the host's own rules, so that a missing runtime is named instead of
/// a process that exits at once. Programs it can't reason about (no runtimeconfig.json, a self-contained app, a root that
/// doesn't exist, a setting the host would refuse) are not checked: starting them tells.
/// </summary>
public static class HostRuntimeCheck
{
    /// <summary><c>CODALUS_SKIP_RUNTIME_CHECK=1</c> turns the check off (a machine whose .NET it misjudges).</summary>
    public const string SkipVariable = "CODALUS_SKIP_RUNTIME_CHECK";

    private static readonly HashSet<string> s_muxerOptionsWithValue = new(StringComparer.Ordinal)
    {
        "--runtimeconfig", "--depsfile", "--additionalprobingpath", "--additional-deps", "--fx-version", "--roll-forward",
    };

    /// <summary>The check for a launch on this machine; null when it doesn't apply or can't tell.</summary>
    public static HostRuntimeCheckResult? Check(ProcessLaunchInfo launch, DotnetRootResolver? roots = null)
    {
        roots ??= DotnetRootResolver.Default;
        if (roots.ProcessEnvironment(SkipVariable) == "1")
        {
            return null;
        }

        string? Env(string name) => launch.Environment is { } overrides && overrides.TryGetValue(name, out var value) ? value : roots.ProcessEnvironment(name);
        string program, configPath, root;
        RollForwardPolicy? rollForward = null;
        string? fxVersion = null;
        if (string.Equals(Path.GetFileNameWithoutExtension(launch.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet [exec] [host options] app.dll [arguments]; anything else (an SDK command) isn't an app.
            string? runtimeConfig = null;
            var i = launch.Arguments.Count > 0 && launch.Arguments[0] == "exec" ? 1 : 0;
            for (; i < launch.Arguments.Count && launch.Arguments[i].StartsWith("--", StringComparison.Ordinal); i += 2)
            {
                var option = launch.Arguments[i];
                if (!s_muxerOptionsWithValue.Contains(option) || i + 1 >= launch.Arguments.Count)
                {
                    return null;
                }

                var value = launch.Arguments[i + 1];
                switch (option)
                {
                    case "--runtimeconfig":
                        runtimeConfig = value;
                        break;
                    case "--fx-version":
                        fxVersion = value;
                        break;
                    case "--roll-forward" when RuntimeConfig.TryParsePolicy(value, out var policy):
                        rollForward = policy;
                        break;
                    case "--roll-forward":
                        return null;
                }
            }

            if (i >= launch.Arguments.Count || !launch.Arguments[i].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            program = launch.Arguments[i];
            configPath = runtimeConfig ?? Path.ChangeExtension(program, ".runtimeconfig.json");
            if (roots.ForMuxer(launch.FileName) is not { } muxerRoot)
            {
                return null;
            }

            root = muxerRoot;
        }
        else
        {
            program = launch.FileName;
            if (!Path.IsPathRooted(program))
            {
                return null;
            }

            var folder = Path.GetDirectoryName(program)!;
            var name = program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(program) : Path.GetFileName(program);
            configPath = Path.Combine(folder, name + ".runtimeconfig.json");
            string hostfxr = roots.OS switch { "windows" => "hostfxr.dll", "macos" => "libhostfxr.dylib", _ => "libhostfxr.so" };
            if (roots.FileExists(Path.Combine(folder, hostfxr)) || roots.ForAppHost(Env) is not { } appHostRoot)
            {
                return null; // self-contained: its runtime is next to it
            }

            root = appHostRoot;
        }

        if (!roots.FileExists(configPath) || !roots.DirectoryExists(root))
        {
            return null;
        }

        return RuntimeConfig.Read(configPath, Env, rollForward, fxVersion) is { IsFrameworkDependent: true } config
            ? Check(program, config, root, Env)
            : null;
    }

    /// <summary>The frameworks of <paramref name="config"/> against the ones installed under <paramref name="dotnetRoot"/>.</summary>
    public static HostRuntimeCheckResult Check(string program, RuntimeConfig config, string dotnetRoot, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var resolutions = config.Frameworks.Select(reference =>
        {
            var installed = InstalledVersions(dotnetRoot, reference.Name, environment);
            return new FrameworkResolution(reference, FrameworkResolver.Resolve(reference, installed), installed);
        }).ToList();
        return new HostRuntimeCheckResult(program, config.Path, dotnetRoot, resolutions);
    }

    /// <summary>
    /// The versions of a framework under a root, as the host counts them: folders <c>shared/&lt;name&gt;/&lt;version&gt;</c>
    /// with a <c>&lt;name&gt;.deps.json</c>, minus <c>DOTNET_DISABLE_RUNTIME_VERSIONS</c>.
    /// </summary>
    public static IReadOnlyList<FrameworkVersion> InstalledVersions(string dotnetRoot, string framework, Func<string, string?>? environment = null)
    {
        var folder = Path.Combine(dotnetRoot, "shared", framework);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var disabled = ((environment ?? Environment.GetEnvironmentVariable)("DOTNET_DISABLE_RUNTIME_VERSIONS") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        try
        {
            return [.. Directory.EnumerateDirectories(folder)
                .Where(d => !disabled.Contains(Path.GetFileName(d)) && File.Exists(Path.Combine(d, framework + ".deps.json")))
                .Select(d => FrameworkVersion.TryParse(Path.GetFileName(d), out var version) ? version : null)
                .OfType<FrameworkVersion>()
                .Order()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// A host that wasn't started because the .NET runtime it needs isn't installed (M49): what it needs, under which root,
/// and what's there. A <see cref="JsonRpcConnectionLostException"/>, so code that handles a lost host handles this too.
/// </summary>
public sealed class HostRuntimeMissingException(HostRuntimeCheckResult result) : JsonRpcConnectionLostException(result.Describe(), null)
{
    public HostRuntimeCheckResult Result { get; } = result;
}

/// <summary>
/// A host process that ended (M49): its exit code and the lines it wrote to standard error (the first ones when it
/// ended during startup, the last ones otherwise), which say why
/// ("You must install or update .NET…"), in place of "The connection was lost".
/// </summary>
public sealed class HostExitedException(string message, int? exitCode, IReadOnlyList<string> errorOutput, bool duringStartup) : JsonRpcConnectionLostException(message, null)
{
    public int? ExitCode { get; } = exitCode;

    /// <summary>The host's standard error: its first lines for an exit during startup, its last ones for a crash.</summary>
    public IReadOnlyList<string> ErrorOutput { get; } = errorOutput;

    /// <summary>It ended before it was ready (before its handshake finished): starting it again would end the same way.</summary>
    public bool DuringStartup { get; } = duringStartup;
}
