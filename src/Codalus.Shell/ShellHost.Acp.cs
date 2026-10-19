using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// External agents over the Agent Client Protocol (M48, ADR 0079): the agents the user configured, and the IDE's side of
/// what they ask for. Files come from the editor, writes go through the refactoring preview into the editor's buffer,
/// permissions and commands through the approval dialog with Reject the default, and each session gets <c>codalus mcp</c>
/// with a pipe of its own.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The external agents and the session that runs (one agent process per chat session).</summary>
    public AcpAgents ExternalAgents { get; private set; } = null!;

    /// <summary>The IDE's side of an external agent's requests.</summary>
    public IAcpHost AcpHost { get; private set; } = null!;

    /// <summary>
    /// How <c>codalus mcp</c> is started for an agent: the running executable (absolute), and the shell's assembly when that
    /// is <c>dotnet</c>. Tests set it to <c>dotnet codalus.dll</c> (their own process is the test runner).
    /// </summary>
    public (string Path, string? Assembly) McpExecutable { get; set; } = (Environment.ProcessPath ?? "codalus", Path.Combine(AppContext.BaseDirectory, "codalus.dll"));

    /// <summary>The session pipes open now (tests).</summary>
    public int OpenAgentMcpPipes => _agentPipes;

    private int _agentPipes;

    private void InitializeExternalAgents()
    {
        ExternalAgents = new AcpAgents(Workbench.Settings, Ai);
        AcpHost = new ShellAcpHost(this);

        // The agent works in one workspace: closing it, or opening another, stops it.
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (ExternalAgents.Current is { } session && !string.Equals(session.WorkspaceRoot, Solutions.Solution?.Directory is { } root ? Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) : null, StringComparison.Ordinal))
            {
                _ = ExternalAgents.StopAsync();
            }

            // Whether the chosen agent can run here follows the workspace (opened, closed, trusted).
            AiChat?.Refresh();
        });
    }

    /// <summary>Shows a dialog that closes (as cancelled) when <paramref name="cancellationToken"/> is cancelled: the turn was stopped.</summary>
    private async Task<DialogResult> ShowCancellableAsync(DialogRequest request, CancellationToken cancellationToken)
    {
        var shown = Dialogs.ShowAsync(request);
        await using var registration = cancellationToken.Register(() => Ui.Post(() =>
        {
            if (ReferenceEquals(Dialogs.Request, request))
            {
                Dialogs.Cancel();
            }
        })).ConfigureAwait(true);
        var result = await shown.ConfigureAwait(true);
        return cancellationToken.IsCancellationRequested ? new DialogResult(null, null) : result;
    }

    private sealed class ShellAcpHost(ShellHost shell) : IAcpHost
    {
        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () => shell.OpenTextOf(path) ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Text : null)).Unwrap();

        public Task<bool> ProposeWriteAsync(string path, string? oldText, string newText, string agent, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var relative = shell.Solutions.Solution?.Directory is { } root ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
                var preview = Edit(path, oldText ?? "", newText, version: null);
                var details = await shell.PreviewAsync(preview).ConfigureAwait(true);
                var apply = Localizer.Get("msg.acp.apply", "Apply");
                var title = oldText is null
                    ? Localizer.Format("msg.acp.agentCreates", "The external agent {0} creates {1}", agent, relative)
                    : Localizer.Format("msg.acp.agentChanges", "The external agent {0} changes {1}", agent, relative);
                var choice = await shell.ShowCancellableAsync(new DialogRequest(title, Localizer.Get("msg.acp.changesInOneFile", "Changes in 1 file:"), [apply, Localizer.Get("msg.acp.reject", "Reject")])
                {
                    Details = details,
                    Icon = "edit",
                    Context = Context(agent, relative),
                }, cancellationToken).ConfigureAwait(true);
                if (choice.Button != apply)
                {
                    return false;
                }

                if (oldText is null)
                {
                    return await shell.ApplyWorkspaceEditAsync(preview).ConfigureAwait(true);
                }

                // Into the editor's buffer (unsaved, one undo step): the file opens first when it isn't.
                if (!shell._models.ContainsKey(path))
                {
                    await shell.OpenFileAsync(path, preview: true).ConfigureAwait(true);
                }

                if (!shell._models.TryGetValue(path, out var model))
                {
                    return await shell.ApplyWorkspaceEditAsync(preview).ConfigureAwait(true);
                }

                return await shell.ApplyWorkspaceEditAsync(Edit(path, model.Text.Text, newText, model.Version)).ConfigureAwait(true);
            }).Unwrap();

        /// <summary>The dialog's context line (M34h's dialog frame): which external agent asks, and what it acts on.</summary>
        private static string Context(string agent, string? target) =>
            target is { Length: > 0 }
                ? Localizer.Format("msg.acp.dialogContextOn", "{0} · external agent · {1}", agent, target)
                : Localizer.Format("msg.acp.dialogContext", "{0} · external agent", agent);

        /// <summary>A whole-file replacement (an insertion into an empty file creates it).</summary>
        private static WorkspaceEdit Edit(string path, string oldText, string newText, int? version) =>
            new([new TextDocumentEdit(
                new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), version),
                [new TextEdit(new LanguageServices.Protocol.Range(new Position(0, 0), PositionIn(oldText, oldText.Length)), newText)])]);

        public Task<string?> RequestPermissionAsync(AcpPermissionPrompt prompt, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var buttons = new List<(string Label, string OptionId)>();
                foreach (var option in prompt.Options)
                {
                    var label = option.Kind switch
                    {
                        "allow_once" => Localizer.Get("msg.acp.allowOnce", "Allow Once"),
                        "reject_once" => Localizer.Get("msg.acp.reject", "Reject"),
                        _ => Localizer.Get("msg.acp.rejectAlways", "Always Reject"),
                    };
                    if (buttons.All(b => b.Label != label))
                    {
                        buttons.Add((label, option.OptionId));
                    }
                }

                var reject = Localizer.Get("msg.acp.reject", "Reject");
                if (buttons.All(b => b.Label != reject))
                {
                    buttons.Add((reject, ""));
                }

                var details = new List<DetailLine>();
                if (prompt.Kind is { Length: > 0 } kind)
                {
                    details.Add(new DetailLine(Localizer.Format("msg.acp.kind", "Kind: {0}", kind), "header"));
                }

                if (prompt.RawInput is { Length: > 0 } raw)
                {
                    details.AddRange(raw.Split('\n').Select(l => new DetailLine(l.TrimEnd('\r'))));
                }

                foreach (var diff in prompt.Diffs)
                {
                    details.AddRange(DiffLines(diff));
                }

                var choice = await shell.ShowCancellableAsync(new DialogRequest(
                    Localizer.Format("msg.acp.permissionTitle", "Allow {0}?", prompt.Agent),
                    Localizer.Format("msg.acp.permissionMessage", "The external agent {0} asks to: {1}", prompt.Agent, prompt.Title),
                    [.. buttons.Select(b => b.Label)])
                {
                    Details = details,
                    DefaultButton = reject,
                    Icon = "shield",
                    Context = Context(prompt.Agent, prompt.Kind),
                }, cancellationToken).ConfigureAwait(true);
                return buttons.FirstOrDefault(b => b.Label == choice.Button).OptionId is { Length: > 0 } id ? id : null;
            }).Unwrap();

        public Task<bool> ApproveCommandAsync(string agent, string commandLine, string folder, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var root = shell.ExternalAgents.Current?.WorkspaceRoot;
                var where = root is null || string.Equals(folder, root, StringComparison.Ordinal) ? Localizer.Get("msg.acp.theWorkspace", "the workspace") : Path.GetRelativePath(root, folder);
                var allow = Localizer.Get("msg.acp.allow", "Allow");
                var reject = Localizer.Get("msg.acp.reject", "Reject");
                var choice = await shell.ShowCancellableAsync(new DialogRequest(
                    Localizer.Get("msg.acp.runTitle", "Run this command?"),
                    Localizer.Format("msg.acp.runMessage", "The external agent {0} wants to run a command in {1}:", agent, where),
                    [allow, reject])
                {
                    Details = [new DetailLine(commandLine)],
                    DefaultButton = reject,
                    Icon = "terminal",
                    Context = Context(agent, where),
                }, cancellationToken).ConfigureAwait(true);
                return choice.Button == allow;
            }).Unwrap();

        public Task<AcpAuthMethod?> ChooseSignInAsync(string agent, IReadOnlyList<AcpAuthMethod> methods, CancellationToken cancellationToken) =>
            Ui.RunAsync(async () =>
            {
                var cancel = Localizer.Get("msg.acp.cancel", "Cancel");
                var choice = await shell.ShowCancellableAsync(new DialogRequest(
                    Localizer.Format("msg.acp.signInTitle", "Sign in to {0}", agent),
                    Localizer.Format("msg.acp.signInMessage", "{0} needs you to sign in. It manages its own sign-in: Codalus IDE doesn't see or keep its credentials.", agent),
                    [.. methods.Select(m => m.Name), cancel])
                {
                    Details = [.. methods.Where(m => m.Description is { Length: > 0 }).Select(m => new DetailLine(m.Name + ": " + m.Description))],
                    DefaultButton = cancel,
                    Icon = "key",
                    Context = Context(agent, null),
                }, cancellationToken).ConfigureAwait(true);
                return methods.FirstOrDefault(m => m.Name == choice.Button);
            }).Unwrap();

        public Task ReloadFromDiskAsync(string path) =>
            Ui.RunAsync(async () =>
            {
                if (!shell._models.TryGetValue(path, out var model))
                {
                    return;
                }

                if (model.IsModified)
                {
                    await shell.ResolveConflictAsync(model).ConfigureAwait(true);
                    return;
                }

                try
                {
                    await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    shell.Workbench.Notifications.Error(Localizer.Format("msg.acp.couldNotReload", "Could not reload {0}: {1}", model.Path, ex.Message));
                }
            });

        public (AcpMcpServerStdio Server, IDisposable Lifetime)? StartMcpServer(string agent)
        {
            var token = McpBridge.NewSessionToken();
            var pipe = McpBridge.SessionPipeName(SingleInstance.PipeName(shell.Paths.State), token);

            // Open for this session only, whatever ai.mcpServer.enabled says (ADR 0079), for the current user only.
            var bridge = McpBridge.Listen(pipe, (tool, args, ct) => Ui.RunAsync(() => shell.McpToolAsync(tool, args, ct, agent)).Unwrap());
            Interlocked.Increment(ref shell._agentPipes);
            var (path, assembly) = shell.McpExecutable;
            var server = AcpAgents.CodalusMcpServer(path, assembly, shell.Options.UserDataDir, token);
            return (server, new PipeLifetime(bridge, () => Interlocked.Decrement(ref shell._agentPipes)));
        }

        private sealed class PipeLifetime(McpBridge bridge, Action closed) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    bridge.Dispose();
                    closed();
                }
            }
        }
    }

    /// <summary>A diff as dialog lines: a header, the old lines removed and the new ones added (around their common start and end).</summary>
    internal static IEnumerable<DetailLine> DiffLines(AiStepDiff diff)
    {
        yield return new DetailLine(diff.Path, "header");
        foreach (var (kind, text) in AiDiffText.Lines(diff.OldText, diff.NewText))
        {
            yield return new DetailLine((kind == '+' ? "+ " : kind == '-' ? "- " : "  ") + text, kind == '+' ? "added" : kind == '-' ? "removed" : null);
        }
    }
}
