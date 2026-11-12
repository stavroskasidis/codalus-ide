using System.Text;
using Codalus.Platform.Pty;
using Codalus.Terminal;

namespace Codalus.Workbench.Tests;

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

    // The tests below start a program with no shell in between, as AI Console starts an agent's CLI and Run
    // Without Debugging a console app: the program leads the terminal's session, and the pty has to be that session's
    // controlling terminal for the kernel to signal it. macOS made it nobody's (the report: resizing AI Console left
    // the agent's CLI drawing at its first size; ADR 0014's amendment). `sleep` and `cat` are the proof there: bash,
    // macOS's `sh`, takes its terminal by itself when it starts, so a test whose program is `sh` passed without the fix.

    [Fact]
    public async Task A_program_started_by_itself_is_told_of_a_resize()
    {
        // Unix: SIGWINCH goes to the terminal's foreground process group; the trap runs between two sleeps.
        // Windows: ConPTY has no signal for it; the program's console is the new size the next time it looks.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "$w = [Console]::WindowWidth; Write-Host ready; while ([Console]::WindowWidth -eq $w) { Start-Sleep -Milliseconds 50 }; Write-Host ('resized: ' + [Console]::WindowHeight + ' ' + [Console]::WindowWidth)" })
            : ("/bin/sh", new[] { "-c", "trap 'echo resized: $(stty size)' WINCH; echo ready; while :; do sleep 0.1; done" });
        var (pty, terminal, raw) = Start(file, args, 100, 30);
        using (pty)
        {
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Contains("ready", StringComparison.Ordinal)), "Output: " + raw);
            pty.Resize(60, 20);
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Contains("resized: 20 60", StringComparison.Ordinal)), "Output: " + raw);
            pty.Kill();
            Assert.True(await WaitUntilAsync(() => pty.ExitCode is not null, 30));
        }
    }

    [Fact]
    public async Task Ctrl_C_interrupts_a_program_started_by_itself()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix terminals turn Ctrl+C into SIGINT for the foreground process group.");

        var (pty, _, raw) = Start("sleep", ["60"]);
        using (pty)
        {
            // Typed until it ends: a Ctrl+C from before the program had the terminal is nobody's.
            var interrupted = await WaitUntilAsync(() =>
            {
                pty.Write("\x03");
                return pty.ExitCode is not null;
            });
            Assert.True(interrupted, "sleep ran on after Ctrl+C. Output: " + raw);
            Assert.Equal(130, pty.ExitCode); // 128 + SIGINT
        }
    }

    [Fact]
    public async Task A_program_started_by_itself_has_the_terminal_as_its_controlling_terminal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/dev/tty is Unix's name for a process's controlling terminal.");

        // Without a controlling terminal /dev/tty can't be opened, and cat ends at once with an error.
        var (pty, terminal, raw) = Start("cat", ["/dev/tty"]);
        using (pty)
        {
            pty.Write("hello tty\r");

            // The line twice: the terminal's echo of what was typed, then cat's copy of the line it read.
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Split('\n').Count(l => l.Trim() == "hello tty") == 2), $"Exit code {pty.ExitCode}. Output: {raw}");
            pty.Write("\x04"); // Ctrl+D: the end of the input
            Assert.True(await WaitUntilAsync(() => pty.ExitCode is not null, 30), "cat did not end. Output: " + raw);
            Assert.Equal(0, pty.ExitCode);
        }
    }

    [Fact]
    public async Task The_shell_that_starts_a_program_on_macos_hands_it_its_arguments_as_they_are()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no /bin/sh.");
            return;
        }

        // The command line macOS programs start with, on every Unix (so dash runs it too, and bash twice on macOS):
        // the program's arguments are the shell's own, so spaces, quotes and $ in them reach the program untouched,
        // the program is the same process (its exit code is the terminal's), and its terminal is still there.
        string[] program = ["/bin/sh", "-c", "printf '<%s>' \"$@\"; test -t 0 && echo ' on a terminal'; exit 7", "sh", "two words", "$HOME", "it's", "\"quoted\"", "*", ""];
        var line = UnixPseudoTerminal.StartThroughShell("/dev/tty", program);
        Assert.Equal(UnixPseudoTerminal.Shell, line[0]);
        Assert.Equal(program, line.Skip(4));
        var (pty, terminal, raw) = Start(line[0], [.. line.Skip(1)], 120, 30);
        using (pty)
        {
            Assert.True(await WaitUntilAsync(() => pty.ExitCode is not null, 30), "The program did not end. Output: " + raw);
            Assert.Equal(7, pty.ExitCode);
            Assert.True(await WaitUntilAsync(() => terminal.ScreenText().Contains("<two words><$HOME><it's><\"quoted\"><*><> on a terminal", StringComparison.Ordinal)), "Output: " + raw);
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
