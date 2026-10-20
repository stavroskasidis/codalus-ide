using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;

namespace Codalus.Shell;

/// <summary>
/// AI assistance (M28a, ADR 0062): the language model providers, the data boundary (<see cref="AiService"/>), the AI Chat
/// window with the context it gathers from the editor, the Error List, the build and the solution, applying code through
/// the refactoring preview, choosing and configuring a provider, and the status bar's indicator.
/// </summary>
public sealed partial class ShellHost : IAiChatHost
{
    private const string AiStatusItem = "codalus.ai";

    /// <summary>The most Error List rows that go with a question.</summary>
    private const int MaxAiDiagnostics = 50;

    /// <summary>The end of the build output that goes with a question.</summary>
    private const int MaxAiBuildOutput = 8_000;

    public LanguageModelService LanguageModels { get; private set; } = null!;

    public AiService Ai { get; private set; } = null!;

    public AiChatViewModel AiChat { get; private set; } = null!;

    /// <summary>AI inline suggestions (M28b), used by each editor.</summary>
    public AiCompletions AiCompletions { get; private set; } = null!;

    /// <summary>AI next-edit suggestions (M28b), used by each editor.</summary>
    public AiNextEdit AiNextEdit { get; private set; } = null!;

    private void InitializeAi()
    {
        Workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        LanguageModels = new LanguageModelService(Plugins);
        Ai = new AiService(Workbench.Settings, LanguageModels, Workbench.Output, () => Solutions.Solution?.Directory, () => Solutions.Solution?.Trusted ?? true);
        InitializeExternalAgents();
        AiChat = new AiChatViewModel(this);
        AiCompletions = new AiCompletions(Ai, Workbench.Settings);
        AiNextEdit = new AiNextEdit(Ai, Workbench.Settings);
        Ai.Changed += (_, _) => Ui.Post(UpdateAiStatus);
        Ai.BusyChanged += (_, _) => Ui.Post(UpdateAiStatus);
        Workbench.Settings.WorkspaceChanged += (_, _) => Ui.Post(() =>
        {
            UpdateAiStatus();
            AiChat.Refresh();
        });
        UpdateAiStatus();
    }

    /// <summary>The status bar's indicator: shown while AI is on, with the provider and model, and "working" during a request.</summary>
    private void UpdateAiStatus()
    {
        Workbench.Context.Set("aiEnabled", Ai.IsEnabled);
        AiUi.Current.IsEnabled = Ai.IsEnabled;
        if (Ai.State == AiState.Off)
        {
            Workbench.StatusBar.Remove(AiStatusItem);
            return;
        }

        var label = Ai.State == AiState.NoProvider
            ? Localizer.Get("msg.ai.statusNoProvider", "AI: no provider")
            : Ai.IsBusy ? Localizer.Get("msg.ai.statusWorking", "AI: working…") : Localizer.Format("msg.ai.statusReady", "AI: {0}", Ai.Provider?.Label ?? Ai.ProviderId!);
        var tooltip = Ai.State == AiState.NoProvider
            ? Localizer.Get("msg.ai.statusNoProviderTip", "AI assistance is on, but no provider is chosen: nothing is sent. Click to open AI Chat.")
            : Localizer.Format("msg.ai.statusReadyTip", "AI assistance sends what you ask to {0}{1}. Click to open AI Chat.", Ai.Provider?.Label ?? Ai.ProviderId!, Ai.ModelId is { } model ? " (" + model + ")" : "");
        Workbench.StatusBar.Set(new StatusBarItem(AiStatusItem, "$(sparkle) " + label, StatusBarAlignment.Right, 60, ContributionSource.Shell.Id)
        {
            Tooltip = tooltip,
            AccessibleName = label,
            Command = "ai.chat.show",
        });
    }

    public void ShowAiChatWindow() => ShowAiChat();

    public void ShowAiChat()
    {
        Layout.ShowToolWindow(AiChatViewModel.ToolWindowId, focus: true);
        AiChat.Refresh();
    }

    /// <summary>Opens AI Chat with <paramref name="question"/> typed in (Ask AI about the selection).</summary>
    public void AskAi(string question)
    {
        ShowAiChat();
        AiChat.Input = question;
    }

    /// <summary>Opens AI Chat and asks <paramref name="question"/> about the selection at once (Explain the Selection).</summary>
    public void AskAiNow(string question)
    {
        AskAi(question);
        _ = AiChat.SendAsync();
    }

