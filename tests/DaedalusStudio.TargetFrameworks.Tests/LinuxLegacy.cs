namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// What .NET Core 2.x to .NET 5 need on Linux that a current distribution doesn't have (M33, ADR 0030): OpenSSL 1.0 or
/// 1.1, for cryptography and HTTPS (.NET 6 added OpenSSL 3). Without it the SDKs of those versions can't reach nuget.org
/// to restore. ICU is handled by Daedalus itself (<c>CLR_ICU_VERSION_OVERRIDE</c>). Containers of the distributions
/// they supported cover them instead: <c>eng/compat-containers.sh</c>.
/// </summary>
internal static class LinuxLegacy
{
    private static readonly Lazy<bool> s_openSsl1 = new(() =>
        new[] { "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib" }
            .Any(folder => File.Exists(Path.Combine(folder, "libssl.so.1.1")) || File.Exists(Path.Combine(folder, "libssl.so.1.0.0")) || File.Exists(Path.Combine(folder, "libssl.so.1.0.2"))));

    /// <summary>Why the SDK <paramref name="sdk"/> can't restore on this machine, or null when it can.</summary>
    public static string? Missing(Version sdk) =>
        OperatingSystem.IsLinux() && sdk.Major < 6 && !s_openSsl1.Value
            ? $"The .NET SDK {sdk} runs on .NET {sdk.Major}.{sdk.Minor}, which needs OpenSSL 1.0 or 1.1 to reach nuget.org, and this distribution has only OpenSSL 3. eng/compat-containers.sh covers its runtime in a distribution of its time (ADR 0030)."
            : null;
}
