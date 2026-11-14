using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Debugging;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Search;
using Codalus.Workbench.Testing;
using BuildTargets = Codalus.Projects.Protocol.BuildTargets;
using TextFiles = Codalus.Shell.Editor.TextFiles;
using WorkspacePaths = Codalus.Workbench.Ai.Agent.WorkspacePaths;

namespace Codalus.Shell;

/// <summary>
/// The IDE's tools for agents (ADR 0092): one dispatcher for <c>codalus mcp</c> (an external agent's session pipe and the
/// general pipe) and AI Chat's built-in agent. Reading tools answer from the language services, Code Search's index, the
/// Error List, Test Explorer and the debugger; actions (build, tests, run, debug, the browser) follow
/// <c>ai.agent.ideActions</c>; plugins' tools ask every time. Paths are confined to the workspace, excluded files are
/// left out, and every answer is redacted and logged to the AI Output channel (the tool and the caller, never content).
/// </summary>
public sealed partial class ShellHost
{
    private const int MaxIdeToolText = 30_000;

    /// <summary>How long the run and browser tools wait for a web app that is starting to listen.</summary>
    private static readonly TimeSpan s_webAppWait = TimeSpan.FromSeconds(90);

    /// <summary>How long debug_start waits for the launch (the build, the launch profile, the debugger, the page).</summary>
    private static readonly TimeSpan s_debugLaunchWait = TimeSpan.FromMinutes(10);

    private readonly HashSet<string> _ideActionGrants = new(StringComparer.Ordinal);
    private AgentBrowser? _agentBrowser;
    private string? _debugAppUrl;
    private string? _debugEnded;

    /// <summary>The launch debug_start or run_project started, while it runs: the app is starting.</summary>
    private Task? _agentLaunch;

    /// <summary>
    /// The address the program being debugged was told to listen on (its launch profile's <c>applicationUrl</c>), set
    /// at every debug launch: a web app is expected, even when its logging filters "Now listening on".
    /// </summary>
    private string? _debugLaunchUrl;

    /// <summary>
    /// The profile of the browser the debug session's page runs in, which has the DevTools Protocol on a port, while the
    /// session has a page: the agent's browser tools drive that page, so its breakpoints stop (ADR 0092, 2026-11-05).
    /// </summary>
    private string? _debugBrowserProfile;

    /// <summary>The address a program run_project started was told to listen on, by program.</summary>
    private (RunningProgram Program, string? Url)? _runLaunchUrl;

    /// <summary>Every tool an agent sees: the built-in ones, then the plugins'.</summary>
    public IReadOnlyList<IdeToolDefinition> IdeToolDefinitions => [.. IdeTools.BuiltIn, .. PluginIdeTools()];

    /// <summary>The browser agents drive, while it runs (tests).</summary>
    internal AgentBrowser? AgentBrowser => _agentBrowser;

