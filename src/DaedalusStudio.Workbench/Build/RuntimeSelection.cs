using System.Runtime.InteropServices;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// Which <c>dotnet</c> a program starts on (M33, ADR 0030). The SDK's <c>RunCommand</c> is either the <c>dotnet</c> that
/// built the project (<c>dotnet exec app.dll</c>: .NET Core 2.x, and projects without an apphost) or the project's
/// apphost, which finds the runtime through <c>DOTNET_ROOT</c>.
/// <list type="bullet">
/// <item>An apphost gets the SDK's root in <c>DOTNET_ROOT</c> (<c>DOTNET_ROOT_&lt;ARCH&gt;</c> from .NET 6), as
/// <c>dotnet run</c> does, unless the variable is already set. A private SDK (<c>~/.dotnet</c>) then runs its own runtimes.</item>
/// <item>On an Arm64 machine, frameworks without an Arm64 runtime (macOS before .NET 6, Windows before .NET 5) run as x64,
/// under Rosetta 2 or the Windows emulator: <c>dotnet</c> becomes the x64 one, and an apphost gets the x64 root.</item>
/// <item>On Linux, .NET Core 2.x to 3.1 look for the ICU versions that existed when they shipped, and stop at start
/// on a distribution with a newer one ("Couldn't find a valid ICU package"). They get <c>CLR_ICU_VERSION_OVERRIDE</c>
/// with the system's ICU, unless the launch sets it or invariant globalization already.</item>
/// </list>
/// Each change is explained in <see cref="LaunchPlan.Notes"/>.
/// </summary>
public static class RuntimeSelection
{
    /// <summary>Adjusts <paramref name="plan"/> for the program of <paramref name="info"/> on this machine.</summary>
    public static LaunchPlan Apply(LaunchPlan plan, RunInfo info, string? dotnetRoot) =>
        Apply(plan, info, dotnetRoot, new Machine(RuntimeInformation.OSArchitecture, DotnetFrameworks.CurrentOs, File.Exists, ReadFile, Environment.GetEnvironmentVariable, SystemIcuMajor));

    /// <summary>What <see cref="Apply(LaunchPlan, RunInfo, string?)"/> asks the machine (tests replace it).</summary>
    public sealed record Machine(
        Architecture Architecture,
        OSPlatform Os,
        Func<string, bool> FileExists,
        Func<string, string?> ReadFile,
        Func<string, string?> GetEnvironmentVariable,
        Func<int?>? IcuMajor = null);

    public const string IcuOverride = "CLR_ICU_VERSION_OVERRIDE";
    public const string Invariant = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";

