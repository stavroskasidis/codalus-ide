namespace Codalus.TargetFrameworks.Tests;

/// <summary>
/// What .NET Core 2.x to .NET 5 need on Linux that a current distribution doesn't have (M33, ADR 0030): OpenSSL 1.x for
/// cryptography and HTTPS (.NET 6 added OpenSSL 3), so the SDKs of those versions can reach nuget.org to restore.
/// .NET Core 2.1 to .NET 5 load OpenSSL 1.1, which <c>eng/get-runtimes.sh</c> provides on such a distribution (the
/// suite puts it on <c>LD_LIBRARY_PATH</c>); .NET Core 2.0, the runtime of the 2.0 and 2.1.2xx SDKs, needs OpenSSL 1.0.
/// ICU is handled by Codalus itself (<c>CLR_ICU_VERSION_OVERRIDE</c>). <c>eng/compat-containers.sh</c> covers the
/// runtimes on the distributions of their time.
/// </summary>
internal static class LinuxLegacy
{
    private static readonly string[] s_folders = ["/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib"];

    private static bool Has(params string[] names) =>
        s_folders.Concat((Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            .Any(folder => names.Any(name => File.Exists(Path.Combine(folder, name))));

    /// <summary>
    /// Why a VSTest test host on the runtime <paramref name="framework"/> can't start on this machine, or null when it can:
    /// test hosts hash test names, which goes through OpenSSL on Linux.
    /// </summary>
    public static string? MissingForTestHost(Version framework)
    {
        if (!OperatingSystem.IsLinux() || framework.Major >= 6)
        {
            return null;
        }

        var core20 = framework < new Version(2, 1);
        var found = core20 ? Has("libssl.so.1.0.0", "libssl.so.1.0.2", "libssl.so.10") : Has("libssl.so.1.1", "libssl.so.1.0.0", "libssl.so.1.0.2");
        return found ? null : $".NET {framework}'s test host needs OpenSSL {(core20 ? "1.0" : "1.0 or 1.1")}, which this distribution doesn't have.";
    }

    /// <summary>Why the SDK <paramref name="sdk"/> can't restore on this machine, or null when it can.</summary>
    public static string? Missing(Version sdk)
    {
        if (!OperatingSystem.IsLinux() || sdk.Major >= 6)
        {
            return null;
        }

        // The SDKs before 2.1.300 run on .NET Core 2.0.
        var onCore20 = sdk < new Version(2, 1, 300);
        var found = onCore20 ? Has("libssl.so.1.0.0", "libssl.so.1.0.2", "libssl.so.10") : Has("libssl.so.1.1", "libssl.so.1.0.0", "libssl.so.1.0.2");
        return found
            ? null
            : $"The .NET SDK {sdk} runs on .NET Core {(onCore20 ? "2.0, which needs OpenSSL 1.0" : $"{sdk.Major}.{sdk.Minor}, which needs OpenSSL 1.0 or 1.1")} to reach nuget.org, and this distribution doesn't have it. eng/compat-containers.sh covers its runtime in a distribution of its time (ADR 0030).";
    }
}