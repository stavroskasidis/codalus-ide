using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>The integrated terminal (ADR 0014) in the headless shell: a real shell in a PTY, input, split, find, kill.</summary>
public sealed class TerminalTests
{
    private static string Screen(TerminalControl control) => control.Session?.Terminal.ScreenText() ?? string.Empty;

    /// <summary>A shell profile that exists on every CI machine: cmd on Windows, sh elsewhere.</summary>
    private static string Profile() => OperatingSystem.IsWindows() ? "cmd" : "sh";

    [AvaloniaFact]
    public async Task A_terminal_runs_commands_typed_into_it()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var session = f.Shell.ShowTerminal(newTerminal: true, profile: Profile());
        Assert.NotNull(session);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TerminalView>().Any());
        var control = f.Window.GetVisualDescendants().OfType<TerminalControl>().First(c => c.Session == session);
        await ShellFixture.PumpUntil(() => control.Columns > 20 && session!.Terminal.Columns == control.Columns, seconds: 10);

        // Typing goes to the shell through the control; Enter runs the command.
        control.Focus();
        f.Type(OperatingSystem.IsWindows() ? "set /a 6*7" : "echo $((6*7))");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Screen(control).Split('\n').Any(l => l.Trim() == "42"), seconds: 20);
        Assert.Equal(TerminalViewModel.ToolWindowId, f.Shell.Workbench.Context.GetValue("focusedView"));

        // Ctrl+C reaches the program (an interrupt, not the IDE's copy).
        f.Type(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30");
        f.Press(Key.Enter);
        if (OperatingSystem.IsWindows())
        {
            // Interrupt only once ping runs: on a busy machine it can take longer than a moment to start.
            await ShellFixture.PumpUntil(() => Screen(control).Contains("127.0.0.1", StringComparison.Ordinal) && Screen(control).Split('\n').Count(l => l.Contains("127.0.0.1", StringComparison.Ordinal)) >= 2, seconds: 20);
        }
        else
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }
        f.Press(Key.C, RawInputModifiers.Control);
        f.Type(OperatingSystem.IsWindows() ? "echo after" : "echo after");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Screen(control).Split('\n').Any(l => l.Trim() == "after"), seconds: 20);

        // Find selects the match.
        var vm = f.Shell.TerminalWindow;
        vm.FindVisible = true;
        vm.FindText = "after";
        ShellFixture.Pump();
        Assert.True(control.HasSelection);
        Assert.Equal("after", control.SelectedText());

        // Split: a second terminal in the same tab; kill closes both.
        var second = vm.Split();
        Assert.NotNull(second);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TerminalControl>().Any(c => c.Session == second && c.IsVisible));
        Assert.True(vm.Selected!.IsSplit);
        vm.Kill(vm.Selected);
        await ShellFixture.PumpUntil(() => vm.Tabs.Count == 0, seconds: 20);
        Assert.Empty(f.Shell.Terminals.Sessions);
    }

    [AvaloniaFact]
    public async Task Ide_keys_still_work_from_the_terminal_and_links_open_files()
    {
        await using var f = ShellFixture.Create();
        var session = f.Shell.ShowTerminal(newTerminal: true, profile: Profile())!;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TerminalControl>().Any(c => c.Session == session));
        var control = f.Window.GetVisualDescendants().OfType<TerminalControl>().First(c => c.Session == session);
        control.Focus();

        // Ctrl+Q (the search box: Feature Search, M47) stays with the IDE.
        f.Press(Key.Q, RawInputModifiers.Control);
        Assert.True(f.Shell.CodeSearch is { IsOpen: true, IsFeatureTab: true });
        ShellFixture.Pump();
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => !f.Shell.CodeSearch.IsOpen);

        // A file:line reference printed by a program is a link to the file.
        var file = Path.Combine(f.UserData, "Linked.cs");
        await File.WriteAllTextAsync(file, "line 1\nline 2\nline 3\n", TestContext.Current.CancellationToken);
        // The shell's own start-up output can repaint the screen over a line fed too early (ConPTY on a busy CI
        // machine): wait for its prompt, and feed again if the line is gone.
        int Row() => Enumerable.Range(0, session.Terminal.TotalRows).LastOrDefault(i => session.Terminal.GetRow(i).GetText().Contains("XX0001", StringComparison.Ordinal), -1);
        await ShellFixture.PumpUntil(() => Enumerable.Range(0, session.Terminal.TotalRows).Any(i => session.Terminal.GetRow(i).GetText().Trim().Length > 0), seconds: 30);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Row() < 0 && DateTime.UtcNow < deadline)
        {
            session.Terminal.Feed($"\r\n{file}(3,2): error XX0001: boom\r\n");
            for (var wait = DateTime.UtcNow.AddSeconds(2); Row() < 0 && DateTime.UtcNow < wait;)
            {
                ShellFixture.Pump();
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        // The logical line: a long temp path (macOS) wraps over several rows.
        var row = Row();
        Assert.True(row >= 0, "the fed line stays on the screen");
        Assert.Contains(Codalus.Terminal.TerminalLinks.Find(session.Terminal.LogicalLine(row).Text), l => l.Kind == Codalus.Terminal.TerminalLinkKind.File && l.Line == 3);
        f.Shell.TerminalWindow.OpenLink(new TerminalLinkActivation(Codalus.Terminal.TerminalLinkKind.File, file, 3, 2));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == file);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.Editor.TextArea.Caret.Line == 3);
    }
}
