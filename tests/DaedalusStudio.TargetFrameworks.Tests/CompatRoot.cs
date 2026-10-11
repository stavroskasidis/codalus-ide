using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// The dotnet root of the target framework matrix (M33, ADR 0030), from <c>eng/get-runtimes.sh</c>:
/// <c>DAEDALUS_DOTNET_COMPAT</c>, else <c>.work/dotnet-compat</c> in the repository. The script writes a stamp
/// (<c>.daedalus-compat</c>) listing what it installed. Without a root the tests skip; when
/// <c>DAEDALUS_DOTNET_COMPAT</c> is set (CI's matrix run), a missing or incomplete root fails them.
/// </summary>
internal static class CompatRoot
{
    public const string EnvironmentVariable = "DAEDALUS_DOTNET_COMPAT";

    private static readonly string s_repo = typeof(CompatRoot).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusRepoRoot").Value!;

    /// <summary>The root, when it has the stamp; null otherwise.</summary>
    public static string? Path { get; } = Find();

    /// <summary>The stamp's lists: <c>sdk</c>, <c>runtimes</c>, <c>aspnetcore</c>, <c>sdks</c>.</summary>
    public static IReadOnlyDictionary<string, string[]> Stamp { get; } = ReadStamp();

    /// <summary>Whether the root must be there (<c>DAEDALUS_DOTNET_COMPAT</c> is set).</summary>
    public static bool Required => Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 };

    public static string Dotnet => System.IO.Path.Combine(Path!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    /// <summary>The older SDKs installed for the global.json tests (<c>sdks</c> in the stamp).</summary>
    public static IReadOnlyList<string> OlderSdks => Stamp.GetValueOrDefault("sdks") ?? [];

    /// <summary>Skips without the root, except when it is required.</summary>
    public static void Require()
    {
        Assert.SkipWhen(Path is null && !Required, "The target framework matrix is not installed: run eng/get-runtimes.sh (ADR 0030).");
        Assert.True(Path is not null, $"{EnvironmentVariable} is set, but {Environment.GetEnvironmentVariable(EnvironmentVariable)} has no .daedalus-compat stamp: run eng/get-runtimes.sh into it.");
    }

    /// <summary>The root of a runtime: <c>&lt;root&gt;/x64</c> for .NET 5 and earlier on Apple silicon.</summary>
    public static string RootOf(Version runtime) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 && runtime.Major < 6 ? System.IO.Path.Combine(Path!, "x64") : Path!;

    /// <summary>
    /// Points this test process's environment at the root before any test runs: the hosts, the programs they build and
    /// the debuggees all start from it, as on a machine where it is the only .NET.
    /// </summary>
#pragma warning disable CA2255 // A test executable, not a library: the environment must be set before anything starts.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (Path is not { } root)
        {
            return;
        }

        Environment.SetEnvironmentVariable("PATH", root + System.IO.Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("DOTNET_ROOT", root);

        // `dotnet test` starts this test apphost with DOTNET_ROOT_<ARCH> pointing at its own dotnet, which apphosts of
        // .NET 6 and later read before DOTNET_ROOT: they would run on that dotnet instead.
        Environment.SetEnvironmentVariable("DOTNET_ROOT_" + RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant(), root);
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Dotnet);
        Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        if (OperatingSystem.IsLinux())
        {
            // dotnet watch (the net6.0 Hot Reload test) polls instead of taking an inotify instance per folder: on
            // Linux they are limited to 128 for all of the user's processes, and this suite runs beside the others.
            Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        }

        if (OperatingSystem.IsLinux() && File.Exists(System.IO.Path.Combine(root, "openssl1.1", "libssl.so.1.1")))
        {
            // OpenSSL 1.1 for .NET Core 2.1 to .NET 5 on a distribution with only OpenSSL 3 (eng/get-runtimes.sh), the
            // way the user guide tells users to provide it.
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", System.IO.Path.Combine(root, "openssl1.1")
                + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") is { Length: > 0 } existing ? ":" + existing : string.Empty));
        }

        if (Directory.Exists(System.IO.Path.Combine(root, "x64")))
        {
            // VSTest looks for the x64 dotnet here when it runs an x64 test host on Arm64.
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", System.IO.Path.Combine(root, "x64"));
        }
    }

    private static string? Find()
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(s_repo, ".work", "dotnet-compat");
        return File.Exists(System.IO.Path.Combine(root, ".daedalus-compat")) ? System.IO.Path.GetFullPath(root) : null;
    }

    private static Dictionary<string, string[]> ReadStamp()
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (Path is null)
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(System.IO.Path.Combine(Path, ".daedalus-compat")))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                result[line[..equals].Trim()] = line[(equals + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        return result;
    }
}
