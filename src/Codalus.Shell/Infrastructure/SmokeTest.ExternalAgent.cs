using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M65's smoke step: an external agent (ADR 0079) in AI Chat, in the app as it ships. Native AOT runs AI Chat's external
/// agent path here and nowhere else: the ACP client, its source-generated messages, the session's steps, and the
/// <c>codalus mcp --agent-session</c> the agent is given, which is this executable. The agent is the tests' stand-in
/// (tests/Codalus.AcpAgent.Fake), built from the repository by the step and run by <c>dotnet</c>: no network, no model.
/// </summary>
internal sealed partial class SmokeTest
{
    private const string StandInAgentName = "stand-in";

    private async Task ExternalAgentChatStepAsync()
    {
        var settings = shell.Workbench.Settings;
        var root = shell.Solutions.Solution is { Trusted: true, Directory: var directory } ? directory : throw new SmokeFailure("No trusted solution is open.");
        var file = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            ?? throw new SmokeFailure($"No C# file in {root}.");
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var agent = await BuildStandInAgentAsync().ConfigureAwait(true);
        var log = Path.Combine(Work, "acp-stand-in.jsonl");

        var entry = new JsonObject
        {
            [StandInAgentName] = new JsonObject
            {
                ["command"] = "dotnet",
                ["args"] = new JsonArray(agent),
                ["env"] = new JsonObject { ["ACP_FAKE_LOG"] = log },
            },
        };
        var chat = shell.AiChat;
        try
        {
            await settings.SetAsync(Workbench.Ai.AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            using (var agents = JsonDocument.Parse(entry.ToJsonString()))
            {
                await settings.SetAsync(Workbench.Ai.Acp.AcpAgents.AgentsSetting, agents.RootElement.Clone(), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }

            shell.ShowAiChat();
            await WaitAsync(() => window.GetVisualDescendants().OfType<AiChatView>().Any(), "AI Chat opens").ConfigureAwait(true);
            await WaitAsync(() => chat.AgentChoices.Contains(StandInAgentName), () => $"the stand-in is in the agent picker ({string.Join(", ", chat.AgentChoices)})").ConfigureAwait(true);
            chat.SelectAgent(StandInAgentName);
            await WaitAsync(() => chat.SelectedAgent == StandInAgentName && !chat.IsAgentBlocked, () => $"the stand-in is chosen and may run ({chat.SelectedAgent}: {chat.AgentBlockedMessage})").ConfigureAwait(true);

            // A turn: the agent's own answer, under its name.
            var hello = await AskExternalAsync("hello").ConfigureAwait(true);
            Check(hello.Error is null && hello.Answer == "Hello from the stand-in." && hello.ExternalAgent == StandInAgentName, $"the stand-in answered ({hello.Error ?? hello.Answer})");

            // A read: the agent asks the IDE for a file of the workspace (fs/read_text_file), a step with its location.
            var read = await AskExternalAsync("read " + relative).ConfigureAwait(true);
            Check(read.Error is null && read.Answer.StartsWith("Read: ", StringComparison.Ordinal), $"the IDE gave the agent {relative} ({read.Error ?? read.Answer})");
            Check(read.Steps is [{ Kind: "read", Status: "completed" }], $"the read is a completed step ({string.Join(", ", read.Steps.Select(s => $"{s.Kind} {s.Status}"))})");
            await WaitAsync(() => window.GetVisualDescendants().OfType<AiChatTurnView>().Any(v => v.Turn == read), "AI Chat shows the agent's turn").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("ai-chat-external.png");

            // The session got this executable as its codalus mcp, which reaches this IDE through the session's pipe.
            var server = ReadAgentLog(log).LastOrDefault(m => m["method"]?.GetValue<string>() == "session/new")?["params"]?["mcpServers"]?[0]
                ?? throw new SmokeFailure("The agent's session/new had no MCP server.");
            var command = server["command"]!.GetValue<string>();
            var args = server["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            Check(string.Equals(Path.GetFullPath(command), Path.GetFullPath(shell.McpExecutable.Path), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) && args.Contains("--agent-session"), $"the agent's MCP server is this app ({command} {string.Join(' ', args)})");
            await using (var client = await McpClient.StartAsync("codalus", new McpServerConfig(command, args, new Dictionary<string, string>()), CancellationToken.None).ConfigureAwait(true))
            {
                using var empty = JsonDocument.Parse("{}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var solution = await client.CallAsync("get_solution", empty.RootElement, timeout.Token).ConfigureAwait(true);
                var name = Path.GetFileName(shell.Solutions.Solution!.Path);
                Check(solution.Contains(name, StringComparison.Ordinal), $"get_solution through the session's pipe names {name} ({solution})");
            }
        }
        finally
        {
            // The built-in assistant again, and no agent left configured or remembered (the command sweep runs AI Chat's commands).
            chat.SelectedAgentIndex = 0;
            await settings.SetAsync(Workbench.Ai.Acp.AcpAgents.AgentsSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await settings.SetAsync(Workbench.Ai.AiService.EnabledSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task<Workbench.Ai.AiChatTurn> AskExternalAsync(string question)
    {
        var chat = shell.AiChat;
        var count = chat.Chat.Turns.Count;
        string State() => $"turns {chat.Chat.Turns.Count} (were {count}), CanSend {chat.CanSend}, IsBusy {chat.IsBusy}, IsAnswering {chat.IsAnswering}, blocked: {chat.AgentBlockedMessage ?? "no"}";

        // The previous turn ends on the agent's thread and the chat stops answering a posted job later (AcpShellTests).
        chat.Input = question;
        await WaitAsync(() => chat.CanSend, State).ConfigureAwait(true);
        _ = chat.SendAsync();
        await WaitAsync(() => chat.Chat.Turns.Count > count, State).ConfigureAwait(true);
        var turn = chat.Chat.Turns[^1];
        await WaitAsync(() => !turn.IsStreaming, () => $"the agent's answer to '{question}' ({State()}, answer '{turn.Answer}')", 60).ConfigureAwait(true);
        return turn;
    }

    /// <summary>Builds the stand-in ACP agent from the repository into the work folder and returns its assembly.</summary>
    private async Task<string> BuildStandInAgentAsync()
    {
        var project = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(samples))!, "tests", "Codalus.AcpAgent.Fake", "Codalus.AcpAgent.Fake.csproj");
        Check(File.Exists(project), $"the stand-in agent's project is at {project}");
        var output = Path.Combine(Work, "acp-stand-in");
        using var process = Process.Start(new ProcessStartInfo("dotnet", ["build", project, "-c", "Release", "-o", output, "-nologo", "-v", "q"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new SmokeFailure("dotnet build didn't start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Platform.ProcessTree.Kill(process);
            throw new SmokeFailure("Building the stand-in agent took over 5 minutes.");
        }

        var dll = Path.Combine(output, "codalus-fake-acp-agent.dll");
        Check(process.ExitCode == 0 && File.Exists(dll), $"the stand-in agent builds (exit {process.ExitCode}): {await stdout.ConfigureAwait(true)}{await stderr.ConfigureAwait(true)}");
        return dll;
    }

    /// <summary>The messages the stand-in agent received, one JSON object a line (ACP_FAKE_LOG); it may still be writing.</summary>
    private static List<JsonNode> ReadAgentLog(string log)
    {
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var messages = new List<JsonNode>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && JsonNode.Parse(line) is { } message)
            {
                messages.Add(message);
            }
        }

        return messages;
    }
}
