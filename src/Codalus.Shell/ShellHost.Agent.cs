using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Agent;
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
    }

    private void UpdateMcpBridge()
    {
        var on = Workbench.Settings.GetBoolean(McpServerSetting, false) && Workbench.Settings.GetLayerValue(McpServerSetting, Codalus.Workbench.Settings.SettingScope.User) is { ValueKind: JsonValueKind.True };
        if (on && _mcpBridge is null)
        {
            _mcpBridge = McpBridge.Listen(McpBridge.PipeName(SingleInstance.PipeName(Paths.State)), (tool, args, ct) => Ui.RunAsync(() => McpToolAsync(tool, args, ct)).Unwrap());
        }
        else if (!on && _mcpBridge is not null)
        {
            _mcpBridge.Dispose();
            _mcpBridge = null;
        }
    }

    // ------------------------------------------------------------------ agent mode

    /// <summary>
    /// Asks in agent mode: checks that the workspace is trusted and the model takes tools, then runs the loop with the
    /// built-in tools and the MCP servers' tools.
    /// </summary>
    public async Task RunAgentAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor, CancellationToken cancellationToken)
    {
        if (Solutions.Solution is not { } solution)
        {
            ShowMessage(Localizer.Get("msg.ai.agentNeedsASolution", "Agent mode works in a solution or folder: open one first."));
            return;
        }

        if (!solution.Trusted)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.ai.agentNeedsTrust", "Agent mode works only in trusted folders: it reads files and runs commands you allow."));
            return;
        }

        if (Ai.ProviderId is { } providerId)
        {
            try
            {
                var models = await LanguageModels.GetModelsAsync(providerId, cancellationToken).ConfigureAwait(true);
                var model = Ai.ModelId ?? (models.Count > 0 ? models[0].Id : null);
                if (models.FirstOrDefault(m => m.Id == model) is { SupportsTools: false })
                {
                    Workbench.Notifications.Warning(Localizer.Format("msg.ai.modelTakesNoTools", "The model {0} doesn't take tools, so agent mode can't use it. Choose another model, or ask without Agent.", model!));
                    return;
                }
            }
            catch (LanguageModelRequestException)
            {
                // The request itself will say what's wrong.
            }
        }

        IReadOnlyList<ExternalAgentTool> external = [];
        if (McpServers.Configured.Count > 0)
        {
            external = await McpServers.GetToolsAsync(cancellationToken).ConfigureAwait(true);
        }

        var tools = new AgentTools(Ai, new AgentHost(this), solution.Directory, external)
        {
            CommandTimeout = TimeSpan.FromSeconds(Math.Clamp(Workbench.Settings.GetInt32("ai.agent.commandTimeoutSeconds", 120), 5, 3600)),
        };
        var maxSteps = Math.Clamp(Workbench.Settings.GetInt32("ai.agent.maxSteps", 25), 1, 200);
        await AiChat.Chat.SendAgentAsync(question, items, tools, maxSteps, cancellationToken, anchor).ConfigureAwait(true);
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

    // ------------------------------------------------------------------ the MCP server's tools

    /// <summary>A call from <c>codalus mcp</c> (on the UI thread); what it returns passes the AI boundary.</summary>
    private async Task<string> McpToolAsync(string tool, JsonElement args, CancellationToken cancellationToken, string? agent = null)
    {
        var root = Solutions.Solution?.Directory;
        async Task<bool> Excluded(string path) => await Ai.Exclusions.IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true);

        // An external agent's session pipe (M48, ADR 0079): only while it may run, and only what's under its workspace.
        if (agent is not null && ExternalAgents.WhyNot() is { } reason)
        {
            throw new McpException(reason);
        }

        bool Outside(string path) => agent is not null && (root is null || !Codalus.Workbench.FileSystem.FileSystems.IsUnder(Path.GetFullPath(path), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        string text;
        switch (tool)
        {
            case "get_solution":
                if (Solutions.Solution is not { } solution)
                {
                    return "No solution is open.";
                }

                var sb = new StringBuilder().Append("Solution: ").AppendLine(solution.Path).Append("Trusted: ").AppendLine(solution.Trusted ? "yes" : "no");
                foreach (var project in Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append("- ").Append(project.Name).Append(" (").Append(project.Path).Append("): ").Append(string.Join(", ", project.TargetFrameworks));
                    if (project.OutputType is { Length: > 0 } outputType)
                    {
                        sb.Append(", ").Append(outputType);
                    }

                    if (project.IsTestProject)
                    {
                        sb.Append(", tests");
                    }

                    sb.AppendLine();
                }

                text = sb.ToString();
                break;
            case "get_open_files":
                var open = new StringBuilder();
                foreach (var editor in Editors.Select(e => e.Model).Distinct())
                {
                    if (!Outside(editor.Path) && !await Excluded(editor.Path).ConfigureAwait(true))
                    {
                        open.Append(ReferenceEquals(editor, ActiveEditor?.Model) ? "* " : "- ").AppendLine(editor.Path);
                    }
                }

                if (ActiveEditor?.Controller?.Editor is { SelectionLength: > 0 } selected && ActiveEditor.Model is { } active && !Outside(active.Path) && !await Excluded(active.Path).ConfigureAwait(true))
                {
                    open.AppendLine().Append("Selection in ").Append(active.Path).AppendLine(":").AppendLine(selected.SelectedText);
                }

                text = open.Length == 0 ? "No files are open." : open.ToString();
                break;
            case "get_diagnostics":
                var path = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } file ? Path.GetFullPath(file, root ?? Environment.CurrentDirectory) : null;
                text = path is not null && Outside(path) ? "That file isn't in the workspace."
                    : path is not null && await Excluded(path).ConfigureAwait(true) ? "That file is excluded from AI."
                    : DiagnosticsText(path, agent is null ? null : Outside);
                break;
            case "get_build_output":
                text = BuildOutputText();
                break;
            case "build":
                var choice = await Dialogs.ShowAsync(new DialogRequest(
                    Localizer.Get("msg.ai.mcpBuildTitle", "Build the solution?"),
                    Localizer.Get("msg.ai.mcpBuildMessage", "An AI agent outside the IDE (codalus mcp) asks to build the solution."),
                    [Localizer.Get("msg.ai.build", "Build"), Localizer.Get("msg.ai.reject", "Reject")])
                {
                    DefaultButton = Localizer.Get("msg.ai.reject", "Reject"),
                }).ConfigureAwait(true);
                if (choice.Button != Localizer.Get("msg.ai.build", "Build"))
                {
                    return "The user didn't allow the build.";
                }

                var succeeded = await BuildAsync("build", null).ConfigureAwait(true);
                text = (succeeded ? "The build succeeded." : "The build failed.") + "\n" + DiagnosticsText(null);
                break;
            default:
                throw new McpException($"There is no tool '{tool}'.");
        }

        Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI").AppendLine(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} MCP server{(agent is null ? "" : " (external agent " + agent + ")")}: {tool}"));
        return SecretRedactor.Redact(text).Text;
    }
}