    public Task TurnOnAiAsync() => Workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);

    public async Task TurnOffAiForWorkspaceAsync()
    {
        if (Solutions.Solution is null)
        {
            ShowMessage(Localizer.Get("msg.ai.openASolutionFirst", "Open a solution or folder first: the switch is in its .codalus/settings.json."));
            return;
        }

        await Ai.SetOffForWorkspaceAsync(!Ai.IsOffForWorkspace, CancellationToken.None).ConfigureAwait(true);
        ShowMessage(Ai.IsOffForWorkspace
            ? Localizer.Get("msg.ai.turnedOffForWorkspace", "AI assistance is off for this workspace.")
            : Localizer.Get("msg.ai.turnedOnForWorkspace", "AI assistance follows your settings in this workspace again."));
    }

    IEditorServices IAiChatHost.EditorServices => this;

    /// <summary>
    /// Opens a file an agent's step worked on (M34h): a path relative to the workspace, as the agent's tools give it, at
    /// its line when it has one. The path comes from the model, so only a file under the workspace's root opens: the
    /// agent's tools are confined to it too (ADR 0063). Its existence is checked off the UI thread.
    /// </summary>
    public async Task OpenAiLocationAsync(string path, int? line)
    {
        if (Solutions.Solution?.Directory is not { } root || ResolveInWorkspace(root, path) is not { } full)
        {
            ShowMessage(Localizer.Format("msg.ai.locationOutside", "{0} isn't in the workspace, so it doesn't open from AI Chat.", path));
            return;
        }

        if (!await Task.Run(() => Codalus.Workbench.FileSystem.FileSystems.FileExistsAsync(full, CancellationToken.None)).ConfigureAwait(true))
        {
            ShowMessage(Localizer.Format("msg.ai.locationNotFound", "{0} isn't in the workspace any more.", path));
            return;
        }

        await OpenFileAsync(full, at: line is > 0 ? new Position(line.Value - 1, 0) : null).ConfigureAwait(true);
    }

    /// <summary>
    /// <paramref name="path"/> (relative to <paramref name="root"/>, or rooted) as a full path under <paramref name="root"/>,
    /// or null: a UNC path, a rooted path elsewhere and <c>..</c> out of the root are refused from the text alone, without
    /// touching the disk or the network.
    /// </summary>
    internal static string? ResolveInWorkspace(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var workspace = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !full.StartsWith(@"\\", StringComparison.Ordinal) && full.Length > workspace.Length && Codalus.Workbench.FileSystem.FileSystems.IsUnder(full, workspace) ? full : null;
    }

    public void ShowAiLog()
    {
        var channel = Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI");
        Workbench.Output.Show(channel);
    }

    public async Task CopyTextAsync(string text)
    {
        if (Window?.Clipboard is { } clipboard)
        {
            await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
            ShowMessage(Localizer.Get("msg.ai.copied", "Copied."));
        }
    }

    // ------------------------------------------------------------------ providers

    /// <summary>Picks the provider (the declared ones, local ones marked), then its model.</summary>
    public async Task ChooseAiProviderAsync()
    {
        var providers = LanguageModels.Providers;
        if (providers.Count == 0)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.ai.noProviders", "No AI provider plugin is installed or turned on."));
            return;
        }

        var items = providers.Select(p => new QuickPickItem(p.Label)
        {
            Detail = p.IsLocal ? Localizer.Get("msg.ai.onThisMachine", "on this machine") : p.Description,
            Icon = "sparkle",
            Tag = p.Id,
        }).ToList();
        var active = Math.Max(0, providers.ToList().FindIndex(p => p.Id == Ai.ProviderId));
        if (await Palette.ShowAsync(items, Localizer.Get("msg.ai.whichProvider", "Which provider gets your questions? Nothing is sent before you choose."), active).ConfigureAwait(true) is not { Tag: string providerId })
        {
            return;
        }

        if (providerId != Ai.ProviderId)
        {
            await Workbench.Settings.SetAsync(AiService.ModelSetting, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }

        await Workbench.Settings.SetAsync(AiService.ProviderSetting, providerId, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        if (!await EnsureAiProviderReadyAsync(providerId).ConfigureAwait(true))
        {
            return;
        }

        await ChooseAiModelAsync(providerId).ConfigureAwait(true);
    }

    /// <summary>Configures the provider when it isn't ready (a key); false when it still isn't.</summary>
    private async Task<bool> EnsureAiProviderReadyAsync(string providerId)
    {
        try
        {
            var status = await LanguageModels.GetStatusAsync(providerId, CancellationToken.None).ConfigureAwait(true);
            if (!status.IsReady)
            {
                status = await LanguageModels.ConfigureAsync(providerId, CancellationToken.None).ConfigureAwait(true);
            }

            if (!status.IsReady)
            {
                ShowMessage(status.Message ?? Localizer.Get("msg.ai.providerNotReady", "The provider isn't ready."));
            }

            return status.IsReady;
        }
        catch (LanguageModelRequestException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return false;
        }
    }

    private async Task ChooseAiModelAsync(string providerId)
    {
        IReadOnlyList<Codalus.Plugins.Protocol.LanguageModelDto> models;
        try
        {
            models = await LanguageModels.GetModelsAsync(providerId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (LanguageModelRequestException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

        if (models.Count == 0)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.ai.noModels", "The provider offers no models."));
            return;
        }

        var items = models.Select(m => new QuickPickItem(m.Name)
        {
            Detail = m.Name == m.Id ? null : m.Id,
            Tag = m.Id,
        }).ToList();
        var active = Math.Max(0, models.ToList().FindIndex(m => m.Id == Ai.ModelId));
        if (await Palette.ShowAsync(items, Localizer.Get("msg.ai.whichModel", "Which model?"), active).ConfigureAwait(true) is { Tag: string modelId })
        {
            await Workbench.Settings.SetAsync(AiService.ModelSetting, modelId, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public async Task ConfigureAiProviderAsync()
    {
        if (Ai.ProviderId is not { } providerId)
        {
            await ChooseAiProviderAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            var status = await LanguageModels.ConfigureAsync(providerId, CancellationToken.None).ConfigureAwait(true);
            ShowMessage(status.IsReady
                ? Localizer.Format("msg.ai.providerReady", "{0} is ready.", Ai.Provider?.Label ?? providerId)
                : status.Message ?? Localizer.Get("msg.ai.providerNotReady", "The provider isn't ready."));
        }
        catch (LanguageModelRequestException ex)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------------ context

    public (IReadOnlyList<AiContextItem> Items, AiSelectionAnchor? Anchor) GatherAiContext(AiContextChoice choice)
    {
        var items = new List<AiContextItem>();
        AiSelectionAnchor? anchor = null;
        var active = ActiveEditor;
        var activeModel = active?.Model;
        if (choice.Selection && active?.Controller?.Editor is { SelectionLength: > 0 } editor && activeModel is not null)
        {
            var start = editor.SelectionStart;
            var text = editor.SelectedText;
            anchor = new AiSelectionAnchor(activeModel.Path, start, text);
            items.Add(new AiContextItem(AiContextKind.Selection, activeModel.Name, text)
            {
                Path = activeModel.Path,
                StartLine = activeModel.Text.GetLineByOffset(start).LineNumber,
                Language = activeModel.LanguageId,
            });
        }

        if (choice.CurrentFile && activeModel is not null)
        {
            items.Add(new AiContextItem(AiContextKind.CurrentFile, activeModel.Name, activeModel.Text.Text) { Path = activeModel.Path, Language = activeModel.LanguageId });
        }

        var openModels = Editors.Select(e => e.Model).Distinct().ToList();
        if (choice.OpenFiles)
        {
            foreach (var model in openModels.Where(m => m != activeModel))
            {
                items.Add(new AiContextItem(AiContextKind.OpenFile, model.Name, model.Text.Text) { Path = model.Path, Language = model.LanguageId });
            }
        }

        if (choice.Errors && ErrorList is not null)
        {
            var files = new HashSet<string>(openModels.Select(m => m.Path), SolutionPaths.Comparer);
            var rows = ErrorList.Rows.Where(r => files.Count == 0 || files.Contains(r.Path)).Take(MaxAiDiagnostics).ToList();
            if (rows.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var row in rows)
                {
                    sb.Append(row.File).Append('(').Append(row.Line.ToString(CultureInfo.InvariantCulture)).Append(',').Append(row.Column.ToString(CultureInfo.InvariantCulture)).Append("): ")
                        .Append(row.Icon).Append(' ').Append(row.Code).Append(": ").AppendLine(row.Description);
                }

                var label = rows.Count == 1
                    ? Localizer.Get("msg.ai.oneProblem", "1 error or warning")
                    : Localizer.Format("msg.ai.problems", "{0} errors and warnings", rows.Count);
                items.Add(new AiContextItem(AiContextKind.Diagnostics, label, sb.ToString()));
            }
        }

        if (choice.BuildOutput && Workbench.Output.Channels.FirstOrDefault(c => c.Id == BuildService.OutputChannelId) is { } build && build.GetText() is { Length: > 0 } output)
        {
            items.Add(new AiContextItem(AiContextKind.BuildOutput, Localizer.Get("msg.ai.buildOutput", "build output"), output.Length > MaxAiBuildOutput ? output[^MaxAiBuildOutput..] : output));
        }

        if (choice.Solution && Solutions.Solution is { } solution)
        {
            var sb = new StringBuilder();
            sb.Append(Path.GetFileName(solution.Path)).AppendLine();
            foreach (var project in Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("- ").Append(project.Name);
                if (project.TargetFrameworks.Length > 0)
                {
                    sb.Append(" (").Append(string.Join(", ", project.TargetFrameworks)).Append(')');
                }

                if (project.OutputType is { Length: > 0 } outputType)
                {
                    sb.Append(", ").Append(outputType);
                }

                sb.AppendLine();
            }

            items.Add(new AiContextItem(AiContextKind.Solution, Path.GetFileName(solution.Path), sb.ToString()));
        }

        return (items, anchor);
    }

    // ------------------------------------------------------------------ applying code

    public async Task ApplyAiCodeAsync(AiChatTurn turn, AiCodeBlock block, bool insertOnly)
    {
        var anchor = turn.Tag as AiSelectionAnchor;

        // The file: the one the block names (open, or in the solution's folder), else the selection's, else the active one.
        CodeDocument? model = null;
        string? path = null;
        if (!insertOnly && block.Path is { Length: > 0 } named)
        {
            model = _models.Values.FirstOrDefault(m => AiCodeBlocks.NamesFile(named, m.Path));
            path = model?.Path ?? ResolveNamedFile(named);
        }

        if (path is null && anchor is not null && _models.TryGetValue(anchor.Path, out var anchored) && !insertOnly)
        {
            model = anchored;
            path = anchored.Path;
        }

        if (path is null && ActiveEditor?.Model is { } activeModel)
        {
            model = activeModel;
            path = activeModel.Path;
        }

        if (path is null)
        {
            ShowMessage(Localizer.Get("msg.ai.openAFileFirst", "Open the file the code goes into first."));
            return;
        }

        var text = model?.Text.Text ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true)).Text : string.Empty);
        var code = block.Code;
        int start, length;
        var kind = insertOnly ? AiApplyKind.Insert : AiCodeBlocks.Plan(block, text, path, anchor is not null && SolutionPaths.Comparer.Equals(anchor.Path, path));
        switch (kind)
        {
            case AiApplyKind.ReplaceFile:
                (start, length) = (0, text.Length);
                break;
            case AiApplyKind.ReplaceSelection when FindAnchor(text, anchor!) is { } found:
                (start, length) = (found, anchor!.Text.Length);

                // The selection's own line ending stays as it was, and its indentation is the code's.
                if (!anchor.Text.EndsWith('\n'))
                {
                    code = code.TrimEnd('\n', '\r');
                }

                code = AiCodeBlocks.IndentLike(code, anchor.Text);

                break;
            default:
                start = model is not null && ReferenceEquals(ActiveEditor?.Model, model) && ActiveEditor?.Controller?.Editor is { } caretEditor ? caretEditor.CaretOffset : text.Length;
                length = 0;
                kind = AiApplyKind.Insert;
                break;
        }

        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            code = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var range = new Codalus.LanguageServices.Protocol.Range(PositionIn(text, start), PositionIn(text, start + length));
        var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), model?.Version), [new TextEdit(range, code)])]);
        if (kind == AiApplyKind.Insert && insertOnly)
        {
            await ApplyWorkspaceEditAsync(edit).ConfigureAwait(true);
            return;
        }

        var title = kind switch
        {
            AiApplyKind.ReplaceFile => Localizer.Format("msg.ai.replaceFile", "Replace {0} with the code?", Path.GetFileName(path)),
            AiApplyKind.ReplaceSelection => Localizer.Get("msg.ai.replaceSelection", "Replace the selection with the code?"),
            _ => Localizer.Format("msg.ai.insertCode", "Insert the code into {0}?", Path.GetFileName(path)),
        };
        await PreviewAndApplyAsync(title, edit).ConfigureAwait(true);
    }

    /// <summary>A block's <c>path=</c> under the solution's folder, when that file exists or the folder does.</summary>
    private string? ResolveNamedFile(string named)
    {
        if (Solutions.Solution?.Directory is not { } root || Path.IsPathRooted(named))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, named.Replace('/', Path.DirectorySeparatorChar)));
        return Codalus.Workbench.FileSystem.FileSystems.IsUnder(full, root) && (File.Exists(full) || Directory.Exists(Path.GetDirectoryName(full))) ? full : null;
    }

    /// <summary>Where the selection's text is now: at its old offset, else the nearest occurrence; null when it's gone.</summary>
    private static int? FindAnchor(string text, AiSelectionAnchor anchor)
    {
        if (anchor.Text.Length == 0)
        {
            return null;
        }

        if (anchor.Offset + anchor.Text.Length <= text.Length && string.CompareOrdinal(text, anchor.Offset, anchor.Text, 0, anchor.Text.Length) == 0)
        {
            return anchor.Offset;
        }

        int? best = null;
        for (var i = text.IndexOf(anchor.Text, StringComparison.Ordinal); i >= 0; i = text.IndexOf(anchor.Text, i + 1, StringComparison.Ordinal))
        {
            if (best is null || Math.Abs(i - anchor.Offset) < Math.Abs(best.Value - anchor.Offset))
            {
                best = i;
            }
        }

        return best;
    }

    private static Position PositionIn(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Position(line, Math.Min(offset, text.Length) - lineStart);
    }
}
