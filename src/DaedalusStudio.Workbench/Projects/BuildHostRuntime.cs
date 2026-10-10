using System.Diagnostics;

namespace DaedalusStudio.Workbench.Projects;

/// <summary>
/// Which runtime the build host starts on (ADR 0025). The hosts target the .NET that Daedalus runs on and roll forward.
/// When <c>dotnet</c> resolves the SDK of a newer .NET for the solution's folder, the build host must run on that
/// .NET to load the SDK's MSBuild: it rolls forward to the newest runtime, previews included for a preview SDK.
/// </summary>
public static class BuildHostRuntime
{
    public const string RollForward = "DOTNET_ROLL_FORWARD";
    public const string RollForwardToPrerelease = "DOTNET_ROLL_FORWARD_TO_PRERELEASE";

    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The major version of .NET the hosts target: the one the shell runs on.</summary>
    public static int HostMajor => Environment.Version.Major;

    /// <summary>
    /// The build host's environment for a solution in <paramref name="solutionDirectory"/>, or null to start it as it
    /// is. Asks <c>dotnet</c> only when an SDK newer than the hosts' .NET is installed.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> EnvironmentAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        if (DotnetExecutable() is not { } dotnet || !HasNewerSdk(Path.GetDirectoryName(dotnet)!, HostMajor))
        {
            return null;
        }

        return EnvironmentFor(await ResolveSdkAsync(dotnet, solutionDirectory, cancellationToken).ConfigureAwait(false), HostMajor);
    }

    /// <summary>The environment for an SDK version (<c>11.0.100-rc.1.26425.128</c>): null unless it is newer than <paramref name="hostMajor"/>.</summary>
    public static IReadOnlyDictionary<string, string>? EnvironmentFor(string? sdkVersion, int hostMajor)
    {
        if (Major(sdkVersion) is not { } major || major <= hostMajor)
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { [RollForward] = "LatestMajor" };
        if (sdkVersion!.Contains('-', StringComparison.Ordinal))
        {
            environment[RollForwardToPrerelease] = "1";
        }

        return environment;
    }

    /// <summary>Whether <c>&lt;root&gt;/sdk</c> has an SDK of a .NET newer than <paramref name="hostMajor"/>.</summary>
    public static bool HasNewerSdk(string dotnetRoot, int hostMajor)
    {
        try
        {
            var sdks = Path.Combine(dotnetRoot, "sdk");
            return Directory.Exists(sdks) && Directory.EnumerateDirectories(sdks).Any(d => Major(Path.GetFileName(d)) > hostMajor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The <c>dotnet</c> that starts the hosts: in <c>DOTNET_ROOT</c>, else the one on the PATH (links resolved).</summary>
    public static string? DotnetExecutable()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), exe);
            if (File.Exists(candidate))
            {
                return Platform.RealPath.Resolve(candidate);
            }
        }

        return null;
    }

    /// <summary><c>dotnet --version</c> in the folder: the SDK it resolves, <c>global.json</c> included, or null.</summary>
    private static async Task<string?> ResolveSdkAsync(string dotnet, string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(dotnet, "--version")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment.Remove(RollForward);
        start.Environment.Remove(RollForwardToPrerelease);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_probeTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var version = (await output.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 && version.Length > 0 && char.IsAsciiDigit(version[0]) ? version : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static int? Major(string? version) =>
        version is not null && int.TryParse(version.AsSpan(0, Math.Max(0, version.IndexOf('.', StringComparison.Ordinal))), out var major) ? major : null;
}
