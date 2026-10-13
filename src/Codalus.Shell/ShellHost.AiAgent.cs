using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Testing;

namespace Codalus.Shell;

/// <summary>
/// Agent mode and MCP (M28d, ADR 0063): the chat's agent runs <see cref="AiAgent"/> with the workspace's tools and the
/// user's MCP servers', every edit through the refactoring preview and every command and MCP call through an approval;
/// <c>codalus mcp</c>'s tools are answered here, over the single-instance pipe.
/// </summary>
public sealed partial class ShellHost
{
    public const string AiMcpServersSetting = "ai.mcpServers";

    private McpServers? _mcp;

    private void InitializeAiAgent()
    {
        Workbench.Settings.Register(
        [
            SettingDescriptor.Create(AiMcpServersSetting, "{}", SettingType.Object, ContributionSource.Shell) with
            {
                Title = "MCP Servers", Category = "AI", Scope = SettingScope.User,
                Description = "MCP servers whose tools the AI agent may use, as { \"name\": { \"command\": …, \"args\": […], \"env\": {…} } }. "
                    + "Each starts when agent mode first needs it, and every call of its tools asks you first.",
            },
        ]);
    }

    private async Task DisposeAiAgentAsync()
    {
        if (_mcp is { } mcp)
        {
            _mcp = null;
            await mcp.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs the agent on a question in the chat: its text and tool calls stream into the conversation.</summary>
    private async Task RunAgentInChatAsync(string question, List<AiAttachment> attachments, List<string> refused, AiTarget? target)
    {
        var root = Solutions.Solution?.Directory;
        var history = _ai.Turns.Select(t => new AiMessageDto(t.Role, t.Text)).ToList();
        var messages = AiContext.Messages(history, attachments, question).ToList();
        messages[0] = new AiMessageDto("system", AiAgent.SystemPrompt + (root is null ? " No workspace is open." : $" The workspace is {root}."));
        _ai.Turns.Add(new AiTurn("user", question, [.. attachments.Select(a => a.Label)], target));
        _ai.Status = refused.Count > 0 ? "Not sent: " + string.Join(" ", refused) : null;
        var streaming = _ai.Streaming = new StringBuilder();
        using var cancel = _ai.Cancel = new CancellationTokenSource();
        UpdateAiStatus();
        _ai.Changed?.Invoke();
        var pending = 0;
        void Append(string text)
        {
            lock (streaming)
            {
                streaming.Append(text);
            }

            if (Interlocked.Exchange(ref pending, 1) == 0)
            {
                _ = Task.Delay(150).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref pending, 0);
                    Ui.Post(() => _ai.Changed?.Invoke());
                }, TaskScheduler.Default);
            }
        }

        try
        {
            var host = new AgentHost(this);
            var tools = new AiAgentTools(host, new AiContext(root)).Create().ToList();
            if (McpServerConfig.Parse(Workbench.Settings.GetValue(AiMcpServersSetting)) is { Count: > 0 } servers)
            {
                _mcp ??= new McpServers(line => Workbench.Log.AppendLine(line));
                tools.AddRange(await _mcp.ToolsAsync(servers, root, host, cancel.Token).ConfigureAwait(true));
                if (_mcp.Failures.Count > 0)
                {
                    Append(string.Join("\n", _mcp.Failures.Values.Select(f => "⚠ " + f)) + "\n\n");
                }
            }

            var provider = AiProvider;
            var model = AiModel;
            var agent = new AiAgent((m, t, onEvent, ct) => Plugins.ChatEventsAsync(provider, model, m, t, onEvent, ct), tools);
            await agent.RunAsync(messages, progress =>
            {
                switch (progress)
                {
                    case AiAgentProgress.Text text:
                        Append(text.Piece);
                        break;
                    case AiAgentProgress.Calling calling:
                        Append($"\n\n▸ {calling.Tool}: {calling.Summary}\n");
                        break;
                    case AiAgentProgress.Result result:
                        Append($"  ↳ {result.Summary}\n\n");
                        break;
                }
            }, cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _ai.Status = "Stopped.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            _ai.Status = ex.Message;
        }
        finally
        {
            string answer;
            lock (streaming)
            {
                answer = streaming.ToString().Trim();
            }

            if (answer.Length > 0)
            {
                _ai.Turns.Add(new AiTurn("assistant", answer, [], null));
            }

            _ai.Streaming = null;
            _ai.Cancel = null;
            UpdateAiStatus();
            _ai.Changed?.Invoke();
        }
    }

