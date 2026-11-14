using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60b's headless mode (ADR 0099): <c>codalus mcp --folder</c> starts an IDE with no window on a trusted folder and
/// serves the IDE's tools on stdio, actions only under <c>ai.agent.ideActions: allow</c>, and ends when the agent's
/// pipe closes; an untrusted folder is refused. The IDE runs as the agent starts it, <c>dotnet codalus.dll</c>, with
/// the tests' hosts.
/// </summary>
public sealed class HeadlessMcpTests
{
    private static string ShellDll => Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus.dll");

    [Fact]
    public void The_command_line_takes_a_folder_with_mcp_only()
    {
        var folder = Path.GetTempPath();
        var options = ShellOptions.Parse(["mcp", "--folder", folder, "--user-data-dir", "data"]);
        Assert.True(options.Headless);
        Assert.Equal(Path.GetFullPath(folder), options.McpFolder);
        Assert.Empty(options.Errors);
        Assert.False(ShellOptions.Parse(["mcp"]).Headless);
        Assert.Contains(ShellOptions.Parse(["--folder", folder]).Errors, e => e.Contains("goes with mcp", StringComparison.Ordinal));
        Assert.Contains("--folder <path>", ShellOptions.HelpText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_trusted_folder_is_served_without_a_window_and_the_ide_ends_with_the_agents_pipe()
    {
        await using var f = ShellFixture.Create(); // the hosts' variables, which the headless IDE inherits
        var folder = f.CopySample("BuildAndRun");
        var userData = UserData(f, "allow", folder);
        await using var ide = Headless.Start(folder, userData);
        await ide.InitializeAsync();

        // The same tools as a window's, the desktop app's too.
        var tools = (await ide.RequestAsync("tools/list", "{}")).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("get_solution", tools);
        Assert.Contains("desktop_app_input", tools);
        Assert.Contains("run_command", tools);

        // A read: the folder's solution, once it has loaded.
        var solution = "";
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMinutes(3) && !solution.Contains("- Hello", StringComparison.Ordinal))
        {
            solution = (await ide.CallAsync("get_solution", "{}")).Text;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Contains("BuildAndRun.slnx", solution, StringComparison.Ordinal);
        Assert.Contains("Trusted: yes", solution, StringComparison.Ordinal);
        Assert.Contains("- Greetings", solution, StringComparison.Ordinal);
        Assert.Contains("- Hello", solution, StringComparison.Ordinal);

        // An action, allowed by the user's setting: it runs without asking anyone.
        var ran = await ide.CallAsync("run_command", """{"id":"workbench.view.errorList"}""");
        Assert.False(ran.IsError, ran.Text + ide.Errors);

        // The agent's pipe closes: the IDE shuts down and its process ends.
        var exit = await ide.CloseInputAsync(TimeSpan.FromSeconds(60));
        Assert.True(exit == 0, $"exit code {exit}\n{ide.Errors}");
    }

    [AvaloniaFact]
    public async Task Without_allow_an_action_is_refused_with_the_reason_and_an_untrusted_folder_isnt_opened()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");

        // An untrusted folder: refused at once, with what to do.
        var untrusted = UserData(f, "allow", trusted: null);
        await using (var refused = Headless.Start(folder, untrusted))
        {
            var code = await refused.WaitForExitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(2, code);
            Assert.Contains("isn't a trusted folder", refused.Errors, StringComparison.Ordinal);
            Assert.Contains("Trust Folder", refused.Errors, StringComparison.Ordinal);
        }

        // Trusted, with actions to be asked about: nobody is there, so an action is refused, saying why; a read runs.
        var asking = UserData(f, "ask", folder);
        await using var ide = Headless.Start(folder, asking);
        await ide.InitializeAsync();
        var action = await ide.CallAsync("run_command", """{"id":"workbench.view.errorList"}""");
        Assert.True(action.IsError, action.Text);
        Assert.Contains("ai.agent.ideActions is \"allow\", and it is \"ask\"", action.Text, StringComparison.Ordinal);
        var read = await ide.CallAsync("get_diagnostics", "{}");
        Assert.False(read.IsError, read.Text);
        Assert.Equal(0, await ide.CloseInputAsync(TimeSpan.FromSeconds(60)));
    }

    /// <summary>A user-data folder for the headless IDE: its settings, and the folder it trusts (none: nothing trusted).</summary>
    private static string UserData(ShellFixture f, string ideActions, string? trusted)
    {
        var userData = Path.Combine(f.UserData, "headless-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, userData);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        File.WriteAllText(paths.SettingsFile, $$"""{ "run.console": "outputWindow", "debug.console": "internalConsole", "ai.agent.ideActions": "{{ideActions}}" }""");
        if (trusted is not null)
        {
            Directory.CreateDirectory(paths.State);
            new TrustStore(Path.Combine(paths.State, "trusted-folders.json")).Trust(trusted);
        }

        return userData;
    }

    /// <summary><c>dotnet codalus.dll mcp --folder</c> with its standard input and output, as an agent runs it.</summary>
    private sealed class Headless : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _errors = new();
        private int _nextId;

        private Headless(Process process)
        {
            _process = process;
            process.ErrorDataReceived += (_, e) =>
            {
                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                }
            };
            process.BeginErrorReadLine();
        }

        public string Errors
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToString();
                }
            }
        }

        public static Headless Start(string folder, string userData)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in new[] { ShellDll, "mcp", "--folder", folder, "--user-data-dir", userData })
            {
                start.ArgumentList.Add(argument);
            }

            return new Headless(Process.Start(start)!);
        }

        public async Task InitializeAsync() =>
            await RequestAsync("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");

        public async Task<(string Text, bool IsError)> CallAsync(string tool, string arguments)
        {
            var result = await RequestAsync("tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""");
            return (result.GetProperty("content")[0].GetProperty("text").GetString() ?? "", result.TryGetProperty("isError", out var e) && e.GetBoolean());
        }

        /// <summary>A request and its answer's result (notifications in between are skipped).</summary>
        public async Task<JsonElement> RequestAsync(string method, string parameters)
        {
            var id = ++_nextId;
            await _process.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new InvalidOperationException($"The headless IDE ended (exit code {(_process.HasExited ? _process.ExitCode : -1)}):\n{Errors}");
                using var message = JsonDocument.Parse(line); // anything but JSON-RPC on stdout fails here
                if (message.RootElement.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                {
                    return message.RootElement.TryGetProperty("result", out var result)
                        ? result.Clone()
                        : throw new InvalidOperationException("Error answer: " + line);
                }
            }
        }

        /// <summary>Closes the agent's end of standard input; the IDE's exit code once it has ended.</summary>
        public async Task<int> CloseInputAsync(TimeSpan limit)
        {
            _process.StandardInput.Close();
            return await WaitForExitAsync(limit);
        }

        public async Task<int> WaitForExitAsync(TimeSpan limit)
        {
            using var timeout = new CancellationTokenSource(limit);
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The headless IDE didn't end within {limit.TotalSeconds:0} s:\n{Errors}");
            }

            return _process.ExitCode;
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                ProcessTree.Kill(_process);
            }

            _process.WaitForExit();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