    /// <summary>
    /// The environment that lets .NET Core 2.x to 3.1 start on a Linux distribution whose ICU is newer than the ones
    /// they know: <c>CLR_ICU_VERSION_OVERRIDE</c> with the system's ICU. Empty elsewhere, for newer runtimes, when the
    /// launch already decides (the override or invariant globalization is set), or without ICU.
    /// </summary>
    public static IReadOnlyDictionary<string, string> IcuEnvironment(string? targetFramework, OSPlatform os, Func<string, string?> getEnvironmentVariable, Func<int?> icuMajor, IReadOnlyDictionary<string, string>? planEnvironment = null)
    {
        if (os != OSPlatform.Linux || DotnetFrameworks.NetCoreVersion(targetFramework) is not { Major: < 5 }
            || IsSet(IcuOverride) || IsSet(Invariant) || icuMajor() is not { } icu)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string> { [IcuOverride] = icu.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        bool IsSet(string name) => planEnvironment?.ContainsKey(name) == true || !string.IsNullOrEmpty(getEnvironmentVariable(name));
    }

    /// <summary>The newest ICU in the system's library folders (<c>libicuuc.so.74</c> → 74), or null.</summary>
    public static int? SystemIcuMajor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        int? newest = null;
        foreach (var folder in new[] { "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/aarch64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib" })
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "libicuuc.so.*"))
                {
                    // The pattern also matches the unversioned libicuuc.so.
                    var name = Path.GetFileName(file);
                    if (name.Length <= "libicuuc.so.".Length)
                    {
                        continue;
                    }

                    var major = name["libicuuc.so.".Length..].Split('.')[0];
                    if (int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) && version > (newest ?? 0))
                    {
                        newest = version;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest;
    }

    public static LaunchPlan Apply(LaunchPlan plan, RunInfo info, string? dotnetRoot, Machine machine)
    {
        var isProjectCommand = info.RunCommand is { } command && string.Equals(command, plan.FileName, StringComparison.Ordinal);
        if (!isProjectCommand)
        {
            // An Executable profile: its program is the user's choice.
            return plan;
        }

        var exe = machine.Os == OSPlatform.Windows ? "dotnet.exe" : "dotnet";
        var isMuxer = string.Equals(Path.GetFileName(plan.FileName), exe, StringComparison.OrdinalIgnoreCase) || plan.FileName == "dotnet";
        var version = DotnetFrameworks.NetCoreVersion(info.TargetFramework);
        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        var notes = new List<string>(plan.Notes);
        foreach (var (name, value) in IcuEnvironment(info.TargetFramework, machine.Os, machine.GetEnvironmentVariable, machine.IcuMajor ?? (() => null), plan.Environment))
        {
            environment[name] = value;
            notes.Add($"{name}={value}: .NET Core {version?.ToString(2)} doesn't look for this distribution's ICU {value} by itself. Set {name} or {Invariant} in the launch profile to choose otherwise.");
        }

        plan = plan with { Environment = environment, Notes = notes };
        if (DotnetFrameworks.RunsAsX64(info.TargetFramework, machine.Architecture, machine.Os))
        {
            var x64 = X64Root(dotnetRoot, machine) ?? throw new InvalidOperationException(MissingX64Runtime(info, machine.Os));
            notes.Add($"{info.TargetFramework} has no Arm64 runtime: the program runs as x64 on {x64}.");
            if (isMuxer)
            {
                return plan with { FileName = Path.Combine(x64, exe), RunsAsX64 = true, Notes = notes };
            }

            // The apphost of .NET 5 and earlier reads only DOTNET_ROOT; later ones DOTNET_ROOT_X64 first.
            environment["DOTNET_ROOT"] = x64;
            environment["DOTNET_ROOT_X64"] = x64;
            return plan with { Environment = environment, RunsAsX64 = true, Notes = notes };
        }

        if (isMuxer || dotnetRoot is null || version is null)
        {
            return plan;
        }

        var variable = RootVariable(version, machine.Architecture);
        if (!environment.ContainsKey(variable) && string.IsNullOrEmpty(machine.GetEnvironmentVariable(variable)))
        {
            environment[variable] = dotnetRoot;
        }

        return plan with { Environment = environment };
    }

    /// <summary>The variable an apphost of <paramref name="version"/> reads first: <c>DOTNET_ROOT_X64</c> and the like from .NET 6.</summary>
    internal static string RootVariable(Version version, Architecture architecture) =>
        version.Major >= 6
            ? "DOTNET_ROOT_" + architecture.ToString().ToUpperInvariant()
            : architecture == Architecture.X86 ? "DOTNET_ROOT(x86)" : "DOTNET_ROOT";

    /// <summary>
    /// The x64 <c>dotnet</c> root next to the native one: <c>&lt;root&gt;/x64</c> (where the installers put it), then the
    /// registered location (<c>/etc/dotnet/install_location_x64</c> on macOS), then the default one. Null when there is none.
    /// </summary>
    public static string? X64Root(string? nativeRoot, Machine machine)
    {
        var exe = machine.Os == OSPlatform.Windows ? "dotnet.exe" : "dotnet";
        var candidates = new List<string>();
        if (nativeRoot is not null)
        {
            candidates.Add(Path.Combine(nativeRoot, "x64"));
        }

        if (machine.Os == OSPlatform.OSX)
        {
            if (machine.ReadFile("/etc/dotnet/install_location_x64") is { } registered && registered.Trim() is { Length: > 0 } location)
            {
                candidates.Add(location);
            }

            candidates.Add("/usr/local/share/dotnet/x64");
        }
        else if (machine.Os == OSPlatform.Windows && machine.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles)
        {
            candidates.Add(Path.Combine(programFiles, "dotnet", "x64"));
        }

        return candidates.FirstOrDefault(c => machine.FileExists(Path.Combine(c, exe)));
    }

    private static string MissingX64Runtime(RunInfo info, OSPlatform os)
    {
        var name = Path.GetFileNameWithoutExtension(info.Path);
        var version = DotnetFrameworks.NetCoreVersion(info.TargetFramework);
        var where = os == OSPlatform.OSX ? "/usr/local/share/dotnet/x64, and runs under Rosetta 2" : @"C:\Program Files\dotnet\x64";
        return $"{name} targets {info.TargetFramework}, which has no Arm64 runtime: it runs as an x64 program. Install the x64 .NET {version?.ToString(2)} runtime (it goes into {where}).";
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
