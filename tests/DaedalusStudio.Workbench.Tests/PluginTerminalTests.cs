using System.Collections.Concurrent;
using DaedalusStudio.Platform;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Projects;
using DaedalusStudio.Workbench.Terminal;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The shell side of the proposed terminal API (API 0.2.3): create, output, send text, exit, kill.</summary>
public sealed class PluginTerminalTests : IDisposable
{
    private readonly SolutionService _solutions = new(() => null);
    private readonly TerminalService _terminals = new(PlatformInfo.Current, integrationScripts: null);
    private readonly WorkbenchProposedApi _api;
    private readonly List<TerminalSession> _shown = [];

    public PluginTerminalTests()
    {
        _api = new WorkbenchProposedApi(
            _solutions,
            new BuildService(_solutions, new OutputService()),
            new DebugService(() => null),
            new LanguageService(new LanguageServiceRegistry(), _solutions))
        {
            Terminals = _terminals,
            DefaultTerminalDirectory = Path.GetTempPath,
            ShowTerminal = _shown.Add,
        };
    }

    public void Dispose()
    {
        _api.Dispose();
        _terminals.Dispose();
    }

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token).Token;

    [Fact]
    public async Task A_command_runs_in_a_terminal_and_reports_its_output_and_exit_code()
    {
        var ct = Timeout();
        var output = new ConcurrentQueue<string>();
        var exited = new TaskCompletionSource<TerminalExitedParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo from a plugin && exit 4" })
            : ("/bin/sh", new[] { "-c", "echo from a plugin; exit 4" });

        var terminal = await _api.TerminalCreateAsync(
            new TerminalCreateParams("Plugin task", null, null, program, arguments, null, Show: true, ReportOutput: true),
            o => output.Enqueue(o.Text),
            e => exited.TrySetResult(e),
            ct);

        var ended = await exited.Task.WaitAsync(ct);
        Assert.Equal(terminal.TerminalId, ended.TerminalId);
        Assert.Equal(4, ended.ExitCode);
        Assert.Contains("from a plugin", string.Concat(output), StringComparison.Ordinal);
        var session = Assert.Single(_shown);
        Assert.Equal("Plugin task", session.Name);
        Assert.True(terminal.ProcessId > 0);

        // An exited terminal can't take input.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _api.TerminalSendTextAsync(terminal.TerminalId, "x", ct));
    }

    [Fact]
    public async Task Text_sent_to_a_shell_runs_and_kill_ends_it()
    {
        var ct = Timeout();
        var output = new ConcurrentQueue<string>();
        var exited = new TaskCompletionSource<TerminalExitedParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var terminal = await _api.TerminalCreateAsync(
            new TerminalCreateParams(null, profile, null, null, null, null, Show: false, ReportOutput: true),
            o => output.Enqueue(o.Text),
            e => exited.TrySetResult(e),
            ct);
        Assert.Empty(_shown);

        await _api.TerminalSendTextAsync(terminal.TerminalId, OperatingSystem.IsWindows() ? "set /a 6*7\r" : "echo $((6*7))\r", ct);

        // The output is raw: ConPTY repaints with escape sequences, which can share the result's line on a busy machine.
        static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1b(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[()][0-9A-Za-z])", string.Empty);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!Plain(string.Concat(output)).Split('\n').Any(l => l.Trim() == "42"))
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(25), "No 42 in the terminal's output: " + string.Concat(output));
            await Task.Delay(20, ct);
        }

        await _api.TerminalKillAsync(terminal.TerminalId, ct);
        var ended = await exited.Task.WaitAsync(ct);
        Assert.Equal(terminal.TerminalId, ended.TerminalId);
        Assert.Empty(_terminals.Sessions);
    }
}
