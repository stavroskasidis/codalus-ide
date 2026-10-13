using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// AI chat (M28a, ADR 0062): off until the user turns it on and picks a provider and a model; a workspace can turn it
/// off for itself. Each message carries only the context the user switches on, listed with it, minus secrets and
/// ignored files (<see cref="AiContext"/>). Code blocks in answers apply to the selection or file they were asked about,
/// through the refactoring preview. A declarative view.
/// </summary>
public sealed partial class ShellHost
{
    public const string AiChatWindow = "ai.chat";
    public const string AiEnabledSetting = "ai.enabled";
    public const string AiProviderSetting = "ai.provider";
    public const string AiModelSetting = "ai.model";
    private const string AiStatusItem = "ai.status";

    private readonly AiChatState _ai = new();

    /// <summary>A turn of the conversation.</summary>
    private sealed record AiTurn(string Role, string Text, IReadOnlyList<string> Attachments, AiTarget? Target);

    /// <summary>What a question was asked about: where a code block of its answer applies.</summary>
    private sealed record AiTarget(string Path, string Uri, int StartLine, int StartCharacter, int EndLine, int EndCharacter, bool WholeFile);

    private sealed class AiChatState
    {
        public readonly List<AiTurn> Turns = [];
        public string Question = string.Empty;
        public StringBuilder? Streaming;
        public CancellationTokenSource? Cancel;
        public IReadOnlyList<string> Models = [];
        public string? Status;
        public bool IncludeSelection = true;
        public bool IncludeFile = true;
        public bool IncludeOpenFiles;
        public bool IncludeDiagnostics = true;
        public bool IncludeBuild;
        public Action? Changed;
    }

