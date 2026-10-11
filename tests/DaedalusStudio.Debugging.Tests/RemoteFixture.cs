using System.Diagnostics;
using DaedalusStudio.Workbench.Debugging.Remote;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>
/// Local stand-ins for the machines remote debugging reaches (M14c): never another machine.
/// <list type="bullet">
/// <item>WSL: a distribution of this Windows machine (<c>DAEDALUS_TEST_WSL</c>, default <c>Ubuntu</c>) with .NET in it.</item>
/// <item>SSH: on Linux and macOS a fake <c>ssh</c> that runs the command here; on Windows the distribution's shell
/// through a fake <c>ssh</c> too (WSL as the "remote" Linux machine).</item>
/// <item>Containers: Podman (or Docker) with a local Ubuntu image, the .NET of the machine running it mounted in.</item>
/// </list>
/// Each is null when it isn't available, and its tests skip.
/// </summary>
public static class RemoteFixture
{
    private static readonly Lazy<string?> s_wsl = new(FindWsl);

    /// <summary>The WSL distribution with .NET (Windows), or null.</summary>
    public static string? WslDistribution => s_wsl.Value;

    private static string? FindWsl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var distribution = Environment.GetEnvironmentVariable("DAEDALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
        try
        {
            var listed = RemoteTargets.WslDistributionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!listed.Contains(distribution, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var dotnet = Run("wsl.exe", ["-d", distribution, "--exec", "sh", "-lc", "command -v dotnet"]);
            return dotnet.Code == 0 && dotnet.Output.Trim().Length > 0 ? distribution : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly Lazy<(IReadOnlyList<string> Tool, string DotnetRoot)?> s_containers = new(FindContainers);

    /// <summary>The image the container tests run (it must be there already: tests never pull).</summary>
    public const string Image = "docker.io/library/ubuntu:24.04";

    /// <summary>
    /// Podman with <see cref="Image"/>: here on Linux, in the WSL distribution on Windows; and the .NET folder (on the
    /// machine running the containers) to mount into one. Null when there is none.
    /// </summary>
    public static (IReadOnlyList<string> Tool, string DotnetRoot)? Containers => s_containers.Value;

    private static (IReadOnlyList<string> Tool, string DotnetRoot)? FindContainers()
    {
        IReadOnlyList<string> tool;
        IReadOnlyList<string> shell;
        if (OperatingSystem.IsWindows())
        {
            if (WslDistribution is not { } distribution)
            {
                return null;
            }

            tool = ["wsl.exe", "-d", distribution, "--exec", "podman"];
            shell = ["wsl.exe", "-d", distribution, "--exec", "sh", "-lc"];
        }
        else if (OperatingSystem.IsLinux())
        {
            tool = ["podman"];
            shell = ["sh", "-lc"];
        }
        else
        {
            return null;
        }

        try
        {
            if (Run(tool[0], [.. tool.Skip(1), "image", "exists", Image]).Code != 0)
            {
                return null;
            }

            // A Microsoft build of .NET (self-contained enough for a bare image): a distribution's own package
            // needs its libraries (libunwind…), which the image doesn't have.
            var root = Run(shell[0], [.. shell.Skip(1), "if [ -x \"$HOME/.dotnet/dotnet\" ]; then echo \"$HOME/.dotnet\"; elif [ -n \"$DOTNET_ROOT\" ]; then echo \"$DOTNET_ROOT\"; else dirname \"$(readlink -f \"$(command -v dotnet)\")\"; fi"]).Output.Trim();
            return root.StartsWith('/') ? (tool, root) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The distribution's <c>dotnet</c> (resolved by its login shell).</summary>
    public static string WslDotnet(string distribution) =>
        Run("wsl.exe", ["-d", distribution, "--exec", "sh", "-lc", "readlink -f \"$(command -v dotnet)\""]).Output.Trim();

    public static (int Code, string Output) Run(string program, IReadOnlyList<string> arguments, int seconds = 60)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["WSL_UTF8"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(seconds)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, output.Result + error.Result);
    }

    /// <summary>
    /// A fake <c>ssh</c> for the SSH tests: it ignores the options and the destination and runs the remote command
    /// line with <c>sh -c</c>, here (Linux, macOS) or in the WSL distribution (Windows). It is a script on Unix and
    /// a <c>.cmd</c> on Windows; null when neither applies.
    /// </summary>
    public static string? FakeSsh(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            var script = Path.Combine(folder, "fake-ssh");
            File.WriteAllText(script, """
                #!/bin/sh
                # A stand-in for ssh: skip the options and the destination, run the command here.
                while [ $# -gt 0 ]; do
                  case "$1" in
                    -T) shift ;;
                    -o|-p|-i|-l) shift 2 ;;
                    *) break ;;
                  esac
                done
                shift
                exec sh -c "$*"
                """.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }

        return null;
    }
}