    /// <summary>A file's problems (or every file's), one per line, for the agent and <c>codalus mcp</c>.</summary>
    private string ProblemsText(string? path)
    {
        static string Severity(int severity) => severity switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" };
        var lines = new List<string>();
        var files = path is null
            ? Languages.Diagnostics.All.GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => (Path: g.Key, Items: g.SelectMany(f => f.Items).ToList()))
            : [(path, Languages.Diagnostics.For(path).ToList())];
        foreach (var (file, items) in files)
        {
            foreach (var d in items.Where(d => d.Severity <= 2).OrderBy(d => d.Range.Start.Line))
            {
                lines.Add($"{file}({d.Range.Start.Line + 1},{d.Range.Start.Character + 1}): {Severity(d.Severity)} {d.Code} {d.Message}");
                if (lines.Count >= 200)
                {
                    return string.Join('\n', lines) + "\n(more not listed)";
                }
            }
        }

        foreach (var error in Builds.Diagnostics.Where(d => d.Severity == "error" && (path is null || string.Equals(d.File, path, StringComparison.OrdinalIgnoreCase))).Take(100))
        {
            lines.Add($"{error.File}({error.Line},{error.Column}): build error {error.Code} {error.Message}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>Builds the solution for the agent or an external one; returns the outcome and the errors.</summary>
    private async Task<string> BuildForAgentAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            return "No solution is open.";
        }

        if (!solution.Trusted)
        {
            return "The solution's folder isn't trusted, so it isn't built.";
        }

        var succeeded = await BuildAsync("Build", null).ConfigureAwait(true);
        var errors = Builds.Diagnostics.Where(d => d.Severity == "error").Take(100)
            .Select(e => $"{e.File}({e.Line},{e.Column}): error {e.Code} {e.Message}").ToList();
        return (succeeded ? "The build succeeded." : "The build failed.") + (errors.Count > 0 ? "\n" + string.Join('\n', errors) : string.Empty);
    }