    public sealed class AiChatContent
    {
        internal AiChatContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }
    }

    private AiChatContent? _aiContent;

    private AiChatContent AiChat() => _aiContent ??= new AiChatContent(this);

    /// <summary>Whether AI is on: the user turned it on, and this workspace didn't turn it off.</summary>
    public bool AiEnabled =>
        Workbench.Settings.GetLayerValue(AiEnabledSetting, SettingScope.User) is { ValueKind: JsonValueKind.True }
        && Workbench.Settings.GetLayerValue(AiEnabledSetting, SettingScope.Workspace) is not { ValueKind: JsonValueKind.False };

    private string AiProvider => Workbench.Settings.GetString(AiProviderSetting);

    private string AiModel => Workbench.Settings.GetString(AiModelSetting);

    private void InitializeAiChat()
    {
        Workbench.Settings.Register(
        [
            SettingDescriptor.Create(AiEnabledSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
            {
                Title = "Use AI", Category = "AI", Scope = SettingScope.Workspace,
                Description = "Turn on AI assistance. Nothing is sent anywhere until it's on and a provider and model are chosen; a workspace can turn it off for itself.",
            },
            SettingDescriptor.Create(AiProviderSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
            {
                Title = "AI Provider", Category = "AI", Scope = SettingScope.User,
                Description = "The plugin that answers: an OpenAI-compatible endpoint (OpenAI, or a local server), Anthropic, or a plugin's.",
            },
            SettingDescriptor.Create(AiModelSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
            {
                Title = "AI Model", Category = "AI", Scope = SettingScope.User,
                Description = "The provider's model. The AI Chat window lists them.",
            },
        ]);
        Workbench.ToolWindows.Register(new ToolWindowDescriptor(AiChatWindow, "AI Chat", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "comment" });
        Workbench.Commands.Register(new CommandDescriptor("ai.chat", "AI Chat", ContributionSource.Shell) { Category = "AI" }, (_, _) =>
        {
            Ui.Post(() => Layout.ShowToolWindow(AiChatWindow, focus: true));
            return Task.FromResult<JsonElement?>(null);
        });
        Workbench.Commands.Register(new CommandDescriptor("ai.newChat", "New AI Chat", ContributionSource.Shell) { Category = "AI" }, (_, _) =>
        {
            Ui.Post(NewAiChat);
            return Task.FromResult<JsonElement?>(null);
        });
        Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("view"), "ai.chat", ContributionSource.Shell) { Group = "view", Order = 40 }]);
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects("ai"))
            {
                Ui.Post(() =>
                {
                    UpdateAiStatus();
                    _ai.Changed?.Invoke();
                });
            }
        };
        UpdateAiStatus();
    }

    /// <summary>The indicator: while AI is on, the provider and model, and whether a request is in flight.</summary>
    private void UpdateAiStatus()
    {
        if (!AiEnabled)
        {
            Workbench.StatusBar.Remove(AiStatusItem);
            return;
        }

        var label = Plugins.AiProviders.FirstOrDefault(p => p.Id == AiProvider).Label ?? "no provider";
        var busy = _ai.Cancel is not null;
        Workbench.StatusBar.Set(new StatusBarItem(AiStatusItem, busy ? $"$(comment) AI: sending to {label}…" : $"$(comment) AI: {label}{(AiModel.Length > 0 ? " · " + AiModel : string.Empty)}", StatusBarAlignment.Right, 20, ContributionSource.Shell.Id)
        {
            Command = "ai.chat",
            Tooltip = "AI is on: requests go to the chosen provider only when you send them. Click for the chat.",
        });
    }

    private void NewAiChat()
    {
        _ai.Cancel?.Cancel();
        _ai.Turns.Clear();
        _ai.Status = null;
        _ai.Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the view

    [GeneratedRegex(@"```[^\n]*\n(.*?)(?:```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    /// <summary>The text and code blocks of an answer, in order.</summary>
    internal static IReadOnlyList<(bool Code, string Text)> SplitAnswer(string answer)
    {
        var parts = new List<(bool, string)>();
        var index = 0;
        foreach (Match match in CodeBlockRegex().Matches(answer))
        {
            if (match.Index > index && answer[index..match.Index].Trim() is { Length: > 0 } text)
            {
                parts.Add((false, text));
            }

            parts.Add((true, match.Groups[1].Value.TrimEnd('\n', '\r')));
            index = match.Index + match.Length;
        }

        if (index < answer.Length && answer[index..].Trim() is { Length: > 0 } rest)
        {
            parts.Add((false, rest));
        }

        return parts;
    }

    internal ViewNodeDto AiChatRoot()
    {
        var children = new List<ViewNodeDto>();
        if (!AiEnabled)
        {
            children.Add(new ViewNodeDto("text") { Text = "AI is off", Style = "heading" });
            children.Add(new ViewNodeDto("text")
            {
                Text = Workbench.Settings.GetLayerValue(AiEnabledSetting, SettingScope.Workspace) is { ValueKind: JsonValueKind.False }
                    ? "This workspace turned AI off (its settings' ai.enabled)."
                    : "Nothing is sent anywhere until you turn it on and choose a provider and a model. Only what you attach to a message is sent; secrets and files your .gitignore or .codalusignore exclude never are.",
                Style = "muted",
            });
            children.Add(new ViewNodeDto("button") { Id = "turnOn", Text = "Turn On AI", Style = "primary" });
            return new ViewNodeDto("stack") { Children = [.. children] };
        }

        var providers = Plugins.AiProviders;
        // One control per line and buttons in toolbars: the chat is often a narrow side window.
        children.Add(new ViewNodeDto("dropdown") { Id = "provider", Options = ["", .. providers.Select(p => p.Label)], Value = providers.FirstOrDefault(p => p.Id == AiProvider).Label ?? string.Empty });
        children.Add(new ViewNodeDto("dropdown") { Id = "model", Options = ["", .. _ai.Models.Union(AiModel.Length > 0 ? [AiModel] : [])], Value = AiModel });
        children.Add(new ViewNodeDto("toolbar")
        {
            Children =
            [
                new ViewNodeDto("button") { Id = "configure", Text = "Configure…", Disabled = AiProvider.Length == 0 },
                new ViewNodeDto("button") { Id = "models", Text = "Refresh Models", Disabled = AiProvider.Length == 0 },
            ],
        });

        foreach (var (turn, index) in _ai.Turns.Select((t, i) => (t, i)))
        {
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = turn.Role == "user" ? "You" : AiModel.Length > 0 ? AiModel : "AI", Style = "heading" });
            if (turn.Attachments.Count > 0)
            {
                children.Add(new ViewNodeDto("text") { Text = "Sent with: " + string.Join(", ", turn.Attachments), Style = "muted" });
            }

            AddAnswer(children, turn.Text, turn.Role == "assistant" ? index : null);
        }

        if (_ai.Streaming is { } streaming)
        {
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = AiModel, Style = "heading" });
            string sofar;
            lock (streaming)
            {
                sofar = streaming.ToString();
            }

            AddAnswer(children, sofar, null);
        }

        if (_ai.Status is { } status)
        {
            children.Add(new ViewNodeDto("text") { Text = status, Style = "muted" });
        }

        children.Add(new ViewNodeDto("separator"));
        children.Add(new ViewNodeDto("text") { Text = "Send with the message:", Style = "muted" });
        children.Add(new ViewNodeDto("stack")
        {
            Children =
            [
                new ViewNodeDto("checkbox") { Id = "ctx:selection", Text = "Selection", Checked = _ai.IncludeSelection },
                new ViewNodeDto("checkbox") { Id = "ctx:file", Text = "Active file", Checked = _ai.IncludeFile },
                new ViewNodeDto("checkbox") { Id = "ctx:open", Text = "Open files", Checked = _ai.IncludeOpenFiles },
                new ViewNodeDto("checkbox") { Id = "ctx:diagnostics", Text = "Problems", Checked = _ai.IncludeDiagnostics },
                new ViewNodeDto("checkbox") { Id = "ctx:build", Text = "Build errors", Checked = _ai.IncludeBuild },
            ],
        });
        var busy = _ai.Cancel is not null;
        children.Add(new ViewNodeDto("textbox") { Id = "question", Value = _ai.Question, Placeholder = "Ask about your code (Enter sends)" });
        children.Add(new ViewNodeDto("toolbar")
        {
            Children =
            [
                new ViewNodeDto("button") { Id = "send", Text = "Send", Style = "primary", Disabled = busy || AiProvider.Length == 0 || AiModel.Length == 0 },
                new ViewNodeDto("button") { Id = "stop", Text = "Stop", Disabled = !busy },
                new ViewNodeDto("button") { Id = "new", Text = "New Chat", Disabled = _ai.Turns.Count == 0 },
            ],
        });
        return new ViewNodeDto("stack") { Children = [.. children] };
    }

    private static void AddAnswer(List<ViewNodeDto> children, string text, int? turn)
    {
        var block = 0;
        foreach (var (code, part) in SplitAnswer(text))
        {
            if (!code)
            {
                children.Add(new ViewNodeDto("text") { Text = part });
                continue;
            }

            children.Add(new ViewNodeDto("text") { Text = part, Style = "code" });
            if (turn is { } index)
            {
                children.Add(new ViewNodeDto("button") { Id = $"apply:{index}:{block}", Text = "Apply…" });
            }

            block++;
        }
    }

    // ------------------------------------------------------------------ events

    internal async Task OnAiChatEventAsync(string id, string kind, string? value)
    {
        switch (id, kind)
        {
            case ("turnOn", "click"):
                await Workbench.Settings.SetAsync(AiEnabledSetting, true, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                return;
            case ("provider", "change"):
                var provider = Plugins.AiProviders.FirstOrDefault(p => p.Label == value).Id ?? string.Empty;
                await Workbench.Settings.SetAsync(AiProviderSetting, provider, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await Workbench.Settings.SetAsync(AiModelSetting, string.Empty, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                _ai.Models = [];
                if (provider.Length > 0)
                {
                    await RefreshAiModelsAsync().ConfigureAwait(true);
                }

                return;
            case ("configure", "click"):
                try
                {
                    if (await Plugins.ConfigureAiAsync(AiProvider, CancellationToken.None).ConfigureAwait(true))
                    {
                        await RefreshAiModelsAsync().ConfigureAwait(true);
                        return;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
                {
                    _ai.Status = ex.Message;
                }

                break;
            case ("models", "click"):
                await RefreshAiModelsAsync().ConfigureAwait(true);
                return;
            case ("model", "change"):
                await Workbench.Settings.SetAsync(AiModelSetting, value ?? string.Empty, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                return;
            case ("question", "change"):
                _ai.Question = value ?? string.Empty;
                if (_ai.Question.Trim().Length > 0 && _ai.Cancel is null)
                {
                    await SendAiQuestionAsync().ConfigureAwait(true);
                    return;
                }

                break;
            case ("send", "click"):
                await SendAiQuestionAsync().ConfigureAwait(true);
                return;
            case ("stop", "click"):
                _ai.Cancel?.Cancel();
                return;
            case ("new", "click"):
                NewAiChat();
                return;
            case ("ctx:selection", "change"):
                _ai.IncludeSelection = value == "true";
                break;
            case ("ctx:file", "change"):
                _ai.IncludeFile = value == "true";
                break;
            case ("ctx:open", "change"):
                _ai.IncludeOpenFiles = value == "true";
                break;
            case ("ctx:diagnostics", "change"):
                _ai.IncludeDiagnostics = value == "true";
                break;
            case ("ctx:build", "change"):
                _ai.IncludeBuild = value == "true";
                break;
            default:
                if (id.StartsWith("apply:", StringComparison.Ordinal) && kind == "click")
                {
                    await ApplyAiCodeAsync(id).ConfigureAwait(true);
                }

                return;
        }

        _ai.Changed?.Invoke();
    }

    private async Task RefreshAiModelsAsync()
    {
        if (AiProvider.Length == 0)
        {
            return;
        }

        try
        {
            _ai.Models = [.. (await Plugins.GetAiModelsAsync(AiProvider, CancellationToken.None).ConfigureAwait(true)).Select(m => m.Id)];
            _ai.Status = _ai.Models.Count == 0 ? "The provider offers no models: choose Configure… to give it a key or an address." : null;
            if (AiModel.Length == 0 && _ai.Models.Count > 0)
            {
                await Workbench.Settings.SetAsync(AiModelSetting, _ai.Models[0], SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            _ai.Status = "The models couldn't be listed: " + ex.Message;
        }

        _ai.Changed?.Invoke();
    }

    /// <summary>Gathers the switched-on context (minus what may not be sent), sends the question, and streams the answer.</summary>
    internal async Task SendAiQuestionAsync()
    {
        var question = _ai.Question.Trim();
        if (question.Length == 0 || _ai.Cancel is not null || !AiEnabled || AiProvider.Length == 0 || AiModel.Length == 0)
        {
            return;
        }

        var context = new AiContext(Solutions.Solution?.Directory);
        var attachments = new List<AiAttachment>();
        var refused = new List<string>();
        AiTarget? target = null;
        async Task Attach(string kind, string path, string content, string label)
        {
            var (attachment, why) = await context.AttachAsync(kind, path, content, label, CancellationToken.None).ConfigureAwait(true);
            if (attachment is not null)
            {
                attachments.Add(attachment);
            }
            else if (why is not null && !refused.Contains(why))
            {
                refused.Add(why);
            }
        }

        if (ActiveEditor is { Controller: { } controller } editor)
        {
            var textEditor = controller.Editor;
            var path = editor.Model.Path;
            var name = Path.GetFileName(path);
            if (_ai.IncludeSelection && textEditor.SelectionLength > 0)
            {
                var start = textEditor.Document.GetLocation(textEditor.SelectionStart);
                var end = textEditor.Document.GetLocation(textEditor.SelectionStart + textEditor.SelectionLength);
                await Attach("selection", path, textEditor.SelectedText, $"The selection in {name} (lines {start.Line}–{end.Line})").ConfigureAwait(true);
                target = new AiTarget(path, editor.Model.Uri, start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1, WholeFile: false);
            }
            else if (_ai.IncludeFile)
            {
                await Attach("file", path, editor.Model.Text.Text, name).ConfigureAwait(true);
                var lines = editor.Model.Text.LineCount;
                target = new AiTarget(path, editor.Model.Uri, 0, 0, lines - 1, editor.Model.Text.Lines[lines - 1].Length, WholeFile: true);
            }

            if (_ai.IncludeDiagnostics && Languages.Diagnostics.For(editor.Model.Uri) is { Count: > 0 } diagnostics)
            {
                var text = string.Join('\n', diagnostics.Take(50).Select(d => $"line {d.Range.Start.Line + 1}: {d.Message}"));
                await Attach("diagnostics", path, text, $"Problems in {name}").ConfigureAwait(true);
            }
        }

        if (_ai.IncludeOpenFiles)
        {
            foreach (var document in _models.Values.Where(m => ActiveEditor?.Model != m).Take(10))
            {
                await Attach("file", document.Path, document.Text.Text, Path.GetFileName(document.Path)).ConfigureAwait(true);
            }
        }

        if (_ai.IncludeBuild && Builds.Diagnostics.Where(d => d.Severity == "error").ToList() is { Count: > 0 } errors)
        {
            // A file the boundary refuses keeps its error out too.
            var allowed = new List<string>();
            foreach (var error in errors.Take(50))
            {
                if (error.File is null || await context.RefusalAsync(error.File, CancellationToken.None).ConfigureAwait(true) is null)
                {
                    allowed.Add($"{(error.File is null ? string.Empty : Path.GetFileName(error.File) + $"({error.Line},{error.Column}): ")}{error.Code} {error.Message}");
                }
            }

            if (allowed.Count > 0)
            {
                attachments.Add(new AiAttachment("build", "The last build's errors", string.Join('\n', allowed)));
            }
        }

        var history = _ai.Turns.Select(t => new AiMessageDto(t.Role, t.Text)).ToList();
        var messages = AiContext.Messages(history, attachments, question);
        _ai.Turns.Add(new AiTurn("user", question, [.. attachments.Select(a => a.Label)], target));
        _ai.Question = string.Empty;
        _ai.Status = refused.Count > 0 ? "Not sent: " + string.Join(" ", refused) : null;
        var streaming = _ai.Streaming = new StringBuilder();
        using var cancel = _ai.Cancel = new CancellationTokenSource();
        UpdateAiStatus();
        _ai.Changed?.Invoke();
        var pending = 0;
        try
        {
            await Plugins.ChatAsync(AiProvider, AiModel, messages, text =>
            {
                lock (streaming)
                {
                    streaming.Append(text);
                }

                // Redraw at most a few times a second while the answer streams.
                if (Interlocked.Exchange(ref pending, 1) == 0)
                {
                    _ = Task.Delay(150).ContinueWith(_ =>
                    {
                        Interlocked.Exchange(ref pending, 0);
                        Ui.Post(() => _ai.Changed?.Invoke());
                    }, TaskScheduler.Default);
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
                answer = streaming.ToString();
            }

            if (answer.Length > 0)
            {
                _ai.Turns.Add(new AiTurn("assistant", answer, [], target));
            }

            _ai.Streaming = null;
            _ai.Cancel = null;
            UpdateAiStatus();
            _ai.Changed?.Invoke();
        }
    }

    /// <summary>Applies a code block to the selection or file its question was about, through the refactoring preview.</summary>
    private async Task ApplyAiCodeAsync(string id)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var turnIndex) || !int.TryParse(parts[2], out var blockIndex) || turnIndex >= _ai.Turns.Count)
        {
            return;
        }

        var turn = _ai.Turns[turnIndex];
        var code = SplitAnswer(turn.Text).Where(p => p.Code).Select(p => p.Text).ElementAtOrDefault(blockIndex);
        if (code is null)
        {
            return;
        }

        if (turn.Target is not { } target)
        {
            StatusBar.ShowMessage("The question wasn't about a file: copy the code where it belongs.", TimeSpan.FromSeconds(5));
            return;
        }

        var range = new LanguageServices.Protocol.Range(new Position(target.StartLine, target.StartCharacter), new Position(target.EndLine, target.EndCharacter));
        var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(target.Uri, null), [new TextEdit(range, target.WholeFile ? code + "\n" : code)])]);
        await PreviewAndApplyAsync(target.WholeFile ? $"Apply to {Path.GetFileName(target.Path)}" : $"Apply to the selection in {Path.GetFileName(target.Path)}", edit).ConfigureAwait(true);
    }

    internal sealed class AiChatView : UserControl
    {
        private readonly DeclarativeView _view = new();

        public AiChatView(ShellHost shell)
        {
            Content = new ScrollViewer { Content = _view };
            shell._ai.Changed = () => _view.Root = shell.AiChatRoot();
            _view.Raised += (id, kind, value, row) => _ = shell.OnAiChatEventAsync(id, kind, value);
            _view.Root = shell.AiChatRoot();
        }

        public DeclarativeView View => _view;
    }
}