    private void InitializeIdeTools()
    {
        // "Allow for this chat" lasts as long as the chat: New Chat, another workspace and AI turned off forget it.
        AiChat.Cleared += (_, _) =>
        {
            ForgetIdeActionGrants();
            _ = StopAgentBrowserAsync();
        };
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            ForgetIdeActionGrants();
            _ = StopAgentBrowserAsync();
        });

        // How the last session ended, for a wait that starts after it did.
        Debugger.Ended += (_, code) =>
        {
            _debugEnded = code is { } exit ? string.Create(CultureInfo.InvariantCulture, $"The program ended (exit code {exit}).") : "The program ended.";
            _debugBrowserProfile = null;
        };
        Debugger.StateChanged += (_, _) =>
        {
            if (Debugger.State == DebugState.Starting)
            {
                _debugEnded = null;
                _debugAppUrl = null;
            }
        };

        // The address a web app under the debugger listens on, for the agent's browser.
        Debugger.Output += (_, output) =>
        {
            if (ListeningUrl().Match(output.Text) is { Success: true } match)
            {
                _debugAppUrl = LocalUrl(match.Groups["url"].Value.TrimEnd('/', '.', ',', ';', ')'));
            }
        };
    }

    private void ForgetIdeActionGrants()
    {
        lock (_ideActionGrants)
        {
            _ideActionGrants.Clear();
        }
    }

    /// <summary>Stops the agent's browser (New Chat, another workspace, exit).</summary>
    internal async Task StopAgentBrowserAsync()
    {
        if (Interlocked.Exchange(ref _agentBrowser, null) is { } browser)
        {
            await browser.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Without quotes: a Serilog template without :lj renders the address quoted.
    [GeneratedRegex(@"Now listening on:\s*[""']?(?<url>https?://[^\s""'<>]+)")]
    private static partial Regex ListeningUrl();

    /// <summary>The first address of a launch's <c>ASPNETCORE_URLS</c> (a web app's), with a wildcard host as localhost.</summary>
    internal static string? WebAppUrl(Codalus.Workbench.Build.LaunchPlan plan) =>
        plan.Environment.TryGetValue("ASPNETCORE_URLS", out var urls) && Codalus.Workbench.Build.LaunchPlanner.BrowserUrl(urls, null) is { } url
            ? LocalUrl(url)
            : null;

    private static string LocalUrl(string url) =>
        url.Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal).Replace("://[::]", "://localhost", StringComparison.Ordinal);

    /// <summary>
    /// A call of an IDE tool (on the UI thread). <paramref name="caller"/> names who asks: an external agent, the built-in
    /// assistant, or null for <c>codalus mcp</c>'s general pipe; it keys "Allow for this chat" and the log.
    /// </summary>
    internal async Task<IdeToolResult> IdeToolAsync(string tool, JsonElement args, string? caller, CancellationToken cancellationToken)
    {
        if (tool == McpBridge.ListToolsCall)
        {
            return new IdeToolResult(IdeTools.ToJson(IdeToolDefinitions));
        }

        var definition = IdeToolDefinitions.FirstOrDefault(t => t.Name == tool) ?? throw new McpException($"There is no tool '{tool}'.");
        var root = Solutions.Solution?.Directory;
        var started = System.Diagnostics.Stopwatch.StartNew();
        IdeToolResult result;
        try
        {
            if (HeadlessRefusal(definition) is { } refusal)
            {
                result = IdeToolResult.Error(refusal); // codalus mcp --folder: nobody to ask (M60b)
            }
            else if (definition.Kind != IdeToolKind.Read && !await ApproveIdeToolAsync(definition, args, caller, cancellationToken).ConfigureAwait(true))
            {
                result = IdeToolResult.Error("The user didn't allow it. Don't try again; ask what they want instead.");
            }
            else
            {
                result = definition.Source == IdeTools.CodalusSource
                    ? await BuiltInIdeToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true)
                    : await CallPluginIdeToolAsync(definition, args, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (IdeToolException ex)
        {
            result = IdeToolResult.Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            result = IdeToolResult.Error(ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
        {
            result = IdeToolResult.Error("The arguments don't match the tool's schema: " + ex.Message);
        }
        catch (Exception ex) when ((ex is IOException or TimeoutException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException) && !cancellationToken.IsCancellationRequested)
        {
            result = IdeToolResult.Error(ex.Message);
        }

        Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI").AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} IDE tool ({caller ?? "codalus mcp"}): {tool}{(result.IsError ? " → error" : "")} ({started.Elapsed.TotalSeconds:0.0} s)"));
        var text = SecretRedactor.Redact(result.Text).Text;
        return result with { Text = text.Length > MaxIdeToolText ? text[..MaxIdeToolText] + "\n… (cut at " + MaxIdeToolText.ToString(CultureInfo.InvariantCulture) + " characters)" : text };
    }

    /// <summary>
    /// The workspaces whose actions run without asking ("Always Allow in This Workspace"), in the user's state, never in
    /// the repository, so a cloned repository can't allow itself.
    /// </summary>
    internal Codalus.Workbench.Projects.TrustStore IdeActionWorkspaces => _ideActionWorkspaces ??= new(Path.Combine(Paths.State, "ai-ide-actions-allowed.json"));

    private Codalus.Workbench.Projects.TrustStore? _ideActionWorkspaces;

    /// <summary>
    /// Asks before an action or a plugin's tool. Actions follow <c>ai.agent.ideActions</c>: <c>allow</c> (the default) runs
    /// them; <c>ask</c> asks every time and <c>askOncePerSession</c> once per chat ("Allow for This Chat"), and both offer
    /// "Always Allow in This Workspace". A plugin's tool asks every time, as an MCP server's does (ADR 0063).
    /// </summary>
    private async Task<bool> ApproveIdeToolAsync(IdeToolDefinition definition, JsonElement args, string? caller, CancellationToken cancellationToken)
    {
        var grantKey = caller ?? "codalus mcp";
        // Only the user's settings decide (a cloned repository's settings can't allow actions for itself).
        var userValue = Workbench.Settings.GetLayerValue(IdeTools.ActionsSetting, Codalus.Workbench.Settings.SettingScope.User);
        var isAction = definition.Kind == IdeToolKind.Action;
        var approval = isAction
            ? IdeTools.ParseApproval(userValue is { ValueKind: JsonValueKind.String } value ? value.GetString() : null)
            : IdeActionApproval.Ask;
        if (approval == IdeActionApproval.Allow)
        {
            return true;
        }

        var workspace = Solutions.Solution?.Directory;
        if (isAction && workspace is not null && IdeActionWorkspaces.IsTrusted(workspace))
        {
            return true;
        }

        lock (_ideActionGrants)
        {
            if (approval == IdeActionApproval.AskOncePerSession && _ideActionGrants.Contains(grantKey))
            {
                return true;
            }
        }

        var allow = Localizer.Get("msg.ideTools.allow", "Allow");
        var allowChat = Localizer.Get("msg.ideTools.allowForChat", "Allow for This Chat");
        var allowWorkspace = Localizer.Get("msg.ideTools.allowForWorkspace", "Always Allow in This Workspace");
        var reject = Localizer.Get("msg.ai.reject", "Reject");
        var who = caller ?? Localizer.Get("msg.ideTools.anAgent", "An AI agent (codalus mcp)");
        var details = new List<DetailLine> { new(definition.Name, "header") };
        if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Any())
        {
            details.AddRange(args.EnumerateObject().Select(p => new DetailLine(p.Name + ": " + SecretRedactor.Redact(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()).Text)));
        }

        if (definition.Kind == IdeToolKind.Plugin)
        {
            details.Add(new DetailLine(Localizer.Format("msg.ideTools.fromPlugin", "A tool of the plugin {0}.", definition.Source), "note"));
        }

        var buttons = new List<string> { allow };
        if (approval == IdeActionApproval.AskOncePerSession)
        {
            buttons.Add(allowChat);
        }

        if (isAction && workspace is not null)
        {
            buttons.Add(allowWorkspace);
        }

        buttons.Add(reject);
        var choice = await ShowCancellableAsync(new DialogRequest(
            Localizer.Format("msg.ideTools.approveTitle", "Allow {0}?", definition.Name),
            Localizer.Format("msg.ideTools.approveMessage", "{0} asks the IDE to {1}", who, ActionDescription(definition)),
            [.. buttons])
        {
            DefaultButton = reject,
            Details = details,
            Icon = "shield",
        }, cancellationToken).ConfigureAwait(true);
        if (choice.Button == allowChat)
        {
            lock (_ideActionGrants)
            {
                _ideActionGrants.Add(grantKey);
            }

            return true;
        }

        if (choice.Button == allowWorkspace && workspace is not null)
        {
            IdeActionWorkspaces.Trust(workspace);
            return true;
        }

        return choice.Button == allow;
    }

    private static string ActionDescription(IdeToolDefinition definition) => definition.Name switch
    {
        "build" => Localizer.Get("msg.ideTools.doBuild", "build the solution."),
        "run_tests" => Localizer.Get("msg.ideTools.doRunTests", "run tests."),
        "run_project" => Localizer.Get("msg.ideTools.doRun", "start the program."),
        "stop_run" => Localizer.Get("msg.ideTools.doStop", "stop the program."),
        "debug_start" => Localizer.Get("msg.ideTools.doDebug", "start debugging."),
        "debug_evaluate" => Localizer.Get("msg.ideTools.doEvaluate", "evaluate an expression in the debugged program (it can call methods)."),
        "debug_step" or "debug_continue" => Localizer.Get("msg.ideTools.doStep", "step or continue the debugged program."),
        "browser_evaluate" => Localizer.Get("msg.ideTools.doScript", "run JavaScript in the web app's page."),
        "browser_navigate" => Localizer.Get("msg.ideTools.doNavigate", "open an address in the web app's browser."),
        _ => Localizer.Format("msg.ideTools.doTool", "run {0}.", definition.Name),
    };

    private async Task<IdeToolResult> BuiltInIdeToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        switch (tool)
        {
            case "get_solution":
                return new(SolutionText());
            case "get_open_files":
                return new(await OpenFilesTextAsync(root, cancellationToken).ConfigureAwait(true));
            case "get_diagnostics":
            {
                var path = OptionalPath(args, "path", root);
                return new(path is not null && await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true) ? "That file is excluded from AI." : DiagnosticsText(path, p => !IsInWorkspace(p, root)));
            }

            case "get_build_output":
                return new(BuildOutputText());
            case "find_symbols":
                return new(await FindSymbolsTextAsync(args, root, cancellationToken).ConfigureAwait(true));
            case "search_code":
                return new(await SearchCodeTextAsync(args, root, cancellationToken).ConfigureAwait(true));
            case "get_document_outline":
                return new(await OutlineTextAsync(args, root, cancellationToken).ConfigureAwait(true));
            case "get_symbol_info":
            case "go_to_definition":
            case "find_references":
            case "find_implementations":
            case "call_hierarchy":
            case "type_hierarchy":
                return new(await NavigationTextAsync(tool, args, root, cancellationToken).ConfigureAwait(true));
            case "build":
                return await BuildToolAsync(args).ConfigureAwait(true);
            case "run_tests":
                return await RunTestsToolAsync(args, root).ConfigureAwait(true);
            case "get_test_results":
                return new(TestResultsText(Bool(args, "failedOnly") ?? true, null, root));
            case "run_project":
                return await RunProjectToolAsync(args, cancellationToken).ConfigureAwait(true);
            case "get_run_output":
                return new(RunOutputText(Int(args, "lines") ?? 200));
            case "stop_run":
                return await StopRunToolAsync().ConfigureAwait(true);
            case "debug_start":
                return await DebugStartToolAsync(args, root, cancellationToken).ConfigureAwait(true);
            case "debug_set_breakpoint":
            {
                var path = RequiredPath(args, "path", root);
                var line = Int(args, "line") ?? throw new IdeToolException("A line is needed.");
                var hit = Int(args, "hitCount");
                Debugger.Breakpoints.Set(new BreakpointSpec(path, line, Str(args, "condition") is { Length: > 0 } c ? c : null, hit is > 0 ? hit.Value.ToString(CultureInfo.InvariantCulture) : null));
                return new(string.Create(CultureInfo.InvariantCulture, $"Breakpoint set at {Relative(path, root)}:{line}."));
            }

            case "debug_remove_breakpoint":
            {
                if (OptionalPath(args, "path", root) is not { } path)
                {
                    Debugger.Breakpoints.Clear();
                    return new("Every breakpoint removed.");
                }

                var line = Int(args, "line") ?? throw new IdeToolException("A line is needed.");
                if (Debugger.Breakpoints.At(path, line) is not { } breakpoint)
                {
                    return IdeToolResult.Error("There is no breakpoint there.");
                }

                Debugger.Breakpoints.Remove(breakpoint);
                return new("Breakpoint removed.");
            }

            case "debug_list_breakpoints":
                return new(Debugger.Breakpoints.All.Count == 0 ? "No breakpoints." : string.Join('\n', Debugger.Breakpoints.All.Select(b =>
                    string.Create(CultureInfo.InvariantCulture, $"{Relative(b.Path, root)}:{b.Line}{(b.Condition is { } c ? " when " + c : "")}{(b.HitCondition is { } h ? " hit " + h : "")}{(b.Enabled ? "" : " (disabled)")}{(Debugger.Breakpoints.StateOf(b) is { Verified: false } s ? " (not bound" + (s.Message is { } m ? ": " + m : "") + ")" : "")}"))));
            case "debug_wait_for_stop":
                return new(await WaitForStopTextAsync(TimeSpan.FromSeconds(Math.Clamp(Int(args, "timeoutSeconds") ?? 30, 1, 600)), root, cancellationToken).ConfigureAwait(true));
            case "debug_get_state":
                return new(await DebugStateTextAsync(root).ConfigureAwait(true));
            case "debug_get_variables":
                return new(await VariablesTextAsync(Int(args, "frame") ?? 0, Str(args, "expand"), cancellationToken).ConfigureAwait(true));
            case "debug_evaluate":
            {
                var frame = StoppedFrame(Int(args, "frame") ?? 0);
                var value = await Debugger.EvaluateInFrameAsync(Str(args, "expression") ?? throw new IdeToolException("An expression is needed."), frame.Id, cancellationToken).ConfigureAwait(true);
                return new(value.Result + (value.Type is { } type ? "  (" + type + ")" : ""));
            }

            case "debug_step":
            {
                StoppedFrame(0);
                await (Str(args, "kind") switch
                {
                    "into" => Debugger.StepIntoAsync(),
                    "out" => Debugger.StepOutAsync(),
                    _ => Debugger.StepOverAsync(),
                }).ConfigureAwait(true);
                return new(await WaitForStopTextAsync(TimeSpan.FromSeconds(30), root, cancellationToken).ConfigureAwait(true));
            }

            case "debug_continue":
                StoppedFrame(0);
                await Debugger.ContinueAsync().ConfigureAwait(true);
                return new("Continued. Use debug_wait_for_stop to wait for the next stop.");
            case "browser_get_page":
                return new((await BrowserCallAsync(null, (b, ct) => b.GetPageAsync(Str(args, "selector"), ct), root, cancellationToken).ConfigureAwait(true)).Text);
            case "browser_screenshot":
            {
                var shot = await BrowserCallAsync(null, (b, ct) => b.ScreenshotAsync(ct), root, cancellationToken).ConfigureAwait(true);
                return shot.Interrupted ? new(shot.Text) : new IdeToolResult("A screenshot of the page.") { Images = [new IdeToolImage(shot.Text, "image/png")] };
            }

            case "browser_console":
            {
                var (browser, _) = await BrowserAsync(cancellationToken).ConfigureAwait(true);
                var lines = browser.Console(Math.Clamp(Int(args, "lines") ?? 100, 1, 500));
                return new(lines.Count == 0 ? "No console messages." : string.Join('\n', lines));
            }

            case "browser_evaluate":
            {
                var script = Str(args, "script") ?? throw new IdeToolException("A script is needed.");
                // A function body (with return or await) runs as an async function; an expression as it is.
                var expression = script.Contains("return", StringComparison.Ordinal) || script.Contains(';', StringComparison.Ordinal) ? "(async () => {" + script + "\n})()" : script;
                return new((await BrowserCallAsync(null, (b, ct) => b.EvaluateAsync(expression, ct), root, cancellationToken).ConfigureAwait(true)).Text);
            }

            case "browser_navigate":
            {
                var target = Str(args, "url") ?? throw new IdeToolException("An address is needed.");
                var page = await BrowserCallAsync(target, (b, ct) => b.GetPageAsync(null, ct), root, cancellationToken).ConfigureAwait(true);
                return new(page.Interrupted ? page.Text : "Opened. " + page.Text.Split('\n', 3) switch { [var url, var title, ..] => url + " — " + title, var other => string.Join(' ', other) });
            }

            default:
                return await Round2IdeToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true)
                    ?? throw new McpException($"There is no tool '{tool}'.");
        }
    }

    // ------------------------------------------------------------------ the solution and the editor

    private string SolutionText()
    {
        if (Solutions.Solution is not { } solution)
        {
            return "No solution is open.";
        }

        var sb = new StringBuilder().Append("Solution: ").AppendLine(solution.Path).Append("Trusted: ").AppendLine(solution.Trusted ? "yes" : "no");
        if (Runs.StartupProject is { } startup)
        {
            sb.Append("Startup project: ").AppendLine(Path.GetFileNameWithoutExtension(startup));
        }

        foreach (var project in Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- ").Append(project.Name).Append(" (").Append(Relative(project.Path, solution.Directory)).Append("): ").Append(string.Join(", ", project.TargetFrameworks));
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

        return sb.ToString();
    }

    private async Task<string> OpenFilesTextAsync(string? root, CancellationToken cancellationToken)
    {
        var open = new StringBuilder();
        foreach (var editor in Editors.Select(e => e.Model).Distinct())
        {
            if (IsInWorkspace(editor.Path, root) && !await IsExcludedAsync(editor.Path, root, cancellationToken).ConfigureAwait(true))
            {
                open.Append(ReferenceEquals(editor, ActiveEditor?.Model) ? "* " : "- ").Append(Relative(editor.Path, root));
                if (ReferenceEquals(editor, ActiveEditor?.Model) && ActiveEditor?.Controller?.Editor is { } active)
                {
                    open.Append(" (line ").Append(active.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                open.AppendLine();
            }
        }

        if (ActiveEditor?.Controller?.Editor is { SelectionLength: > 0 } selected && ActiveEditor.Model is { } model && IsInWorkspace(model.Path, root) && !await IsExcludedAsync(model.Path, root, cancellationToken).ConfigureAwait(true))
        {
            open.AppendLine().Append("Selection in ").Append(Relative(model.Path, root)).AppendLine(":").AppendLine(selected.SelectedText);
        }

        return open.Length == 0 ? "No files are open." : open.ToString();
    }

    // ------------------------------------------------------------------ code intelligence

    private async Task<string> FindSymbolsTextAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var text = Str(args, "query") is { Length: > 0 } q ? q : throw new IdeToolException("A query is needed.");
        var limit = Math.Clamp(Int(args, "limit") ?? 30, 1, 200);
        var filter = Str(args, "kind") switch
        {
            "type" => CodeSearchFilter.Types,
            "member" => CodeSearchFilter.Members,
            "file" => CodeSearchFilter.Files,
            _ => CodeSearchFilter.All,
        };
        IReadOnlyList<CodeSearchResult> results = [];
        var incomplete = false;
        await foreach (var batch in ((ICodeSearchHost)this).SearchCodeAsync(new CodeSearchQuery(filter, text), CodeSearchScope.EntireSolution, includeExternal: false, cancellationToken).ConfigureAwait(true))
        {
            results = batch.Results;
            incomplete = batch.Incomplete;
        }

        var sb = new StringBuilder();
        var count = 0;
        foreach (var result in results.Where(r => r.Kind is not (CodeSearchKind.Text or CodeSearchKind.Line)))
        {
            if (result.Path is { } path && (!IsInWorkspace(path, root) || await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true)))
            {
                continue;
            }

            sb.Append(result.Kind.ToString().ToLowerInvariant()).Append(' ');
            if (result.Kind != CodeSearchKind.File && result.SymbolKind > 0)
            {
                sb.Append('(').Append(SymbolKindName(result.SymbolKind)).Append(") ");
            }

            sb.Append(result.Container is { Length: > 0 } container && result.Kind != CodeSearchKind.File ? container + "." + result.Name : result.Name);
            if (result.Path is { } file)
            {
                sb.Append(" — ").Append(Relative(file, root));
                if (result.Kind != CodeSearchKind.File)
                {
                    sb.Append(':').Append((result.Line + 1).ToString(CultureInfo.InvariantCulture));
                }
            }

            if (result.Project is { Length: > 0 } project)
            {
                sb.Append(" [").Append(project).Append(']');
            }

            sb.AppendLine();
            if (++count >= limit)
            {
                break;
            }
        }

        if (count == 0)
        {
            sb.Append("Nothing matches '").Append(text).AppendLine("'.");
        }

        if (incomplete)
        {
            sb.AppendLine("(Incomplete: the solution is still loading or being indexed; try again shortly for every result.)");
        }

        return sb.ToString();
    }

    private async Task<string> SearchCodeTextAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var query = Str(args, "query") is { Length: > 0 } q ? q : throw new IdeToolException("A query is needed.");
        var limit = Math.Clamp(Int(args, "limit") ?? 100, 1, 1000);
        var options = new FindInFilesOptions(query, Bool(args, "matchCase") ?? false, false, Bool(args, "regex") ?? false, FindScopes.EntireSolution, Str(args, "glob") is { Length: > 0 } glob ? glob : FindInFilesOptions.DefaultFileTypes);
        Regex regex;
        try
        {
            regex = options.CreateRegex();
        }
        catch (ArgumentException ex)
        {
            throw new IdeToolException("The regular expression is invalid: " + ex.Message);
        }

        var (files, openTexts) = await FilesToSearchAsync(options, cancellationToken).ConfigureAwait(true);
        var candidates = new List<string>();
        foreach (var file in files)
        {
            if (IsInWorkspace(file, root) && !await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
            {
                candidates.Add(file);
            }
        }

        var (lines, cut) = await Task.Run(async () =>
        {
            var found = new List<string>();
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = openTexts.TryGetValue(file, out var edited) ? edited : await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    continue;
                }

                var number = 0;
                foreach (var line in text.Split('\n'))
                {
                    number++;
                    if (FindResultsPage.MatchLine(file, number, line.TrimEnd('\r'), regex) is not null)
                    {
                        found.Add(string.Create(CultureInfo.InvariantCulture, $"{Relative(file, root)}:{number}: {line.Trim()}"));
                        if (found.Count >= limit)
                        {
                            return (found, true);
                        }
                    }
                }
            }

            return (found, false);
        }, cancellationToken).ConfigureAwait(true);
        return lines.Count == 0 ? "No matches." : string.Join('\n', lines) + (cut ? string.Create(CultureInfo.InvariantCulture, $"\n(Stopped at {limit} lines; narrow the query or the glob.)") : "");
    }

    private async Task<string> OutlineTextAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", root);
        await EnsureReadableAsync(path, root, cancellationToken).ConfigureAwait(true);
        var client = await LanguageClientAsync(path).ConfigureAwait(true);
        var symbols = await client.DocumentSymbolsAsync(DocumentUris.FromPath(path), cancellationToken).ConfigureAwait(true);
        if (symbols.Length == 0)
        {
            return "The file declares nothing the language service reports.";
        }

        var sb = new StringBuilder();
        void Write(DocumentSymbol symbol, int depth)
        {
            sb.Append(' ', depth * 2).Append(SymbolKindName(symbol.Kind)).Append(' ').Append(symbol.Name);
            if (symbol.Detail is { Length: > 0 } detail && detail != symbol.Name)
            {
                sb.Append(" — ").Append(detail);
            }

            sb.Append(" (line ").Append((symbol.SelectionRange.Start.Line + 1).ToString(CultureInfo.InvariantCulture)).AppendLine(")");
            foreach (var child in symbol.Children ?? [])
            {
                Write(child, depth + 1);
            }
        }

        foreach (var symbol in symbols)
        {
            Write(symbol, 0);
        }

        return sb.ToString();
    }

    private async Task<string> NavigationTextAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var (client, path, position) = await ResolvePositionAsync(args, root, cancellationToken).ConfigureAwait(true);
        var uri = DocumentUris.FromPath(path);
        var lines = new LineCache(this);
        switch (tool)
        {
            case "get_symbol_info":
            {
                var hover = await client.HoverAsync(uri, position, cancellationToken).ConfigureAwait(true);
                var details = await client.SymbolDetailsAsync(uri, position, cancellationToken).ConfigureAwait(true);
                var sb = new StringBuilder();
                if (details is not null)
                {
                    sb.Append(details.Kind).Append(' ').AppendLine(details.DisplayName);
                    if (details.AssemblyName is { Length: > 0 } assembly)
                    {
                        sb.Append("Assembly: ").AppendLine(assembly);
                    }
                }

                if (hover?.Contents.Value is { Length: > 0 } value)
                {
                    sb.AppendLine(value);
                }

                return sb.Length == 0 ? "The language service knows nothing at that position." : sb.ToString();
            }

            case "go_to_definition":
            {
                var locations = await client.DefinitionAsync(uri, position, cancellationToken).ConfigureAwait(true);
                if (locations.Length == 0)
                {
                    return "No definition found.";
                }

                var sb = new StringBuilder();
                foreach (var location in locations.Take(20))
                {
                    if (DocumentUris.IsMetadata(location.Uri))
                    {
                        // A referenced assembly's symbol: its metadata as text, around the declaration.
                        var metadata = await client.MetadataTextAsync(location.Uri, cancellationToken).ConfigureAwait(true);
                        sb.Append("In ").Append(metadata?.AssemblyName ?? "a referenced assembly").Append(" (").Append(metadata?.TypeName).AppendLine("):");
                        if (metadata?.Text is { } text)
                        {
                            var all = text.Split('\n');
                            var from = Math.Max(0, location.Range.Start.Line - 5);
                            sb.AppendLine(string.Join('\n', all.Skip(from).Take(40)));
                        }

                        continue;
                    }

                    sb.AppendLine(await lines.FormatAsync(location, root, cancellationToken).ConfigureAwait(true) ?? "(a file outside the workspace or excluded from AI)");
                }

                return sb.ToString();
            }

            case "find_references":
            {
                var limit = Math.Clamp(Int(args, "limit") ?? 200, 1, 2000);
                var result = await client.ReferenceItemsAsync(uri, position, cancellationToken).ConfigureAwait(true);
                if (result is not null)
                {
                    var sb = new StringBuilder().Append("References to ").Append(result.SymbolName).Append(": ").Append(result.Items.Length.ToString(CultureInfo.InvariantCulture)).AppendLine();
                    var shown = 0;
                    foreach (var item in result.Items)
                    {
                        if (DocumentUris.ToPath(item.Location.Uri) is not { } file || !IsInWorkspace(file, root) || await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
                        {
                            continue;
                        }

                        sb.Append(Relative(file, root)).Append(':').Append((item.Location.Range.Start.Line + 1).ToString(CultureInfo.InvariantCulture))
                            .Append(" [").Append(item.Kind).Append(']');
                        if (item.ContainingMember is { Length: > 0 } member)
                        {
                            sb.Append(" in ").Append(member);
                        }

                        sb.Append(": ").AppendLine(item.LineText.Trim());
                        if (++shown >= limit)
                        {
                            sb.AppendLine("(more not shown)");
                            break;
                        }
                    }

                    return sb.ToString();
                }

                // A language service without Codalus's extension: plain references.
                var locations = await client.ReferencesAsync(uri, position, true, cancellationToken).ConfigureAwait(true);
                return await lines.FormatAllAsync(locations.Take(limit), root, "No references found.", cancellationToken).ConfigureAwait(true);
            }

            case "find_implementations":
            {
                var locations = await client.ImplementationAsync(uri, position, cancellationToken).ConfigureAwait(true);
                return await lines.FormatAllAsync(locations.Take(200), root, "No implementations found.", cancellationToken).ConfigureAwait(true);
            }

            case "call_hierarchy":
            {
                var items = await client.PrepareCallHierarchyAsync(uri, position, cancellationToken).ConfigureAwait(true);
                if (items.Length == 0)
                {
                    return "That isn't a method or property the call hierarchy knows.";
                }

                var outgoing = Str(args, "direction") == "outgoing";
                var sb = new StringBuilder().Append(outgoing ? "Calls made by " : "Callers of ").AppendLine(items[0].Detail is { Length: > 0 } d ? d : items[0].Name);
                if (outgoing)
                {
                    foreach (var call in await client.OutgoingCallsAsync(items[0], cancellationToken).ConfigureAwait(true))
                    {
                        sb.AppendLine(await HierarchyLineAsync(call.To.Name, call.To.Detail, call.To.Uri, call.To.SelectionRange, root, cancellationToken).ConfigureAwait(true));
                    }
                }
                else
                {
                    foreach (var call in await client.IncomingCallsAsync(items[0], cancellationToken).ConfigureAwait(true))
                    {
                        var at = call.FromRanges.Length > 0 ? call.FromRanges[0] : call.From.SelectionRange;
                        sb.AppendLine(await HierarchyLineAsync(call.From.Name, call.From.Detail, call.From.Uri, at, root, cancellationToken).ConfigureAwait(true));
                    }
                }

                return sb.ToString();
            }

            case "type_hierarchy":
            {
                var items = await client.PrepareTypeHierarchyAsync(uri, position, cancellationToken).ConfigureAwait(true);
                if (items.Length == 0)
                {
                    return "That isn't a type the type hierarchy knows.";
                }

                var subtypes = Str(args, "direction") == "subtypes";
                var related = subtypes
                    ? await client.SubtypesAsync(items[0], cancellationToken).ConfigureAwait(true)
                    : await client.SupertypesAsync(items[0], cancellationToken).ConfigureAwait(true);
                var sb = new StringBuilder().Append(subtypes ? "Types deriving from " : "Base types and interfaces of ").AppendLine(items[0].Detail is { Length: > 0 } d ? d : items[0].Name);
                foreach (var item in related)
                {
                    sb.AppendLine(await HierarchyLineAsync(item.Name, item.Detail, item.Uri, item.SelectionRange, root, cancellationToken).ConfigureAwait(true));
                }

                return related.Length == 0 ? sb.AppendLine("(none)").ToString() : sb.ToString();
            }

            default:
                throw new McpException($"There is no tool '{tool}'.");
        }
    }

    private async Task<string> HierarchyLineAsync(string name, string? detail, string uri, Codalus.LanguageServices.Protocol.Range range, string? root, CancellationToken cancellationToken)
    {
        var label = detail is { Length: > 0 } && detail != name ? name + " — " + detail : name;
        if (DocumentUris.ToPath(uri) is { } file && IsInWorkspace(file, root) && !await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
        {
            return "- " + label + " (" + Relative(file, root) + ":" + (range.Start.Line + 1).ToString(CultureInfo.InvariantCulture) + ")";
        }

        return "- " + label + (DocumentUris.IsMetadata(uri) ? " (metadata)" : "");
    }

    /// <summary>
    /// The language service and position a navigation tool works at: a symbol by name (Code Search's index, then the
    /// language services' <c>workspace/symbol</c>), or a path, a 1-based line and the identifier on it.
    /// </summary>
    private async Task<(LanguageHostClient Client, string Path, Position Position)> ResolvePositionAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (Str(args, "symbol") is { Length: > 0 } symbol && Str(args, "path") is not { Length: > 0 })
        {
            var (path, line, column) = await FindSymbolAsync(symbol, root, cancellationToken).ConfigureAwait(true);
            await EnsureReadableAsync(path, root, cancellationToken).ConfigureAwait(true);
            return (await LanguageClientAsync(path).ConfigureAwait(true), path, new Position(line, column));
        }

        var file = RequiredPath(args, "path", root);
        await EnsureReadableAsync(file, root, cancellationToken).ConfigureAwait(true);
        var number = Int(args, "line") ?? throw new IdeToolException("A line is needed with a path (or give a symbol).");
        var text = await new LineCache(this).LineAsync(file, number - 1, cancellationToken).ConfigureAwait(true) ?? throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"{Relative(file, root)} has no line {number}."));
        var name = Str(args, "name") ?? Str(args, "symbol");
        var character = -1;
        if (name is { Length: > 0 })
        {
            var simple = name[(name.LastIndexOf('.') + 1)..];
            var match = Regex.Match(text, @"(?<![\w@])@?" + Regex.Escape(simple) + @"(?!\w)");
            character = match.Success ? match.Index : text.IndexOf(simple, StringComparison.Ordinal);
            if (character < 0)
            {
                throw new IdeToolException($"'{simple}' isn't on line {number.ToString(CultureInfo.InvariantCulture)} of {Relative(file, root)}: {text.Trim()}");
            }
        }
        else
        {
            var identifier = Regex.Match(text, @"[A-Za-z_]\w*");
            character = identifier.Success ? identifier.Index : 0;
        }

        return (await LanguageClientAsync(file).ConfigureAwait(true), file, new Position(number - 1, character));
    }

    private async Task<(string Path, int Line, int Column)> FindSymbolAsync(string symbol, string? root, CancellationToken cancellationToken)
    {
        var parts = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1].Split('(', '<')[0];
        var container = parts.Length > 1 ? string.Join('.', parts[..^1]) : null;
        bool Matches(string candidate, string? candidateContainer) =>
            candidate == name && (container is null || (candidateContainer is { } c && (c == container || c.EndsWith("." + container, StringComparison.Ordinal))));

        if (CodeSearchIndex.IsLoaded)
        {
            var found = CodeSearchIndex.Search(new CodeSearchQuery(CodeSearchFilter.All, name), CodeSearchRankContext.Empty, null, 200, cancellationToken)
                .FirstOrDefault(r => r.Path is not null && Matches(r.Name, r.Container));
            if (found is { Path: { } path })
            {
                return (path, found.Line, found.Column);
            }
        }

        foreach (var (_, client) in Languages.RunningClients())
        {
            if (client.Capabilities?.WorkspaceSymbolProvider is null)
            {
                continue;
            }

            var symbols = await client.WorkspaceSymbolsAsync(name, cancellationToken).ConfigureAwait(true);
            var hit = symbols.FirstOrDefault(s => Matches(s.Name, s.ContainerName) && DocumentUris.ToPath(s.Location.Uri) is not null);
            if (hit is not null)
            {
                return (DocumentUris.ToPath(hit.Location.Uri)!, hit.Location.Range.Start.Line, hit.Location.Range.Start.Character);
            }
        }

        throw new IdeToolException($"No symbol named '{symbol}' in the solution. Try find_symbols with part of the name.");
    }

    private async Task<LanguageHostClient> LanguageClientAsync(string path)
    {
        var language = Languages.LanguageIdOf(path) ?? throw new IdeToolException($"No language service handles {Path.GetFileName(path)}.");
        return await Languages.ClientAsync(language).ConfigureAwait(true) ?? throw new IdeToolException($"The {language} language service isn't running.");
    }

    private static string SymbolKindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property", 8 => "field",
        9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable", 14 => "constant", 15 => "string",
        16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key", 21 => "null", 22 => "enum member",
        23 => "struct", 24 => "event", 25 => "operator", 26 => "type parameter", _ => "symbol",
    };

    /// <summary>Lines of files as the editor has them (unsaved edits) or on the disk, read once per call.</summary>
    private sealed class LineCache(ShellHost shell)
    {
        private readonly Dictionary<string, string[]?> _files = new(StringComparer.Ordinal);

        public async Task<string?> LineAsync(string path, int line, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(path, out var lines))
            {
                var text = shell.OpenTextOf(path);
                if (text is null && File.Exists(path))
                {
                    text = (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Text;
                }

                _files[path] = lines = text?.Split('\n');
            }

            return lines is not null && line >= 0 && line < lines.Length ? lines[line].TrimEnd('\r') : null;
        }

        public async Task<string?> FormatAsync(Location location, string? root, CancellationToken cancellationToken)
        {
            if (DocumentUris.ToPath(location.Uri) is not { } file || !IsInWorkspace(file, root) || await shell.IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
            {
                return null;
            }

            var line = location.Range.Start.Line;
            return Relative(file, root) + ":" + (line + 1).ToString(CultureInfo.InvariantCulture) + ": " + ((await LineAsync(file, line, cancellationToken).ConfigureAwait(true))?.Trim() ?? "");
        }

        public async Task<string> FormatAllAsync(IEnumerable<Location> locations, string? root, string none, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            foreach (var location in locations)
            {
                if (await FormatAsync(location, root, cancellationToken).ConfigureAwait(true) is { } line)
                {
                    sb.AppendLine(line);
                }
            }

            return sb.Length == 0 ? none : sb.ToString();
        }
    }

    // ------------------------------------------------------------------ build and tests

    private async Task<IdeToolResult> BuildToolAsync(JsonElement args)
    {
        var project = Str(args, "project") is { Length: > 0 } name ? ProjectPath(name) : null;
        if (Builds.IsBuilding)
        {
            return IdeToolResult.Error("A build is running already; wait for it, then read get_build_output.");
        }

        var succeeded = await BuildAsync(BuildTargets.Build, project).ConfigureAwait(true);
        var last = Builds.LastBuild?.Result;
        var summary = last is null ? (succeeded ? "The build succeeded." : "The build didn't run (no solution, an untrusted folder, or it was cancelled).")
            : string.Create(CultureInfo.InvariantCulture, $"The build {(last.Canceled ? "was cancelled" : last.Succeeded ? "succeeded" : "failed")}: {last.Errors} error(s), {last.Warnings} warning(s), {last.ElapsedMilliseconds / 1000.0:0.#} s.");
        var root = Solutions.Solution?.Directory;
        var diagnostics = Builds.Diagnostics.Where(d => d.File is null || IsInWorkspace(d.File, root)).Take(200).Select(d =>
            string.Create(CultureInfo.InvariantCulture, $"{(d.File is { } f ? Relative(f, root) : d.Project ?? "")}({d.Line},{d.Column}): {d.Severity} {d.Code}: {d.Message}"));
        return new IdeToolResult(summary + "\n" + string.Join('\n', diagnostics), IsError: !succeeded);
    }

    private async Task<IdeToolResult> RunTestsToolAsync(JsonElement args, string? root)
    {
        if (Tests.IsRunning)
        {
            return IdeToolResult.Error("Tests are running already; wait for them, then read get_test_results.");
        }

        ActivateTestProviders();
        List<string>? keys = null;
        var project = Str(args, "project");
        var filter = Str(args, "filter");
        var failedOnly = Bool(args, "failedOnly") ?? false;
        var selective = project is { Length: > 0 } || filter is { Length: > 0 } || failedOnly;
        var parsed = TestFilter.Parse(string.Join(' ', new[]
        {
            project is { Length: > 0 } ? "project:" + (project.Contains(' ', StringComparison.Ordinal) ? "\"" + project + "\"" : project) : null,
            failedOnly ? "outcome:failed" : null,
        }.Where(t => t is not null)));

        // Discovery fills the tree a project at a time: wait (a minute at most) for the tests asked for to appear.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var found = Tests.Read(r => r.SelectMany(n => n.Leaves())
                .Where(l => parsed.Matches(l) && (filter is not { Length: > 0 } || l.FullLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.Key).ToList());
            if (found.Count > 0 || DateTime.UtcNow > deadline || (failedOnly && Tests.Read(r => r.Sum(n => n.Leaves().Count())) > 0))
            {
                keys = selective ? found : null;
                break;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        if (keys is { Count: 0 })
        {
            return IdeToolResult.Error("No test matches. get_test_results lists the tests the IDE knows.");
        }

        var started = DateTimeOffset.Now;
        await RunTestsAsync(TestRunKind.Run, keys).ConfigureAwait(true);

        // The last results can land just after the run's task ends.
        for (var i = 0; i < 600 && Tests.IsRunning; i++)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        return new(TestResultsText(failedOnly: true, since: started, root, keys));
    }

    private string TestResultsText(bool failedOnly, DateTimeOffset? since, string? root, IReadOnlyList<string>? keys = null)
    {
        var include = keys is null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        return Tests.Read(roots =>
        {
            var leaves = roots.SelectMany(n => n.Leaves()).Where(l => include is null || include.Contains(l.Key)).ToList();
            if (leaves.Count == 0)
            {
                return "The IDE knows no tests yet (build the solution, or wait for discovery).";
            }

            var ran = leaves.Where(l => l.Result is { } r && (since is null || r.Finished >= since)).ToList();
            var sb = new StringBuilder();
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{leaves.Count} test(s); {(since is null ? "last results" : "this run")}: "))
                .Append(string.Join(", ", ran.GroupBy(l => l.Result!.Outcome).OrderBy(g => g.Key).Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key.Name().ToLowerInvariant())));
            if (ran.Count == 0)
            {
                sb.Append("none ran");
            }

            sb.AppendLine();
            foreach (var leaf in (failedOnly ? ran.Where(l => l.Result!.Outcome.IsFailure()) : ran).Take(100))
            {
                var result = leaf.Result!;
                sb.Append(result.Outcome.IsFailure() ? "FAILED " : result.Outcome.Name().ToLowerInvariant() + " ").Append(leaf.FullLabel);
                if (result.Duration is { } duration)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" ({duration.TotalMilliseconds:0} ms)"));
                }

                sb.AppendLine();
                foreach (var message in result.Messages.Take(3))
                {
                    sb.Append("  ").AppendLine(message.Message.Trim());
                    if (message.Expected is { } expected)
                    {
                        sb.Append("  expected: ").AppendLine(expected);
                    }

                    if (message.Actual is { } actual)
                    {
                        sb.Append("  actual: ").AppendLine(actual);
                    }

                    if (message.StackTrace is { Length: > 0 } stack)
                    {
                        sb.AppendLine(string.Join('\n', stack.Split('\n').Take(15).Select(l => "    " + l.Trim())));
                    }
                }
            }

            return sb.ToString();
        });
    }

    // ------------------------------------------------------------------ running

    private async Task<IdeToolResult> RunProjectToolAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var project = Str(args, "project") is { Length: > 0 } name ? ProjectPath(name) : null;
        var before = LastRunning();
        var offScreen = Bool(args, "offScreen") == true ? await PrepareOffScreenAsync(cancellationToken).ConfigureAwait(true) + "\n" : "";
        var launch = StartWithoutDebuggingAsync(project);
        _agentLaunch = launch;
        try
        {
            await launch.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _offScreenLaunch = null;
        }
        if (LastRunning() is not { } running || running == before)
        {
            return IdeToolResult.Error("It didn't start. " + LaunchFailureText());
        }

        // A web app: until it listens (or ends). Anything else: a moment, to see a failure at its start.
        var expected = await RunLaunchUrlAsync(running).ConfigureAwait(true);
        string? app = null;
        if (expected is not null)
        {
            app = await WebAppAsync(s_webAppWait, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            await Task.WhenAny(running.Exited, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(true);
        }

        var state = running.HasExited ? "It started and has exited." + (running.Exited.IsCompletedSuccessfully ? string.Create(CultureInfo.InvariantCulture, $" (exit code {running.Exited.Result})") : "")
            : app is not null ? "Started. The web app listens at " + app + "; browser_* opens it (a path such as /counter is relative to it)."
            : expected is not null ? string.Create(CultureInfo.InvariantCulture, $"Started, but nothing listens at {expected} after {s_webAppWait.TotalSeconds:0} s: read the output below.")
            : "Started.";
        return new(state + "\n" + offScreen + RunOutputText(40));
    }

    /// <summary>The address <paramref name="program"/> was told to listen on (its launch profile's), found once.</summary>
    private async Task<string?> RunLaunchUrlAsync(RunningProgram program)
    {
        if (_runLaunchUrl is { } known && known.Program == program)
        {
            return known.Url;
        }

        string? url = null;
        try
        {
            var (plan, _, _) = await Runs.PlanAsync(program.Project, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            url = WebAppUrl(plan);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            // Not a project with a launch profile: no address is expected.
        }

        _runLaunchUrl = (program, url);
        return url;
    }

    /// <summary>Why a launch didn't start: the build's errors, or the end of the run or debug output.</summary>
    private string LaunchFailureText() =>
        Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } } ? "The build failed:\n" + BuildOutputText()
        : "Read the output below, and get_build_output.\n" + RunOutputText(30);

    private RunningProgram? LastRunning() => Runs.Running is { Count: > 0 } running ? running[^1] : null;

    private string RunOutputText(int lines)
    {
        var running = LastRunning();
        var channel = running?.Channel
            ?? (Debugger.IsActive || _debugEnded is not null ? Workbench.Output.Channels.FirstOrDefault(c => c.Id == "codalus.debug") : null)
            ?? Workbench.Output.Channels.LastOrDefault(c => c.Id.StartsWith("codalus.run.", StringComparison.Ordinal));
        if (channel is null)
        {
            return running is { Channel: null } ? "The program runs in its own console window, so the IDE doesn't see its output (run.console)." : "Nothing has run yet.";
        }

        var text = channel.GetText().Split('\n');
        var state = running is not null ? (running.HasExited ? "It has exited." : "It is running" + (running.Listening.IsCompletedSuccessfully ? " at " + running.Listening.Result : "") + ".")
            : Debugger.IsActive ? "It is being debugged (" + Debugger.State.ToString().ToLowerInvariant() + ")." : "It has ended.";
        return state + "\n" + string.Join('\n', text.Skip(Math.Max(0, text.Length - Math.Clamp(lines, 1, 2000))));
    }

    private async Task<IdeToolResult> StopRunToolAsync()
    {
        var stopped = new List<string>();
        if (Runs.Running.Count > 0)
        {
            await StopRunningAsync().ConfigureAwait(true);
            stopped.Add("the program");
        }

        if (Debugger.IsActive)
        {
            await Debugger.StopAsync().ConfigureAwait(true);
            stopped.Add("the debug session");
        }

        return new(stopped.Count == 0 ? "Nothing was running." : "Stopped " + string.Join(" and ", stopped) + ".");
    }

    // ------------------------------------------------------------------ debugging

    /// <summary>debug_start, with a desktop app's windows off-screen when asked (M60b).</summary>
    private async Task<IdeToolResult> DebugStartToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var offScreen = Bool(args, "offScreen") == true && Str(args, "test") is not { Length: > 0 } && !Debugger.IsActive
            ? await PrepareOffScreenAsync(cancellationToken).ConfigureAwait(true)
            : null;
        try
        {
            var result = await DebugLaunchToolAsync(args, root, cancellationToken).ConfigureAwait(true);
            return offScreen is null ? result : result with { Text = offScreen + "\n" + result.Text };
        }
        finally
        {
            _offScreenLaunch = null;
        }
    }

    private async Task<IdeToolResult> DebugLaunchToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (Debugger.IsActive)
        {
            return IdeToolResult.Error("A debug session is running already: use debug_get_state, or stop_run first.");
        }

        Task launch;
        var test = Str(args, "test") is { Length: > 0 } name ? name : null;
        if (test is not null)
        {
            ActivateTestProviders();
            var keys = Tests.Read(r => r.SelectMany(n => n.Leaves()).Where(l => l.FullLabel.Equals(test, StringComparison.Ordinal) || l.FullLabel.EndsWith("." + test, StringComparison.Ordinal)).Select(l => l.Key).ToList());
            if (keys.Count == 0)
            {
                return IdeToolResult.Error($"No test named '{test}'. get_test_results lists them.");
            }

            launch = RunTestsAsync(TestRunKind.Debug, keys);
        }
        else
        {
            var project = Str(args, "project") is { Length: > 0 } projectName ? ProjectPath(projectName) : null;
            launch = StartDebuggingAsync(project);
        }

        _agentLaunch = launch;

        // A project: until its launch has finished (the build, the launch profile, the debugger, and a web app's page),
        // which says whether it started; it can take minutes on a large solution. A test: until its session runs (the
        // test run ends only with it).
        var deadline = DateTime.UtcNow + s_debugLaunchWait;
        while (!launch.IsCompleted && DateTime.UtcNow < deadline
            && Debugger.State != DebugState.Stopped
            && !(test is not null && Debugger.State == DebugState.Running))
        {
            await Task.WhenAny(launch, Task.Delay(200, cancellationToken)).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (launch is { IsFaulted: true, Exception: { } failed })
        {
            return IdeToolResult.Error("The debug session didn't start: " + failed.GetBaseException().Message);
        }

        if (Debugger.State == DebugState.Idle)
        {
            return IdeToolResult.Error(launch.IsCompleted
                ? (_debugEnded is { } ended ? "The debug session started and ended. " + ended + "\n" + RunOutputText(30) : "The debug session didn't start. " + LaunchFailureText())
                : string.Create(CultureInfo.InvariantCulture, $"The debug session hasn't started after {s_debugLaunchWait.TotalMinutes:0} minutes (the build or the launch is still going). Use debug_wait_for_stop to wait for it, or get_build_output."));
        }

        // A web app: until it listens, unless something stops it first.
        string? app = null;
        if (test is null && Debugger.State == DebugState.Running && _debugLaunchUrl is not null)
        {
            app = await WebAppAsync(s_webAppWait, cancellationToken).ConfigureAwait(true);
        }

        var sb = new StringBuilder();
        if (Debugger.State == DebugState.Stopped)
        {
            sb.Append("The debug session started and stopped.\n").Append(await DebugStateTextAsync(root).ConfigureAwait(true));
        }
        else if (!Debugger.IsActive)
        {
            sb.Append("The debug session started and ended. ").Append(_debugEnded).Append('\n').Append(RunOutputText(30));
            return new(sb.ToString());
        }
        else
        {
            sb.Append("The debug session is running.");
            if (app is not null)
            {
                sb.Append(" The web app listens at ").Append(app).Append(_debugBrowserProfile is not null
                    ? "; its page is open in the IDE's debugging browser, which browser_* drives (its breakpoints stop there)."
                    : "; browser_* opens it.");
            }
            else if (_debugLaunchUrl is { } expected)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $" Nothing listens at {expected} after {s_webAppWait.TotalSeconds:0} s: read get_run_output."));
            }

            sb.Append(" Make the program reach a breakpoint (a web app: browser_navigate or browser_evaluate), then debug_wait_for_stop.");
        }

        sb.Append('\n').Append(BreakpointsText(root));
        return new(sb.ToString().TrimEnd());
    }

    /// <summary>The breakpoints and whether the debugger bound them (and why not), for the agent to check its plan.</summary>
    private string BreakpointsText(string? root)
    {
        if (Debugger.Breakpoints.All.Count == 0)
        {
            return "No breakpoints are set.";
        }

        var sb = new StringBuilder("Breakpoints:\n");
        foreach (var b in Debugger.Breakpoints.All.Take(30))
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"  {Relative(b.Path, root)}:{b.Line}"));
            sb.AppendLine(!b.Enabled ? " — disabled"
                : Debugger.Breakpoints.StateOf(b) is { Verified: false } state ? " — not bound" + (state.Message is { Length: > 0 } m ? ": " + m : " yet (its code hasn't loaded, or no code is on that line)")
                : Debugger.IsActive ? " — bound" : "");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> WaitForStopTextAsync(TimeSpan timeout, string? root, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        if (!Debugger.IsActive && _agentLaunch is { IsCompleted: false } launch)
        {
            // debug_start's launch is still building: wait for the session first.
            await WaitUntilAsync(() => Debugger.IsActive || launch.IsCompleted, deadline, cancellationToken).ConfigureAwait(true);
        }

        if (!Debugger.IsActive)
        {
            return _debugEnded is { } ended ? ended + " " + RunOutputText(30)
                : _agentLaunch is { IsCompleted: false } ? string.Create(CultureInfo.InvariantCulture, $"The debug session is still being launched (building) after {timeout.TotalSeconds:0} s; wait again.")
                : "No debug session is running.";
        }

        if (Debugger.State != DebugState.Stopped)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStopped(object? s, StopInfo e) => stopped.TrySetResult();
            void OnEnded(object? s, int? e) => stopped.TrySetResult();
            Debugger.Stopped += OnStopped;
            Debugger.Ended += OnEnded;

            // A web app that doesn't listen yet: its start ends the wait too, since nothing stops it before a request.
            using var readyStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ready = _debugLaunchUrl is not null && ListeningAddress() is null
                ? WaitForWebAppAsync(readyStop.Token)
                : new TaskCompletionSource<string?>().Task;
            try
            {
                if (Debugger.State != DebugState.Stopped && Debugger.IsActive)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    var first = await Task.WhenAny(stopped.Task, ready).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken).ConfigureAwait(true);
                    if (first == ready && ready.IsCompletedSuccessfully && ready.Result is { } app && Debugger.State == DebugState.Running)
                    {
                        return "The web app has started and listens at " + app + "; nothing has stopped it yet. Request the code with a breakpoint (browser_navigate, browser_evaluate), then debug_wait_for_stop.\n" + BreakpointsText(root);
                    }
                }
            }
            catch (TimeoutException)
            {
                var app = ListeningAddress();
                return string.Create(CultureInfo.InvariantCulture, $"The program is still running after {timeout.TotalSeconds:0} s (nothing stopped it).")
                    + (app is not null ? " The web app listens at " + app + ": request the code with a breakpoint (browser_navigate, browser_evaluate), then wait again." : " Make it reach a breakpoint, then wait again.")
                    + "\n" + BreakpointsText(root);
            }
            finally
            {
                Debugger.Stopped -= OnStopped;
                Debugger.Ended -= OnEnded;
                await readyStop.CancelAsync().ConfigureAwait(true);
            }

            // The stack is read before Stopped is raised; let the UI-thread handlers finish.
            await Task.Yield();
        }

        return Debugger.IsActive ? await DebugStateTextAsync(root).ConfigureAwait(true) : (_debugEnded ?? "The program ended.") + " " + RunOutputText(30);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, DateTime deadline, CancellationToken cancellationToken)
    {
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<string> DebugStateTextAsync(string? root)
    {
        if (!Debugger.IsActive)
        {
            return "No debug session is running.";
        }

        var sb = new StringBuilder().Append("State: ").AppendLine(Debugger.State.ToString().ToLowerInvariant());
        if (Debugger.State == DebugState.Stopped && Debugger.CurrentStop is { } stop)
        {
            sb.Append("Stopped: ").Append(stop.Reason);
            if (stop.Description is { Length: > 0 } description)
            {
                sb.Append(" — ").Append(description);
            }

            sb.AppendLine();
            if (stop.Exception is { } exception)
            {
                sb.Append("Exception: ").Append(exception.Details?.FullTypeName ?? exception.ExceptionId).Append(": ").AppendLine(exception.Details?.Message ?? exception.Description);
                if (exception.Details?.StackTrace is { Length: > 0 } stack)
                {
                    sb.AppendLine(string.Join('\n', stack.Split('\n').Take(20).Select(l => "  " + l.Trim())));
                }
            }

            if (Debugger.Threads.Count > 1)
            {
                sb.Append("Threads: ").AppendLine(string.Join(", ", Debugger.Threads.Take(30).Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Id} {t.Name}{(t.Id == stop.ThreadId ? " (stopped)" : "")}"))));
            }

            sb.AppendLine("Call stack:");
            var lines = new LineCache(this);
            for (var i = 0; i < Debugger.Frames.Count && i < 30; i++)
            {
                var frame = Debugger.Frames[i];
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"  #{i} {frame.Name}"));
                if (frame.Source?.Path is { } path && IsInWorkspace(path, root))
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" at {Relative(path, root)}:{frame.Line}"));
                    if (i == 0 && await lines.LineAsync(path, frame.Line - 1, CancellationToken.None).ConfigureAwait(true) is { } text)
                    {
                        sb.Append(" → ").Append(text.Trim());
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private StackFrame StoppedFrame(int index)
    {
        if (Debugger.State != DebugState.Stopped)
        {
            throw new IdeToolException(Debugger.IsActive ? "The program isn't stopped: use debug_wait_for_stop first." : "No debug session is running.");
        }

        return index >= 0 && index < Debugger.Frames.Count ? Debugger.Frames[index] : throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"There is no frame {index}; the stack has {Debugger.Frames.Count}."));
    }

    private async Task<string> VariablesTextAsync(int frameIndex, string? expand, CancellationToken cancellationToken)
    {
        var frame = StoppedFrame(frameIndex);
        var sb = new StringBuilder();
        if (expand is { Length: > 0 })
        {
            var value = await Debugger.EvaluateInFrameAsync(expand, frame.Id, cancellationToken).ConfigureAwait(true);
            sb.Append(expand).Append(" = ").Append(value.Result).AppendLine(value.Type is { } t ? "  (" + t + ")" : "");
            if (value.VariablesReference > 0)
            {
                foreach (var member in (await Debugger.VariablesAsync(value.VariablesReference, new VariablesPage(0, 100, null)).ConfigureAwait(true)).Take(100))
                {
                    sb.Append("  ").Append(member.Name).Append(" = ").Append(member.Value).AppendLine(member.Type is { } mt ? "  (" + mt + ")" : "");
                }
            }

            return sb.ToString();
        }

        foreach (var scope in await Debugger.ScopesAsync(frame).ConfigureAwait(true))
        {
            if (scope.Expensive)
            {
                continue;
            }

            sb.Append(scope.Name).AppendLine(":");
            foreach (var variable in (await Debugger.VariablesAsync(scope.VariablesReference).ConfigureAwait(true)).Take(100))
            {
                sb.Append("  ").Append(variable.Name).Append(" = ").Append(variable.Value).Append(variable.Type is { } type ? "  (" + type + ")" : "")
                    .AppendLine(variable.VariablesReference > 0 ? "  [expand: " + (variable.EvaluateName ?? variable.Name) + "]" : "");
            }
        }

        return sb.Length == 0 ? "No variables in that frame." : sb.ToString();
    }

    // ------------------------------------------------------------------ the browser

    /// <summary>What a browser call returned, or, when <see cref="Interrupted"/>, why it didn't (the program stopped).</summary>
    private readonly record struct BrowserOutcome(string Text, bool Interrupted);

    /// <summary>
    /// The address a web app the IDE runs or debugs listens on, now: the last running program's "Now listening on", or
    /// the debug session's.
    /// </summary>
    private string? ListeningAddress() =>
        Runs.Running.LastOrDefault(r => r.Listening.IsCompletedSuccessfully && !r.HasExited)?.Listening.Result
        ?? (Debugger.IsActive ? _debugAppUrl : null);

    /// <summary>The address the web app being run or debugged was told to listen on, when one is expected.</summary>
    private async Task<string?> ExpectedAddressAsync()
    {
        if (Debugger.IsActive)
        {
            return _debugLaunchUrl;
        }

        return LastRunning() is { HasExited: false } running ? await RunLaunchUrlAsync(running).ConfigureAwait(true) : null;
    }

    /// <summary>
    /// The web app's address once it accepts requests, waiting at most <paramref name="wait"/> while it is starting: a
    /// launch still building, a debug session starting, or a program that was told an address and doesn't answer there
    /// yet. Null when no web app is starting or it didn't listen in time; it returns at once when the debugger stops it.
    /// </summary>
    private async Task<string?> WebAppAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (ListeningAddress() is { } listening)
            {
                return listening;
            }

            var expected = await ExpectedAddressAsync().ConfigureAwait(true);
            if (expected is not null && await AcceptsAsync(expected, cancellationToken).ConfigureAwait(true))
            {
                return expected; // its logging may filter the host's "Now listening on"
            }

            var starting = _agentLaunch is { IsCompleted: false }
                || Debugger.State == DebugState.Starting
                || (expected is not null && (Debugger.State == DebugState.Running || LastRunning() is { HasExited: false }));
            if (!starting || Debugger.State == DebugState.Stopped || DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Until a web app listens (debug_wait_for_stop's other end), however long that takes.</summary>
    private async Task<string?> WaitForWebAppAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await WebAppAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(true) is { } app)
            {
                return app;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Whether something accepts a connection at <paramref name="url"/>'s host and port (within a second).</summary>
    private static async Task<bool> AcceptsAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// The browser agents drive, and the web app's address (null when none listens):
    /// <list type="bullet">
    /// <item>while a debug session has a page (a Blazor Web App, a web app whose page is debugged with it), that page, in
    /// the IDE's debugging browser, so the page's own breakpoints (WebAssembly components, scripts) stop;</item>
    /// <item>otherwise a browser of the agent's own, started at the web app's address, waiting for a web app that is
    /// starting.</item>
    /// </list>
    /// </summary>
    private async Task<(AgentBrowser Browser, string? App)> BrowserAsync(CancellationToken cancellationToken)
    {
        var app = await WebAppAsync(s_webAppWait, cancellationToken).ConfigureAwait(true);
        if (Debugger.IsActive && _debugBrowserProfile is { } profile)
        {
            if (_agentBrowser is { IsRunning: true, IsAttached: true } attached)
            {
                return (attached, app);
            }

            await StopAgentBrowserAsync().ConfigureAwait(true);
            var origin = app is not null && Uri.TryCreate(app, UriKind.Absolute, out var appUri) ? appUri.GetLeftPart(UriPartial.Authority) : null;
            _agentBrowser = await AgentBrowser.AttachAsync(profile, origin, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(true);
            return (_agentBrowser, app);
        }

        if (_agentBrowser is { IsRunning: true, IsAttached: false } running)
        {
            return (running, app);
        }

        await StopAgentBrowserAsync().ConfigureAwait(true);
        var start = app ?? throw new IdeToolException(NoWebAppText());
        var browser = Browsers.Find(Workbench.Settings.GetString(Browsers.SettingKey, "auto")) ?? throw new IdeToolException("No Chromium browser (Chrome, Edge or Chromium) was found; set debug.browser to one.");
        var ownProfile = Path.Combine(Paths.State, "agent-browser");
        _agentBrowser = await AgentBrowser.StartAsync(browser.Path, ownProfile, start, Browsers.Headless, cancellationToken).ConfigureAwait(true);
        return (_agentBrowser, app);
    }

    /// <summary>Why the browser tools have no web app: nothing runs, it is stopped, or it doesn't listen.</summary>
    private string NoWebAppText() =>
        Debugger.State == DebugState.Stopped
            ? "The debugged program is stopped (debug_get_state says where) before its web app listens: debug_continue, then try again."
        : _agentLaunch is { IsCompleted: false }
            ? string.Create(CultureInfo.InvariantCulture, $"The app is still being built and launched after {s_webAppWait.TotalSeconds:0} s: try again, or read get_build_output.")
        : Debugger.IsActive || LastRunning() is { HasExited: false }
            ? string.Create(CultureInfo.InvariantCulture, $"A program runs, but no web app listens after {s_webAppWait.TotalSeconds:0} s: read get_run_output (is it a web app, and did it start?).")
        : "No web app is running in the IDE. Start it with run_project or debug_start, then use the browser tools.";

    /// <summary>
    /// Calls the agent's browser, after opening <paramref name="navigate"/> (an address relative to the web app's) when
    /// given. When the debugger stops the program meanwhile (a breakpoint the call's click reached), it returns at once
    /// with where it stopped, and the call finishes when the program goes on. A page the debugger has paused doesn't
    /// answer: the call says so after a few seconds instead of waiting.
    /// </summary>
    private async Task<BrowserOutcome> BrowserCallAsync(string? navigate, Func<AgentBrowser, CancellationToken, Task<string>> call, string? root, CancellationToken cancellationToken)
    {
        var (browser, app) = await BrowserAsync(cancellationToken).ConfigureAwait(true);
        string? target = null;
        if (navigate is { Length: > 0 })
        {
            target = Uri.TryCreate(navigate, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.AbsoluteUri
                : app is not null && Uri.TryCreate(new Uri(app), navigate, out var relative) ? relative.AbsoluteUri
                : throw new IdeToolException(NoWebAppText() + " A full address (http://localhost:port/path) opens without one.");
            if (new Uri(target) is { IsLoopback: false })
            {
                throw new IdeToolException("The agent's browser only opens the web app on this machine (localhost).");
            }
        }

        var stoppedBefore = Debugger.State == DebugState.Stopped;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? s, StopInfo e) => stopped.TrySetResult();
        Debugger.Stopped += OnStopped;
        try
        {
            var work = Task.Run(async () =>
            {
                if (target is not null)
                {
                    await browser.NavigateAsync(target, cancellationToken, retryFor: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }

                return await call(browser, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            var paused = stoppedBefore && browser.IsAttached ? Task.Delay(TimeSpan.FromSeconds(5), cancellationToken) : Task.Delay(Timeout.Infinite, cancellationToken);
            var first = await Task.WhenAny(work, stopped.Task, paused).ConfigureAwait(true);
            if (first == work)
            {
                return new(await work.ConfigureAwait(true), false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (first == stopped.Task)
            {
                await Task.Yield(); // the stack is read before Stopped is raised
                return new("The debugged program stopped while the page was busy (the page waits for it):\n" + await DebugStateTextAsync(root).ConfigureAwait(true), true);
            }

            return new("The page doesn't answer: the debugged program is stopped, which pauses the page. Read it (debug_get_state, debug_get_variables), then debug_continue or debug_step.", true);
        }
        finally
        {
            Debugger.Stopped -= OnStopped;
        }
    }

    // ------------------------------------------------------------------ arguments and paths

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : null;

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => value.GetString() == "true", _ => null }
            : null;

    private string? OptionalPath(JsonElement args, string name, string? root) => Str(args, name) is { Length: > 0 } path ? WorkspacePath(path, root) : null;

    private string RequiredPath(JsonElement args, string name, string? root) => OptionalPath(args, name, root) ?? throw new IdeToolException($"A {name} is needed.");

    /// <summary>A path an agent gave, inside the workspace (links included), or an error.</summary>
    private static string WorkspacePath(string path, string? root)
    {
        if (root is null)
        {
            throw new IdeToolException("No solution or folder is open.");
        }

        try
        {
            return WorkspacePaths.Resolve(root, path);
        }
        catch (Codalus.Workbench.Ai.Agent.AgentToolException ex)
        {
            throw new IdeToolException(ex.Message);
        }
    }

    private static bool IsInWorkspace(string path, string? root) =>
        root is not null && Codalus.Workbench.FileSystem.FileSystems.IsUnder(Path.GetFullPath(path), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private Task<bool> IsExcludedAsync(string path, string? root, CancellationToken cancellationToken) => Ai.Exclusions.IsExcludedAsync(path, root, cancellationToken);

    private async Task EnsureReadableAsync(string path, string? root, CancellationToken cancellationToken)
    {
        if (await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true))
        {
            throw new IdeToolException($"'{Relative(path, root)}' is excluded from AI.");
        }
    }

    private static string Relative(string path, string? root) =>
        root is not null && IsInWorkspace(path, root) ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;

    private string ProjectPath(string name)
    {
        var project = Solutions.Projects.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
        return project?.Path ?? throw new IdeToolException($"No project named '{name}'. get_solution lists them.");
    }
}
