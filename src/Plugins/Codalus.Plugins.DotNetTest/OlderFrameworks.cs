using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Plugins.DotNetTest;

/// <summary>Target frameworks that target Windows (M40a) or are .NET Framework's (M43a): their test hosts run only there.</summary>
internal static class WindowsFrameworks
{
    /// <summary><c>net8.0-windows</c>, <c>net10.0-windows10.0.19041.0</c>.</summary>
    public static bool IsWindows(string targetFramework) =>
        targetFramework.IndexOf('-', StringComparison.Ordinal) is var dash and > 0 && targetFramework[(dash + 1)..].StartsWith("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>.NET Framework's monikers: <c>net48</c>, <c>net472</c> (no dot, unlike <c>net8.0</c>).</summary>
    public static bool IsNetFramework(string targetFramework) =>
        targetFramework.Length is >= 5 and <= 6 && targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && targetFramework[3..].All(char.IsAsciiDigit) && targetFramework[3] is >= '1' and <= '4';

    /// <summary>
    /// Whether a project with these frameworks can't run its tests here: all of them target Windows or .NET Framework
    /// (whose test hosts run on the desktop CLR; Mono is a later decision), and this isn't Windows.
    /// </summary>
    public static bool OnlyWindows(IReadOnlyList<string> targetFrameworks, bool windows) =>
        !windows && targetFrameworks.Count > 0 && targetFrameworks.All(f => IsWindows(f) || IsNetFramework(f));

    /// <summary>Why a project's tests run only on Windows, for its row and a run's output.</summary>
    public static string Reason(string project, IReadOnlyList<string> targetFrameworks) =>
        targetFrameworks.All(IsNetFramework)
            ? $"{project} targets .NET Framework ({string.Join(", ", targetFrameworks)}): its tests run only on Windows."
            : $"{project} targets Windows ({string.Join(", ", targetFrameworks)}): its tests run only on Windows.";
}

/// <summary>
/// What the test hosts of older frameworks need (M33, ADR 0030), as the shell's runs and debug sessions get it:
/// <list type="bullet">
/// <item>On an Arm64 Mac, .NET 5 and earlier have no Arm64 runtime: their test hosts run as x64 (VSTest's
/// <c>TargetPlatform</c>), unless <c>testing.architecture</c> says otherwise.</item>
/// <item>On Linux, .NET Core 2.x to 3.1 don't find a newer distribution's ICU by themselves: they get
/// <c>CLR_ICU_VERSION_OVERRIDE</c> with the system's ICU, unless it or invariant globalization is set already.</item>
/// </list>
/// </summary>
internal static class OlderFrameworks
{
    public static RunOptions Adjust(RunOptions options, string? targetFramework) =>
        DebugNetFramework(
            Adjust(options, targetFramework, RuntimeInformation.OSArchitecture, OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, SystemIcuMajor),
            targetFramework,
            OperatingSystem.IsWindows(),
            RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Debugging .NET Framework tests (M43b, ADR 0076): the desktop-CLR debugger debugs processes of its own bitness, and
    /// VSTest may pick the 32-bit .NET Framework test host for an AnyCPU assembly, so the test host runs as the IDE's
    /// architecture (x64, or ARM64), unless <c>testing.architecture</c> chose one.
    /// </summary>
    public static RunOptions DebugNetFramework(RunOptions options, string? targetFramework, bool windows, Architecture debugger) =>
        windows && options.Mode == RunMode.Debug && string.IsNullOrEmpty(options.Architecture) && targetFramework is not null && WindowsFrameworks.IsNetFramework(targetFramework)
            ? options with { Architecture = debugger switch { Architecture.Arm64 => "ARM64", Architecture.X86 => "x86", _ => "x64" } }
            : options;

    public static RunOptions Adjust(RunOptions options, string? targetFramework, Architecture machine, bool macOS, bool linux, Func<string, string?> environment, Func<int?> icuMajor)
    {
        if (NetCoreVersion(targetFramework) is not { } version)
        {
            return options;
        }

        if (macOS && machine == Architecture.Arm64 && version.Major < 6 && string.IsNullOrEmpty(options.Architecture))
        {
            options = options with { Architecture = "x64" };
        }

        if (linux && version.Major < 5 && !IsSet("CLR_ICU_VERSION_OVERRIDE") && !IsSet("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") && icuMajor() is { } icu)
        {
            options = options with
            {
                Environment = new Dictionary<string, string>(options.Environment, StringComparer.Ordinal) { ["CLR_ICU_VERSION_OVERRIDE"] = icu.ToString(CultureInfo.InvariantCulture) },
            };
        }

        return options;

        bool IsSet(string name) => options.Environment.ContainsKey(name) || !string.IsNullOrEmpty(environment(name));
    }

    /// <summary><c>netcoreapp2.1</c> → 2.1, <c>net8.0</c> → 8.0; null for .NET Standard, .NET Framework and the unknown.</summary>
    internal static Version? NetCoreVersion(string? targetFramework)
    {
        var tfm = (targetFramework ?? string.Empty).Trim().ToLowerInvariant();
        if (tfm.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
        {
            tfm = tfm[..dash];
        }

        var number = tfm.StartsWith("netcoreapp", StringComparison.Ordinal) ? tfm["netcoreapp".Length..]
            : tfm.StartsWith("net", StringComparison.Ordinal) && !tfm.StartsWith("netstandard", StringComparison.Ordinal) && tfm.Contains('.', StringComparison.Ordinal) ? tfm["net".Length..]
            : null;
        return number is not null && Version.TryParse(number, out var version) && (tfm.StartsWith("netcoreapp", StringComparison.Ordinal) || version.Major >= 5) ? version : null;
    }

    private static int? SystemIcuMajor()
    {
        int? newest = null;
        foreach (var folder in new[] { "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/aarch64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib" })
        {
            try
            {
                foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "libicuuc.so.?*") : [])
                {
                    if (Path.GetFileName(file).Length > "libicuuc.so.".Length && int.TryParse(Path.GetFileName(file)["libicuuc.so.".Length..].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > (newest ?? 0))
                    {
                        newest = major;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest;
    }
}
