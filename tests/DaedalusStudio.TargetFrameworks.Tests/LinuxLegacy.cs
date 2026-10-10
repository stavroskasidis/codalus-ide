namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// What the .NET Core 2.x runtime needs on Linux that a current distribution doesn't have (M33, ADR 0030): OpenSSL 1.0
/// or 1.1 (.NET Core 3.1 and .NET 5 also load OpenSSL 3; 2.x doesn't). Without it the .NET SDK 2.x can't reach nuget.org.
/// ICU is handled by Daedalus itself (<c>CLR_ICU_VERSION_OVERRIDE</c>). Containers of the distributions .NET Core 2.x
/// supported cover it instead: <c>eng/compat-containers.sh</c>.
/// </summary>
internal static class LinuxLegacy
{
    private static readonly Lazy<bool> s_openSsl1 = new(() =>
        new[] { "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib" }
            .Any(folder => File.Exists(Path.Combine(folder, "libssl.so.1.1")) || File.Exists(Path.Combine(folder, "libssl.so.1.0.0")) || File.Exists(Path.Combine(folder, "libssl.so.1.0.2"))));

    /// <summary>Why the SDK <paramref name="sdk"/> can't restore on this machine, or null when it can.</summary>
    public static string? Missing(Version sdk) =>
        OperatingSystem.IsLinux() && sdk.Major < 3 && !s_openSsl1.Value
            ? $"The .NET SDK {sdk} runs on .NET Core 2.x, which needs OpenSSL 1.0 or 1.1 to reach nuget.org, and this distribution has only OpenSSL 3. eng/compat-containers.sh covers it in a distribution of its time (ADR 0030)."
            : null;
}
