using System.Text;
using DaedalusStudio.Platform.Pty;
using DaedalusStudio.Terminal;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Real pseudo-terminals (ConPTY, forkpty) with the VT engine on top (ADR 0014).</summary>
public sealed class PseudoTerminalTests
{
    private static Dictionary<string, string> Environment() =>
        System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return true;
    }

    private static (PseudoTerminal Pty, VirtualTerminal Terminal, StringBuilder Raw) Start(string file, string[] args, int columns = 80, int rows = 24)
    {
        var terminal = new VirtualTerminal(columns, rows);
        var raw = new StringBuilder();
        var pty = PseudoTerminal.Start(new PtyStartInfo(file, args, Path.GetTempPath(), Environment(), columns, rows));
        pty.Output += data =>
        {
            lock (raw)
            {
                raw.Append(Encoding.UTF8.GetString(data.Span));
            }

            terminal.Feed(data.Span);
        };
        terminal.Reply += pty.Write; // ConPTY asks for the cursor position at start
        return (pty, terminal, raw);
    }

    [Fact]
    public async Task A_command_runs_and_reports_its_exit_code()
    {
        Assert.True(PseudoTerminal.IsSupported);
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo hello pty && exit 3" })
            : ("/bin/sh", new[] { "-c", "echo hello pty; exit 3" });
        var (pty, terminal, raw) = Start(file, args);
        using (pty)
        {
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.Exited += code => exited.TrySetResult(code);
            if (pty.ExitCode is { } already)
            {
                exited.TrySetResult(already);
            }

            Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Contains("hello pty", StringComparison.Ordinal)), "Output: " + raw);
            Assert.True(pty.ProcessId > 0);
        }
    }

    [Fact]
    public async Task An_interactive_shell_echoes_input_and_resizes()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", Array.Empty<string>())
            : ("/bin/sh", ["-i"]);
        var (pty, terminal, raw) = Start(file, args, 100, 30);
        using (pty)
        {
            // A computed value proves the shell ran the command (not just echoed the typed text).
            pty.Write(OperatingSystem.IsWindows() ? "set /a 40+2\r" : "echo $((40+2))\r");
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Split('\n').Any(l => l.Trim() == "42" || l.TrimEnd().EndsWith("42", StringComparison.Ordinal) && !l.Contains("40+2", StringComparison.Ordinal))), "Output: " + raw);

            pty.Resize(60, 20);
            if (!OperatingSystem.IsWindows())
            {
                pty.Write("stty size\r");
                Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Contains("20 60", StringComparison.Ordinal)), "Output: " + raw);
            }

            pty.Write(OperatingSystem.IsWindows() ? "exit\r" : "exit\r");
            var exited = await WaitUntilAsync(() => pty.ExitCode is not null, 30);
            Assert.True(exited, "The shell did not exit. Output: " + raw);
        }
    }

    [Fact]
    public async Task Killing_ends_the_program()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/k" })
            : ("/bin/sh", new[] { "-c", "sleep 60" });
        var (pty, _, _) = Start(file, args);
        using (pty)
        {
            pty.Kill();
            Assert.True(await WaitUntilAsync(() => pty.ExitCode is not null, 30));
        }
    }
}
