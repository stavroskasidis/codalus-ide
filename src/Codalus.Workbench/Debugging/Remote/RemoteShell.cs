using System.Diagnostics;
using System.Globalization;
using System.Text;
using Codalus.Debugging;

namespace Codalus.Workbench.Debugging.Remote;

/// <summary>The result of a script on a target: its exit code, stdout and stderr.</summary>
public sealed record RemoteResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A process on a target (M14c): <see cref="IsDotnet"/> when it has the .NET runtime loaded.</summary>
public sealed record RemoteProcess(int Id, string Name, string CommandLine, bool IsDotnet);

/// <summary>Runs scripts on a <see cref="RemoteTarget"/>: lists its processes, and starts netcoredbg there.</summary>
public static class RemoteShell
{
    /// <summary>
    /// Variables for every command (ssh, docker, wsl.exe): <c>WSL_UTF8</c> makes <c>wsl.exe</c>'s own messages UTF-8.
    /// The shell adds <c>SSH_ASKPASS</c> for SSH passwords.
    /// </summary>
    public static Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { ["WSL_UTF8"] = "1" };

    /// <summary>Runs <paramref name="script"/> on <paramref name="target"/>, writing <paramref name="input"/> to its stdin.</summary>
    public static async Task<RemoteResult> RunAsync(RemoteTarget target, string script, CancellationToken cancellationToken, Stream? input = null)
    {
        var (program, arguments) = target.Shell(script);
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (name, value) in Environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (ex is IOException)
            {
                // stdin closed early: the command failed before reading it; say what it printed.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return new RemoteResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }

            throw;
        }
    }

    /// <summary>
    /// The script that lists processes: <c>/proc</c> on Linux (no <c>ps</c> needed, as in slim containers), with the
    /// .NET ones told apart by <c>libcoreclr</c> in their memory maps; <c>ps</c> elsewhere (macOS).
    /// </summary>
    public const string ListScript = """
        # codalus-ps: the shells running this script leave themselves out by this mark
        if [ -d /proc/self ]; then
          for d in /proc/[0-9]*; do
            p=${d#/proc/}
            [ "$p" = "$$" ] && continue
            c=$(tr '\000' ' ' < "$d/cmdline" 2>/dev/null) || continue
            [ -n "$c" ] || continue
            n=$(cat "$d/comm" 2>/dev/null)
            if grep -q libcoreclr "$d/maps" 2>/dev/null; then k=1; else k=0; fi
            printf '%s\t%s\t%s\t%s\n' "$p" "$k" "$n" "$c"
          done
        else
          ps -axo pid=,comm=,args= | while read -r p n c; do
            case "$c" in *dotnet*|*.dll*) k=1 ;; *) k=0 ;; esac
            printf '%s\t%s\t%s\t%s\n' "$p" "$k" "${n##*/}" "$c"
          done
        fi
        """;

    /// <summary>The target's processes, the .NET ones first.</summary>
    public static async Task<IReadOnlyList<RemoteProcess>> ProcessesAsync(RemoteTarget target, CancellationToken cancellationToken)
    {
        var result = await RunAsync(target, ListScript, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && result.Output.Length == 0)
        {
            throw new InvalidOperationException($"Couldn't list the processes of {target.Display}: {Trim(result.Error)}");
        }

        return ParseProcesses(result.Output);
    }

    public static IReadOnlyList<RemoteProcess> ParseProcesses(string listing)
    {
        var processes = new List<RemoteProcess>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t', 4);
            if (fields.Length == 4 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                var commandLine = fields[3].Trim();
                if (commandLine.Contains("codalus-ps", StringComparison.Ordinal))
                {
                    continue;
                }

                processes.Add(new RemoteProcess(pid, fields[2].Trim(), commandLine, fields[1] == "1"));
            }
        }

        return [.. processes.OrderByDescending(p => p.IsDotnet).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }

    /// <summary>
    /// Reattach's process: the one attached to last if it still runs, else a .NET process with its command line (the
    /// program started again). Never one by name alone: a framework-dependent program is "dotnet", like any other.
    /// </summary>
    public static RemoteProcess? FindAgain(IReadOnlyList<RemoteProcess> processes, RemoteProcess last) =>
        processes.FirstOrDefault(p => p.Id == last.Id && p.CommandLine == last.CommandLine)
        ?? processes.FirstOrDefault(p => p.IsDotnet && p.CommandLine == last.CommandLine);

    /// <summary>
    /// The adapter's pipe transport: netcoredbg at <paramref name="debuggerPath"/> on the target, speaking DAP on its
    /// stdin and stdout.
    /// </summary>
    public static DebugAdapterStartInfo Adapter(RemoteTarget target, string debuggerPath)
    {
        var (program, arguments) = target.Shell("exec " + Build.WslLaunch.ShellQuote(debuggerPath) + " --interpreter=vscode");
        return new DebugAdapterStartInfo(program, arguments, null, new Dictionary<string, string>(Environment), target.Display);
    }

    internal static string Trim(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "it printed nothing" : trimmed.Length > 600 ? trimmed[..600] + "…" : trimmed;
    }
}
