using System.Globalization;
using System.Runtime.InteropServices;

namespace DaedalusStudio.Plugins.DotNetTest;

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
        Adjust(options, targetFramework, RuntimeInformation.OSArchitecture, OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, SystemIcuMajor);

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
                foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "libicuuc.so.*") : [])
                {
                    if (int.TryParse(Path.GetFileName(file)["libicuuc.so.".Length..].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > (newest ?? 0))
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
