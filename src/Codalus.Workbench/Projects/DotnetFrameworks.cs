using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Workbench.Projects;

/// <summary>
/// What a target framework moniker says about the runtime a project needs (M33, ADR 0030): its .NET (Core) version,
/// the C# version it defaults to, whether Hot Reload works, and the processor architecture it runs as.
/// </summary>
public static class DotnetFrameworks
{
    /// <summary>
    /// The .NET (Core) version of a runnable target framework: <c>netcoreapp2.1</c> → 2.1, <c>net8.0</c> and
    /// <c>net8.0-windows</c> → 8.0. Null for .NET Standard, .NET Framework (<c>net48</c>) and anything unknown.
    /// </summary>
    public static Version? NetCoreVersion(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        var tfm = targetFramework.Trim().ToLowerInvariant();
        var dash = tfm.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            tfm = tfm[..dash];
        }

        if (tfm.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            return Parse(tfm["netcoreapp".Length..], minimumMajor: 1);
        }

        // net5.0 and later have a dot; .NET Framework's monikers (net48, net472) don't.
        return tfm.StartsWith("net", StringComparison.Ordinal) && !tfm.StartsWith("netstandard", StringComparison.Ordinal) && tfm.Contains('.', StringComparison.Ordinal)
            ? Parse(tfm["net".Length..], minimumMajor: 5)
            : null;

        static Version? Parse(string number, int minimumMajor) =>
            Version.TryParse(number, out var version) && version.Major >= minimumMajor ? new Version(version.Major, Math.Max(0, version.Minor)) : null;
    }

    /// <summary>
    /// The C# version the SDK uses for a target framework when the project doesn't set <c>LangVersion</c>: 7.3 for
    /// .NET Framework (M43a), .NET Core 2.x and .NET Standard 2.0, 8.0 for 3.x and .NET Standard 2.1, 9.0 for .NET 5,
    /// then one per release. Null for frameworks it doesn't know (the compiler's own default applies).
    /// </summary>
    public static string? DefaultCSharpVersion(string? targetFramework)
    {
        if (NetFrameworkProjects.IsMoniker(targetFramework?.Trim()))
        {
            return "7.3";
        }

        if (targetFramework?.Trim().ToLowerInvariant() is { } tfm && tfm.StartsWith("netstandard", StringComparison.Ordinal))
        {
            return Version.TryParse(tfm["netstandard".Length..], out var standard) ? standard >= new Version(2, 1) ? "8.0" : "7.3" : null;
        }

        return NetCoreVersion(targetFramework) is not { } version ? null
            : version.Major < 3 ? "7.3"
            : version.Major < 5 ? "8.0"
            : (version.Major + 4).ToString(CultureInfo.InvariantCulture) + ".0";
    }

    /// <summary>
    /// Why Hot Reload can't run a project of <paramref name="targetFramework"/>, or null when it can (or the framework
    /// isn't known). <c>dotnet watch</c> applies changes from .NET 6 on.
    /// </summary>
    public static string? HotReloadUnsupportedReason(string? targetFramework, string? projectName = null) =>
        NetCoreVersion(targetFramework) is { Major: < 6 } || NetFrameworkProjects.IsMoniker(targetFramework)
            ? $"Hot Reload needs .NET 6 or later: {(projectName is null ? "the project" : projectName)} targets {targetFramework}{(NetFrameworkProjects.IsMoniker(targetFramework) ? " (.NET Framework)" : string.Empty)}."
            : null;

    /// <summary>
    /// Whether a program of <paramref name="targetFramework"/> runs as an x64 process on an Arm64 machine: .NET has no
    /// Arm64 build for macOS before .NET 6, nor for Windows before .NET 5. Those programs run under Rosetta 2 or the
    /// Windows emulator, on the x64 runtime.
    /// </summary>
    public static bool RunsAsX64(string? targetFramework, Architecture machine, OSPlatform os)
    {
        if (machine != Architecture.Arm64 || NetCoreVersion(targetFramework) is not { } version)
        {
            return false;
        }

        return (os == OSPlatform.OSX && version.Major < 6) || (os == OSPlatform.Windows && version.Major < 5);
    }

    /// <summary><see cref="RunsAsX64(string?, Architecture, OSPlatform)"/> on this machine.</summary>
    public static bool RunsAsX64(string? targetFramework) => RunsAsX64(targetFramework, RuntimeInformation.OSArchitecture, CurrentOs);

    internal static OSPlatform CurrentOs =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
}
