using System.Diagnostics;

namespace Codalus.Tests;

/// <summary>
/// The WSL distribution the remote tests stand in for another machine with (Windows; <c>CODALUS_TEST_WSL</c>, default
/// <c>Ubuntu</c>): usable only with the .NET runtime the tests' programs target on the <c>PATH</c> that programs started
/// there get (<c>wsl --exec sh -c</c>, as the product starts them). A distribution with an older .NET only made these
/// tests fail ("You must install or update .NET to run this application") instead of skipping.
/// </summary>
internal static class WslStandIn
{
    private static readonly Lazy<string?> s_distribution = new(Probe);

    public static string Name { get; } = Environment.GetEnvironmentVariable("CODALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";

    /// <summary>The distribution, or null when it's missing or lacks the runtime (see <see cref="SkipReason"/>).</summary>
    public static string? Distribution => s_distribution.Value;

    public static string SkipReason =>
        $"No WSL distribution '{Name}' with the .NET {Environment.Version.Major} runtime on its PATH (CODALUS_TEST_WSL): the remote stand-ins run .NET {Environment.Version.Major} programs there.";

    private static string? Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo("wsl.exe", ["-d", Name, "--exec", "sh", "-c", "dotnet --list-runtimes"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["WSL_UTF8"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && output.Result.Contains($"Microsoft.NETCore.App {Environment.Version.Major}.", StringComparison.Ordinal) ? Name : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
