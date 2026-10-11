using System.Diagnostics;
using System.Text;

namespace DaedalusStudio.Workbench.Debugging.Remote;

/// <summary>A running container: its id, name and image.</summary>
public sealed record ContainerInfo(string Id, string Name, string Image);

/// <summary>Finds what can be debugged on (M14c): WSL distributions and running containers.</summary>
public static class RemoteTargets
{
    /// <summary>The setting with the container CLI's command line: <c>docker</c>, <c>podman</c>, or <c>wsl -d Ubuntu --exec podman</c>.</summary>
    public const string ContainerToolSetting = "debug.containerTool";

    /// <summary>The setting with the remote-to-local source folders (an object, the target's folder to this machine's).</summary>
    public const string SourceFileMapSetting = "debug.sourceFileMap";

    /// <summary>The container CLI of the setting's value (split like a command line); <c>docker</c> when empty.</summary>
    public static IReadOnlyList<string> ContainerTool(string? setting) =>
        Debugging.CommandLine.Split(setting ?? string.Empty) is { Count: > 0 } tool ? tool : ["docker"];

    /// <summary>The WSL distributions (Windows only), the default first.</summary>
    public static async Task<IReadOnlyList<string>> WslDistributionsAsync(CancellationToken cancellationToken, string program = "wsl.exe")
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var (code, output) = await RunAsync(program, ["--list", "--quiet"], cancellationToken).ConfigureAwait(false);
        return code != 0 ? [] : [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Trim('\0')).Where(l => l.Length > 0)];
    }

    /// <summary>The running containers of <paramref name="tool"/> (<c>docker ps</c>).</summary>
    public static async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(IReadOnlyList<string> tool, CancellationToken cancellationToken)
    {
        var (code, output) = await RunAsync(tool[0], [.. tool.Skip(1), "ps", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}"], cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"{string.Join(' ', tool)} ps failed: {RemoteShell.Trim(output)}");
        }

        return ParseContainers(output);
    }

    public static IReadOnlyList<ContainerInfo> ParseContainers(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(f => f.Length >= 3 && f[0].Length > 0)
            .Select(f => new ContainerInfo(f[0], f[1], f[2]))];

    private static async Task<(int Code, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (name, value) in RemoteShell.Environment)
        {
            start.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{program} isn't installed or isn't on the PATH: {ex.Message}", ex);
        }
    }
}
