using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M60b's smoke step: this app, as an agent starts it, headless (<c>codalus mcp --folder</c>) on the trusted BuildAndRun
/// solution, with a user-data folder of its own whose <c>ai.agent.ideActions</c> is <c>allow</c>. Over its stdio: the
/// tools are listed (the desktop app's among them), get_solution names the solution's projects, run_command runs an
/// action without anyone asked, and the process ends when its input closes. An untrusted folder is refused at once.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task HeadlessMcpStepAsync()
    {
        var (program, assembly) = shell.McpExecutable;
        var userData = Path.Combine(Work, "headless-user-data");
        var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, userData);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """{ "ai.agent.ideActions": "allow", "run.console": "outputWindow" }""").ConfigureAwait(true);

        // Not trusted there yet: refused, saying why.
        using (var refused = StartHeadless(program, assembly, userData))
        {
            var errors = refused.StandardError.ReadToEndAsync();
            Check(await ExitedAsync(refused, TimeSpan.FromSeconds(60)).ConfigureAwait(true) == HeadlessIde.RefusedExitCode, "an untrusted folder is refused with its exit code");
            Check((await errors.ConfigureAwait(true)).Contains("isn't a trusted folder", StringComparison.Ordinal), "the refusal says the folder isn't trusted");
        }

        Directory.CreateDirectory(paths.State);
        new TrustStore(Path.Combine(paths.State, "trusted-folders.json")).Trust(_folder);
        using var ide = StartHeadless(program, assembly, userData);
        var stderr = new StringBuilder();
        ide.ErrorDataReceived += (_, e) =>
        {
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }
        };
        ide.BeginErrorReadLine();
        var next = 0;
        async Task<JsonElement> RequestAsync(string method, string parameters)
        {
            var id = ++next;
            await ide.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""").ConfigureAwait(true);
            await ide.StandardInput.FlushAsync().ConfigureAwait(true);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                string? line;
                try
                {
                    line = await ide.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw new SmokeFailure($"The headless IDE didn't answer {method} within 3 minutes.");
                }

                if (line is null)
                {
                    string said;
                    lock (stderr)
                    {
                        said = stderr.ToString();
                    }

                    throw new SmokeFailure($"The headless IDE ended before answering {method}: {said}");
                }

                using var message = JsonDocument.Parse(line); // only JSON-RPC reaches the agent's pipe
                if (message.RootElement.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                {
                    return message.RootElement.GetProperty("result").Clone();
                }
            }
        }

        async Task<(string Text, bool IsError)> CallAsync(string tool, string arguments)
        {
            var result = await RequestAsync("tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""").ConfigureAwait(true);
            return (result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty, result.TryGetProperty("isError", out var error) && error.GetBoolean());
        }

        try
        {
            await RequestAsync("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}""").ConfigureAwait(true);
            var tools = (await RequestAsync("tools/list", "{}").ConfigureAwait(true)).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Check(tools.Contains("get_solution") && tools.Contains("desktop_app_input") && tools.Contains("run_command"), $"the headless IDE lists the IDE's tools ({tools.Count})");
            var solution = string.Empty;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(3) && !solution.Contains("- Greetings", StringComparison.Ordinal))
            {
                solution = (await CallAsync("get_solution", "{}").ConfigureAwait(true)).Text;
                await Task.Delay(500).ConfigureAwait(true);
            }

            Check(solution.Contains("BuildAndRun.slnx", StringComparison.Ordinal) && solution.Contains("Trusted: yes", StringComparison.Ordinal) && solution.Contains("- Greetings", StringComparison.Ordinal), $"get_solution names the trusted solution's projects ({solution})");
            var ran = await CallAsync("run_command", """{"id":"workbench.view.errorList"}""").ConfigureAwait(true);
            Check(!ran.IsError, $"an action runs under allow, with nobody asked ({ran.Text})");
            ide.StandardInput.Close();
            Check(await ExitedAsync(ide, TimeSpan.FromSeconds(60)).ConfigureAwait(true) == 0, "the headless IDE ends when its input closes");
        }
        finally
        {
            if (!ide.HasExited)
            {
                ProcessTree.Kill(ide);
            }
        }
    }

    private Process StartHeadless(string program, string? assembly, string userData)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && assembly is { Length: > 0 })
        {
            start.ArgumentList.Add(assembly);
        }

        foreach (var argument in new[] { "mcp", "--folder", _folder, "--user-data-dir", userData })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new SmokeFailure("The headless IDE didn't start.");
    }

    private static async Task<int> ExitedAsync(Process process, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw new SmokeFailure($"The headless IDE didn't end within {limit.TotalSeconds:0} s.");
        }

        return process.ExitCode;
    }
}
