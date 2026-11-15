using System.Diagnostics;
using System.Text;
using Codalus.Platform;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// A run of one of the platform SDKs' command-line tools (ADR 0104): <c>sdkmanager</c>, <c>avdmanager</c>,
/// <c>adb logcat</c>, <c>xcrun simctl</c>. Its <see cref="Environment"/> is added to this process's (<c>ANDROID_HOME</c>,
/// <c>JAVA_HOME</c>), and <see cref="Input"/>, when set, is written to its standard input and then closed: the answers
/// to its prompts (<c>sdkmanager</c>'s licenses).
/// </summary>
public sealed record ToolCommand(string Program, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Input { get; init; }

    /// <summary>The command line as the user would type it (for confirmations and the output channel).</summary>
    public string Display => string.Join(' ', new[] { Program }.Concat(Arguments).Select(Quote));

    private static string Quote(string argument) => argument.Length == 0 || argument.Any(c => c is ' ' or ';' or '"' or '&' or '|') ? "\"" + argument + "\"" : argument;
}

/// <summary>
/// Runs a <see cref="ToolCommand"/> to its end, each line of its output (standard output and error, as they come) to
/// <c>onLine</c>; its exit code. Cancelling ends it (and its tree). Tests use a stand-in that replays recorded output.
/// </summary>
public delegate Task<int> ToolProcessRunner(ToolCommand command, Action<string>? onLine, CancellationToken cancellationToken);

/// <summary>The real <see cref="ToolProcessRunner"/>.</summary>
public static class ToolProcess
{
    public static async Task<int> RunAsync(ToolCommand command, Action<string>? onLine, CancellationToken cancellationToken)
    {
        await Codalus.Rpc.ProcessEnvironment.WhenReadyAsync(cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(command.Program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.Input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (command.WorkingDirectory is { } directory)
        {
            start.WorkingDirectory = directory;
        }

        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        ToolTelemetry.OptOut(start.Environment);
        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = await Task.Run(() => Process.Start(start), cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"{command.Program} didn't start.");
        var gate = new object();
        void Line(string? line)
        {
            if (line is not null && onLine is not null)
            {
                lock (gate)
                {
                    onLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (command.Input is { } input)
        {
            try
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It ended before reading its answers; its exit code says how.
            }
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process);
            throw;
        }

        return process.ExitCode;
    }

    /// <summary>Runs <paramref name="command"/> and returns its exit code and its whole output.</summary>
    public static async Task<(int ExitCode, string Output)> CaptureAsync(this ToolProcessRunner run, ToolCommand command, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var code = await run(command, line => output.Append(line).Append('\n'), cancellationToken).ConfigureAwait(false);
        return (code, output.ToString());
    }
}
