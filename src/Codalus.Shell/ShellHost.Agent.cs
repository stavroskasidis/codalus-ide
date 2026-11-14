using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Agent;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Build;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Agent mode and MCP (M28c, ADR 0063): the agent's host (files as the editor has them, the preview for every change,
/// approvals for commands and MCP tools, diagnostics and builds), the user's MCP servers, and the bridge that lets
/// <c>codalus mcp</c> reach this IDE while <c>ai.mcpServer.enabled</c> is on.
/// </summary>
public sealed partial class ShellHost
{
    public const string McpServerSetting = "ai.mcpServer.enabled";

    /// <summary>Who asks for an IDE tool from AI Chat's built-in agent (the approvals and the log name it).</summary>
    private static string AiChatBuiltInCaller => Localizer.Get("msg.ai.assistant", "Assistant");

    private McpBridge? _mcpBridge;

    /// <summary>The user's MCP servers (started when agent mode first needs their tools).</summary>
    public McpServers McpServers { get; private set; } = null!;

    /// <summary>Whether the MCP bridge for <c>codalus mcp</c> is listening (tests).</summary>
    public bool IsMcpServerListening => _mcpBridge is not null;

    private void InitializeAgent()
    {
        McpServers = new McpServers(Workbench.Settings, line => Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI").AppendLine(line));
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(McpServerSetting))
            {
                Ui.Post(UpdateMcpBridge);
            }
        };
        UpdateMcpBridge();
        InitializeIdeTools();
    }

    private void UpdateMcpBridge()
    {
        // The headless IDE (codalus mcp --folder) serves its own agent only: the general pipe is the window's.
        var on = !IsHeadless && Workbench.Settings.GetBoolean(McpServerSetting, false) && Workbench.Settings.GetLayerValue(McpServerSetting, Codalus.Workbench.Settings.SettingScope.User) is { ValueKind: JsonValueKind.True };
        if (on && _mcpBridge is null)
        {
            _mcpBridge = McpBridge.Listen(McpBridge.PipeName(SingleInstance.PipeName(Paths.State)), (tool, args, ct) => Ui.RunAsync(() => OutsideAgentToolAsync(tool, args, ct)).Unwrap());
        }
        else if (!on && _mcpBridge is not null)
        {
            _mcpBridge.Dispose();
            _mcpBridge = null;
        }
    }

    // ------------------------------------------------------------------ agents outside the IDE (M60)

    /// <summary>How long the question to an agent outside the IDE waits for an answer before the call is refused.</summary>
    internal TimeSpan OutsideAgentQuestionTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The folders whose IDE tools agents outside the IDE reach without asking ("Always for This Folder"), in the user's
    /// state, never in the repository, so a cloned repository can't allow itself.
    /// </summary>
    internal Codalus.Workbench.Projects.TrustStore OutsideAgentFolders => _outsideAgentFolders ??= new(Path.Combine(Paths.State, "ai-outside-agents-allowed.json"));

    private Codalus.Workbench.Projects.TrustStore? _outsideAgentFolders;

    /// <summary>This window's answers for the folders it has had open (Allow, Don't Allow), until it closes.</summary>
    private readonly Dictionary<string, bool> _outsideAgentAnswers = new(StringComparer.Ordinal);

    /// <summary>The question being asked, so calls that arrive meanwhile wait for the same answer.</summary>
    private Task<bool>? _outsideAgentQuestion;

    /// <summary>
    /// A call from <c>codalus mcp</c>'s general pipe (an agent outside the IDE, ADR 0099): the first call for a folder asks
    /// once (Allow, Always for This Folder, Don't Allow), on top of <c>ai.mcpServer.enabled</c>; an unanswered question is
    /// refused after <see cref="OutsideAgentQuestionTimeout"/>. Listing the tools never asks.
    /// </summary>
    private async Task<IdeToolResult> OutsideAgentToolAsync(string tool, JsonElement args, CancellationToken cancellationToken)
    {
        if (tool != McpBridge.ListToolsCall && !await OutsideAgentAllowedAsync(cancellationToken).ConfigureAwait(true))
        {
            Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI").AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} IDE tool (codalus mcp): {tool} → not allowed in this folder"));
            return IdeToolResult.Error("The user didn't allow agents outside Codalus IDE to use its tools in this folder. Don't try again; ask the user to allow it in the IDE.");
        }

        return await IdeToolAsync(tool, args, null, cancellationToken).ConfigureAwait(true);
    }

    private async Task<bool> OutsideAgentAllowedAsync(CancellationToken cancellationToken)
    {
        var folder = Solutions.Solution?.Directory;
        var key = folder ?? "";
        if (_outsideAgentAnswers.TryGetValue(key, out var answered))
        {
            return answered;
        }

        if (folder is not null && OutsideAgentFolders.IsTrusted(folder))
        {
            return true;
        }

        _outsideAgentQuestion ??= AskOutsideAgentAsync(folder, key);
        var question = _outsideAgentQuestion;
        try
        {
            return await question.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            if (question.IsCompleted && ReferenceEquals(_outsideAgentQuestion, question))
            {
                _outsideAgentQuestion = null;
            }
        }
    }

    private async Task<bool> AskOutsideAgentAsync(string? folder, string key)
    {
        var allow = Localizer.Get("msg.outsideAgent.allow", "Allow");
        var always = Localizer.Get("msg.outsideAgent.always", "Always for This Folder");
        var deny = Localizer.Get("msg.outsideAgent.deny", "Don't Allow");
        var where = folder ?? Localizer.Get("msg.outsideAgent.noFolder", "this window (no folder is open)");
        var buttons = folder is null ? new[] { allow, deny } : [allow, always, deny];
        using var timeout = new CancellationTokenSource(OutsideAgentQuestionTimeout);
        var choice = await ShowCancellableAsync(new DialogRequest(
            Localizer.Get("msg.outsideAgent.title", "Allow an agent outside the IDE?"),
            Localizer.Format("msg.outsideAgent.message", "An AI agent running outside Codalus IDE (codalus mcp) wants to use the IDE's tools in {0}: read its code, the Error List and Git, and, as Agent IDE Actions allow, build, test, run, debug and change it.", where),
            buttons)
        {
            DefaultButton = deny,
            Icon = "shield",
        }, timeout.Token).ConfigureAwait(true);
        if (choice.Button is null)
        {
            return false; // unanswered (or closed): this call is refused, and the next one asks again
        }

        if (choice.Button == always && folder is not null)
        {
            OutsideAgentFolders.Trust(folder);
            return true;
        }

        _outsideAgentAnswers[key] = choice.Button == allow;
        return choice.Button == allow;
    }

    /// <summary>The known agents' setups for <c>codalus mcp</c>, with this install's path (M60).</summary>
    internal IReadOnlyList<McpSetup> McpSetupsForThisInstall
    {
        get
        {
            var (path, assembly) = McpExecutable;
            return McpSetups.For(path, assembly, Options.UserDataDir);
        }
    }

    /// <summary>
    /// Set Up Agents Outside the IDE (<c>ai.mcpServer.setup</c>, from Options › AI › MCP Server's description): each known
    /// agent's setup, with a Copy button each; a copy turns the MCP server on when it's off, after asking.
    /// </summary>
    internal async Task ShowMcpSetupAsync()
    {
        var setups = McpSetupsForThisInstall;
        var details = new List<DetailLine>();
        foreach (var setup in setups)
        {
            details.Add(new DetailLine(Localizer.Format("msg.mcpSetup.where", "{0} — in {1}:", setup.Agent, setup.Where), "header"));
            details.AddRange(setup.Text.Split('\n').Select(line => new DetailLine(line)));
            details.Add(new DetailLine(""));
        }

        var close = Localizer.Get("msg.mcpSetup.close", "Close");
        var copies = setups.ToDictionary(s => Localizer.Format("msg.mcpSetup.copy", "Copy for {0}", s.Agent), s => s.Text, StringComparer.Ordinal);
        var on = Workbench.Settings.GetLayerValue(McpServerSetting, Codalus.Workbench.Settings.SettingScope.User) is { ValueKind: JsonValueKind.True };
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Get("msg.mcpSetup.title", "Set Up Agents Outside the IDE"),
            on
                ? Localizer.Get("msg.mcpSetup.message", "An agent running outside Codalus IDE reaches it through codalus mcp. Add this to the agent; the first call in a folder asks you here.")
                : Localizer.Get("msg.mcpSetup.messageOff", "An agent running outside Codalus IDE reaches it through codalus mcp. Add this to the agent, and turn on MCP Server (a copy offers to); the first call in a folder asks you here."),
            [.. copies.Keys, close])
        {
            DefaultButton = close,
            Details = details,
            Icon = "bot",
        }).ConfigureAwait(true);
        if (choice.Button is not { } button || !copies.TryGetValue(button, out var text))
        {
            return;
        }

        if (Window?.Clipboard is { } clipboard)
        {
            await SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
        }

        LastMcpSetupCopy = text;
        if (!on)
        {
            var turnOn = Localizer.Get("msg.mcpSetup.turnOn", "Turn On");
            var answer = await Dialogs.ShowAsync(new DialogRequest(
                Localizer.Get("msg.mcpSetup.offTitle", "Turn on MCP Server?"),
                Localizer.Get("msg.mcpSetup.offMessage", "Copied. The agent reaches the IDE only while MCP Server (ai.mcpServer.enabled) is on."),
                [turnOn, Localizer.Get("msg.mcpSetup.notNow", "Not Now")])).ConfigureAwait(true);
            if (answer.Button == turnOn)
            {
                await Workbench.Settings.SetAsync(McpServerSetting, true, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    /// <summary>The last setup copied (tests: the clipboard isn't there headless).</summary>
    internal string? LastMcpSetupCopy { get; private set; }

    // ------------------------------------------------------------------ agent mode

    /// <summary>
    /// Asks the built-in assistant, which is always an agent (ADR 0092): the loop with agent mode's tools, the IDE's and the
    /// MCP servers'. Where the tools can't run (no folder, an untrusted one, a model that doesn't take them) it answers
    /// without tools, and the turn says why.
    /// </summary>
    public async Task RunAgentAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor, CancellationToken cancellationToken)
    {
        if (await WhyNoToolsAsync(cancellationToken).ConfigureAwait(true) is { } reason)
        {
            await AiChat.Chat.SendAsync(question, items, cancellationToken, anchor, withoutTools: reason).ConfigureAwait(true);
            return;
        }

        var solution = Solutions.Solution!;
        // The IDE's tools (ADR 0092: code intelligence, tests, run, debug, the browser; plugins' tools), then the MCP servers'.
        var external = new List<ExternalAgentTool>();
        foreach (var definition in IdeToolDefinitions.Where(t => !AgentTools.OwnToolNames.Contains(t.Name)))
        {
            using var schema = JsonDocument.Parse(definition.Schema);
            var name = definition.Name;
            external.Add(new ExternalAgentTool(
                new Codalus.Plugins.Protocol.ChatToolDto(name, definition.Description, schema.RootElement.Clone()),
                definition.Source,
                async (args, ct) =>
                {
                    var result = await Ui.RunAsync(() => IdeToolAsync(name, args, AiChatBuiltInCaller, ct)).Unwrap().ConfigureAwait(false);
                    return result.IsError ? throw new IdeToolException(result.Text) : result.Text;
                })
            {
                ApprovesItself = true,
            });
        }

        if (McpServers.Configured.Count > 0)
        {
            external.AddRange(await McpServers.GetToolsAsync(cancellationToken).ConfigureAwait(true));
        }

        var tools = new AgentTools(Ai, new AgentHost(this), solution.Directory, external)
        {
            CommandTimeout = TimeSpan.FromSeconds(Math.Clamp(Workbench.Settings.GetInt32("ai.agent.commandTimeoutSeconds", 120), 5, 3600)),
        };
        var maxSteps = Math.Clamp(Workbench.Settings.GetInt32("ai.agent.maxSteps", 25), 1, 200);
        await AiChat.Chat.SendAgentAsync(question, items, tools, maxSteps, cancellationToken, anchor).ConfigureAwait(true);
    }

    /// <summary>Why the built-in assistant can't use tools here, or null when it can.</summary>
    private async Task<string?> WhyNoToolsAsync(CancellationToken cancellationToken)
    {
        if (Solutions.Solution is not { } solution)
        {
            return Localizer.Get("msg.ai.noToolsNoFolder", "Answered without tools: no folder is open, so the assistant can't read, change or run anything.");
        }

        if (!solution.Trusted)
        {
            return Localizer.Get("msg.ai.noToolsUntrusted", "Answered without tools: this folder isn't trusted, so the assistant can't read, change or run anything.");
        }

        if (Ai.ProviderId is { } providerId)
        {
            try
            {
                var models = await LanguageModels.GetModelsAsync(providerId, cancellationToken).ConfigureAwait(true);
                var model = Ai.ModelId ?? (models.Count > 0 ? models[0].Id : null);
                if (models.FirstOrDefault(m => m.Id == model) is { SupportsTools: false })
                {
                    return Localizer.Format("msg.ai.noToolsModel", "Answered without tools: the model {0} doesn't take tools. Choose another model to let the assistant read, change and run.", model!);
                }
            }
            catch (LanguageModelRequestException)
            {
                // The request itself will say what's wrong.
            }
        }

        return null;
    }

    /// <summary>The agent's view of the IDE.</summary>
    private sealed class AgentHost(ShellHost shell) : IAgentHost
    {
        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () => shell.OpenTextOf(path) ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Text : null)).Unwrap();

        public Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var relative = shell.Solutions.Solution?.Directory is { } root ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
                if (oldText is null)
                {
                    var create = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), null), [new TextEdit(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), newText)])]);
                    return await shell.PreviewAndApplyAsync(Localizer.Format("msg.ai.agentCreates", "The AI agent creates {0}", relative), create).ConfigureAwait(true);
                }

                var text = shell.OpenTextOf(path) ?? (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Text;
                var at = text.IndexOf(oldText, StringComparison.Ordinal);
                if (at < 0)
                {
                    return false;
                }

                var version = shell._models.TryGetValue(path, out var model) ? model.Version : (int?)null;
                var edit = new WorkspaceEdit([new TextDocumentEdit(
                    new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), version),
                    [new TextEdit(new LanguageServices.Protocol.Range(PositionIn(text, at), PositionIn(text, at + oldText.Length)), newText)])]);
                return await shell.PreviewAndApplyAsync(Localizer.Format("msg.ai.agentChanges", "The AI agent changes {0}", relative), edit).ConfigureAwait(true);
            }).Unwrap();

        public Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var allow = Localizer.Get("msg.ai.allow", "Allow");
                var choice = await shell.Dialogs.ShowAsync(new DialogRequest(title, message, [allow, Localizer.Get("msg.ai.reject", "Reject")])
                {
                    Details = [.. details.Select(d => new DetailLine(d))],
                    DefaultButton = Localizer.Get("msg.ai.reject", "Reject"),
                }).ConfigureAwait(true);
                return choice.Button == allow;
            }).Unwrap();

        public Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken) =>
            Ui.RunAsync(() => shell.DiagnosticsText(path));

        public Task<string> BuildOutputAsync(CancellationToken cancellationToken) =>
            Ui.RunAsync(() => shell.BuildOutputText());

        public Task<string> BuildAsync(CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var succeeded = await shell.BuildAsync("build", null).ConfigureAwait(true);
                return (succeeded ? "The build succeeded." : "The build failed.") + "\n" + shell.DiagnosticsText(null);
            }).Unwrap();
    }

    /// <summary>The Error List's rows as text (of one file, or all; at most 200).</summary>
    private string DiagnosticsText(string? path, Func<string, bool>? outside = null)
    {
        var rows = ErrorList.Rows.Where(r => (path is null || SolutionPaths.Comparer.Equals(r.Path, path)) && (outside is null || (r.Path.Length > 0 && !outside(r.Path)))).Take(200).ToList();
        if (rows.Count == 0)
        {
            return "No errors or warnings.";
        }

        var root = Solutions.Solution?.Directory;
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var file = root is not null && row.Path.Length > 0 ? Path.GetRelativePath(root, row.Path).Replace('\\', '/') : row.File;
            sb.Append(file).Append('(').Append(row.Line.ToString(CultureInfo.InvariantCulture)).Append(',').Append(row.Column.ToString(CultureInfo.InvariantCulture)).Append("): ")
                .Append(row.Icon).Append(' ').Append(row.Code).Append(": ").AppendLine(row.Description);
        }

        return sb.ToString();
    }

    private string BuildOutputText()
    {
        var output = Workbench.Output.Channels.FirstOrDefault(c => c.Id == BuildService.OutputChannelId)?.GetText() ?? "";
        return output.Length == 0 ? "No build output." : output.Length > MaxAiBuildOutput ? output[^MaxAiBuildOutput..] : output;
    }
}