    /// <summary>Shows an agent's edit in the refactoring preview; true when the user applied it.</summary>
    private async Task<bool> ProposeAgentEditAsync(string path, string? oldText, string newText)
    {
        var current = OpenTextOf(path) ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true)).Text : null);
        LanguageServices.Protocol.Range range;
        string replacement;
        if (oldText is not null && current is not null && current.IndexOf(oldText, StringComparison.Ordinal) is var at and >= 0)
        {
            range = new LanguageServices.Protocol.Range(PositionAt(current, at), PositionAt(current, at + oldText.Length));
            replacement = newText;
        }
        else
        {
            var text = current ?? string.Empty;
            range = new LanguageServices.Protocol.Range(new Position(0, 0), PositionAt(text, text.Length));
            replacement = newText;
        }

        var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), null), [new TextEdit(range, replacement)])]);
        var name = Path.GetFileName(path);
        return await PreviewAndApplyAsync(current is null ? $"The AI agent wants to create {name}" : $"The AI agent wants to change {name}", edit).ConfigureAwait(true);
    }

    private static Position PositionAt(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Position(line, offset - lineStart);
    }

    /// <summary>The agent's view of the IDE: every call comes back to the UI thread.</summary>
    private sealed class AgentHost(ShellHost shell) : IAiAgentHost
    {
        public string? Root { get; } = shell.Solutions.Solution?.Directory;

        public bool Trusted { get; } = shell.Solutions.Solution?.Trusted == true;

        public string? OpenText(string path) => Ui.RunAsync(() => shell.OpenTextOf(path)).GetAwaiter().GetResult();

        public string Problems(string? path) => Ui.RunAsync(() => shell.ProblemsText(path)).GetAwaiter().GetResult();

        public Task<bool> ProposeEditAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) =>
            Ui.RunAsync(() => shell.ProposeAgentEditAsync(path, oldText, newText)).Unwrap();

        public Task<bool> ApproveCommandAsync(string command, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () => (await shell.Dialogs.ShowAsync(new DialogRequest("Run a command for the AI agent?", $"In {Root}:", ["Run", "Don't Run"])
            {
                Details = [new DetailLine(command, "header")],
                DefaultButton = "Don't Run",
            }).ConfigureAwait(true)).Button == "Run").Unwrap();

        public Task<AiApproval> ApproveToolAsync(string server, string tool, string arguments, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () => (await shell.Dialogs.ShowAsync(new DialogRequest($"Let the AI agent use {tool}?", $"A tool of the MCP server {server}, with:", ["Allow Once", "Allow for This Session", "Don't Allow"])
            {
                Details = [new DetailLine(arguments.Length > 2000 ? arguments[..2000] + "…" : arguments)],
                DefaultButton = "Don't Allow",
            }).ConfigureAwait(true)).Button switch
            {
                "Allow Once" => AiApproval.Once,
                "Allow for This Session" => AiApproval.Session,
                _ => AiApproval.Deny,
            }).Unwrap();

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Ui.RunAsync(shell.BuildForAgentAsync).Unwrap();
    }

    // ------------------------------------------------------------------ codalus mcp

    /// <summary>A tool call of <c>codalus mcp</c> (on a background thread); its answer is text.</summary>
    private Task<string> OnInstanceQueryAsync(InstanceRequest request)
    {
        if (!request.Command.StartsWith(McpServer.RequestPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult($"Unknown request: {request.Command}");
        }

        var tool = request.Command[McpServer.RequestPrefix.Length..];
        JsonObject args;
        try
        {
            args = request.Arguments is [var json, ..] && JsonNode.Parse(json) is JsonObject o ? o : [];
        }
        catch (JsonException)
        {
            args = [];
        }

        return Ui.RunAsync(async () => tool switch
        {
            "get_solution" => SolutionText(),
            "get_problems" => args["path"] is JsonValue p && p.TryGetValue<string>(out var path) && path.Length > 0
                ? ProblemsText(Path.GetFullPath(path)) is { Length: > 0 } some ? some : "No problems in " + path + "."
                : ProblemsText(null) is { Length: > 0 } all ? all : "No problems.",
            "get_test_results" => TestResultsText(),
            "build" => await BuildForAgentAsync().ConfigureAwait(true),
            _ => $"Unknown tool: {tool}",
        }).Unwrap();
    }

    private string SolutionText()
    {
        if (Solutions.Solution is not { } solution)
        {
            return "No solution is open in Codalus IDE.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{solution.Path}{(solution.Trusted ? string.Empty : " (not trusted)")}\n");
        foreach (var project in Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(CultureInfo.InvariantCulture, $"- {project.Name}: {project.Path} [{string.Join(", ", project.TargetFrameworks)}]{(project.OutputType is { } type ? " " + type : string.Empty)}{(project.Error is { } error ? " — " + error : string.Empty)}\n");
        }

        return text.ToString().TrimEnd();
    }

    private string TestResultsText()
    {
        var (passed, failed, failures) = Tests.Read(roots =>
        {
            var leaves = roots.SelectMany(Leaves).Where(n => n.Result is not null).ToList();
            return (leaves.Count(n => n.Result!.Outcome == TestOutcome.Passed), leaves.Count(n => n.Result!.Outcome.IsFailure()),
                leaves.Where(n => n.Result!.Outcome.IsFailure()).Take(30).Select(n => (n.Label, n.Uri, n.Result!.Messages)).ToList());
        });
        if (passed + failed == 0)
        {
            return "No tests have run.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{passed} passed, {failed} failed.\n");
        foreach (var (label, uri, messages) in failures)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nFAILED {label}{(uri is null ? string.Empty : " (" + DocumentUris.ToPath(uri) + ")")}\n");
            foreach (var message in messages.Take(3))
            {
                text.Append(message.Message.Trim()).Append('\n');
                if (message.Expected is not null || message.Actual is not null)
                {
                    text.Append(CultureInfo.InvariantCulture, $"Expected: {message.Expected}\nActual: {message.Actual}\n");
                }
            }
        }

        return text.ToString().TrimEnd();

        static IEnumerable<TestNode> Leaves(TestNode node) => node.IsLeaf ? [node] : node.Children.SelectMany(Leaves);
    }
}
