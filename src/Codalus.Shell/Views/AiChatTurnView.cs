using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// One turn of AI Chat (M34h, docs/design/controls/AiChat.dc.html): your question as a bubble on the right with what went
/// with it as chips, then the assistant's answer beside its avatar: who answered and when, its steps (agent mode), the
/// answer as Markdown with code blocks in the editor's colors, and how it ended. It renders when it comes into view and
/// again as the answer streams; leaving the view releases its code blocks.
/// </summary>
internal sealed class AiChatTurnView : StackPanel
{
    private static readonly FuncDataTemplate<AiStepItem> s_stepTemplate = new((_, _) => new AiStepView(), supportsRecycling: false);

    private readonly AiChatTurnItem _item;
    private readonly AiChatView _owner;
    private readonly ObservableCollection<AiStepItem> _steps = [];
    private readonly Border _stepsRail;
    private readonly ContentControl _answer = new();
    private readonly ToggleButton _thought;
    private readonly TextBlock _thoughtLabel = new();
    private readonly SelectableTextBlock _thoughtText = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ToggleButton _summary;
    private readonly TextBlock _summaryText = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _changes = new() { Spacing = 2, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
    private readonly HashSet<string> _reverted = new(StringComparer.Ordinal);
    private string? _renderedChanges;
    private bool? _renderedCompact;
    private readonly StackPanel _plan = new() { Spacing = 1, IsVisible = false };
    private readonly StackPanel _notices = new() { Spacing = 2, IsVisible = false };
    private readonly WrapPanel _status = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
    private readonly List<AiCodeBlockView> _codeBlocks = [];
    private IReadOnlyList<AiCodeBlock> _blocks = [];
    private string? _rendered;
    private bool _renderedStreaming;

    public AiChatTurnView(AiChatTurnItem item, AiChatView owner)
    {
        _item = item;
        _owner = owner;
        Spacing = 14;
        Margin = new Thickness(0, 0, 0, 14);
        var turn = item.Turn;

        // Your question, and what went with it.
        var you = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var question = new SelectableTextBlock { Text = turn.Question, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(question, Localizer.Format("msg.ai.youAsked", "You: {0}", turn.Question));
        var bubble = new Border { Child = question };
        bubble.Classes.Add("userBubble");
        you.Children.Add(bubble);
        if (Sent(turn) is { } sent)
        {
            you.Children.Add(sent);
        }

        if (turn.Images.Count > 0)
        {
            var images = Chip(
                turn.Images.Count == 1 ? Localizer.Get("msg.ai.oneImage", "1 image") : Localizer.Format("msg.ai.images", "{0} images", turn.Images.Count),
                null,
                string.Join(", ", turn.Images.Select(i => i.Name)));
            images.HorizontalAlignment = HorizontalAlignment.Right;
            you.Children.Add(images);
        }

        Children.Add(new BubbleRow { Child = you });

        // The answer, beside the assistant's avatar.
        var avatar = new Border { Child = new Icon { Kind = "sparkle", Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        avatar.Classes.Add("avatar");
        avatar.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(avatar, AccessibilityView.Raw);
        var author = new TextBlock { Text = item.Author, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var byline = new TextBlock { Text = item.Byline, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        byline.Classes.Add("muted");
        byline.Classes.Add("small");
        var who = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 2, Children = { author, byline } };
        if (turn.ExternalAgent is not null)
        {
            // An external agent (M48, ADR 0079): its own program, provider and data policy.
            var external = new Border { Child = new TextBlock { Text = Localizer.Get("msg.acp.externalAgentChip", "External agent") } };
            external.Classes.Add("chip");
            ToolTip.SetTip(external, Localizer.Get("aiChat.externalAgentUsesItsOwn", "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for."));
            who.Children.Add(external);
        }

        var steps = new ItemsControl { ItemsSource = _steps, ItemTemplate = s_stepTemplate };
        AutomationProperties.SetName(steps, Localizer.Get("msg.ai.whatTheAssistantDid", "What the assistant did"));
        _stepsRail = new Border { Child = steps, IsVisible = false };
        _stepsRail.Classes.Add("steps");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        // An external agent's thinking (collapsed), its plan and its notices (M48).
        // Its thinking as one short line ("Thought ▸", ADR 0092) that opens on a click, never a box.
        _thoughtText.Classes.Add("small");
        _thoughtText.Classes.Add("muted");
        _thoughtText.Margin = new Thickness(18, 0, 0, 0);
        _thought = Disclosure(_thoughtLabel);
        _thought.IsVisible = false;
        _thought.IsCheckedChanged += (_, _) => _thoughtText.IsVisible = _thought.IsChecked == true;
        AutomationProperties.SetName(_thought, Localizer.Get("msg.acp.thinking", "Thinking"));
        AutomationProperties.SetName(_plan, Localizer.Get("msg.acp.plan", "Plan"));

        // Compact (ADR 0092): the steps fold into one line ("Read 3 files · ran 2 commands ▸") that opens them.
        _summary = Disclosure(_summaryText);
        _summary.IsVisible = false;
        _summary.IsCheckedChanged += (_, _) => _stepsRail.IsVisible = _steps.Count > 0 && _summary.IsChecked == true;
        AutomationProperties.SetName(_summary, Localizer.Get("msg.ai.whatTheAssistantDid", "What the assistant did"));
        AutomationProperties.SetName(_changes, Localizer.Get("msg.ai.changedFilesName", "Changed files"));
        var body = new StackPanel { Spacing = 6, Children = { who, _thought, _thoughtText, _plan, _summary, _stepsRail, _answer, _changes, _notices, _status } };
        Children.Add(new AnswerRow { Children = { avatar, body } });
    }

    public AiChatTurn Turn => _item.Turn;

    /// <summary>Whether the chat folds the steps (the setting; compact when the view has no chat yet).</summary>
    private bool IsCompact => _owner.ViewModel?.IsCompact ?? true;

    /// <summary>The folded line of the steps (tests).</summary>
    internal string StepsSummary => _summary.IsVisible ? _summaryText.Text ?? "" : "";

    /// <summary>Whether the steps show (tests).</summary>
    internal bool StepsShown => _stepsRail.IsVisible;

    /// <summary>The changed files' rows (tests).</summary>
    internal Panel ChangesPanel => _changes;

    /// <summary>A small muted disclosure: a chevron that turns down when open, then <paramref name="label"/>.</summary>
    private static ToggleButton Disclosure(TextBlock label)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Classes.Add("muted");
        var chevron = new Icon { Kind = "chevron-right", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(chevron, Avalonia.Controls.Dock.Left);

        // A dock panel, not a stack: the label gets the width there is, so a long line ends in an ellipsis.
        var toggle = new ToggleButton
        {
            Content = new DockPanel { Children = { chevron, label } },
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2, 1),
            MinHeight = 0,
        };
        toggle.Classes.Add("toolbar");
        toggle.Classes.Add("disclosure");
        toggle.Classes.Add("small");
        toggle.Classes.Add("muted");
        toggle.IsCheckedChanged += (_, _) => chevron.RenderTransform = toggle.IsChecked == true ? new RotateTransform(90) : null;
        return toggle;
    }

    /// <summary>The code blocks shown (tests).</summary>
    public IReadOnlyList<AiCodeBlockView> CodeBlocks => _codeBlocks;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner.Realized(this);
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _owner.Unrealized(this);
        Release();
    }

    /// <summary>Shows the turn as it is now: its steps, the answer so far, and how it ended.</summary>
    public void Render()
    {
        var turn = _item.Turn;
        var streaming = turn.IsStreaming;
        RenderAgentParts(turn);
        RenderSteps(turn, streaming);

        var answer = turn.Answer;
        if (answer != _rendered || streaming != _renderedStreaming)
        {
            _rendered = answer;
            _renderedStreaming = streaming;
            _blocks = AiCodeBlocks.Parse(answer);
            var used = 0;
            Control CodeBlock(MarkdownCodeBlock block)
            {
                used = Math.Max(used, block.Index + 1);
                return ShowCodeBlock(block, streaming);
            }

            _answer.Content = answer.Length == 0
                ? null
                : MarkdownRenderer.Render(answer, target => _owner.ViewModel?.OpenLink(target), new MarkdownOptions
                {
                    Selectable = true,
                    CompactHeadings = true,
                    Margin = default,
                    CodeBlock = CodeBlock,
                });
            while (_codeBlocks.Count > used)
            {
                _codeBlocks[^1].Release();
                _codeBlocks.RemoveAt(_codeBlocks.Count - 1);
            }
        }

        RenderChanges(turn);
        RenderStatus(turn, answer);
    }

    /// <summary>Releases the code blocks; the next render builds them again (the turn came back into view).</summary>
    public void Release()
    {
        foreach (var block in _codeBlocks)
        {
            block.Release();
        }

        _codeBlocks.Clear();
        _rendered = null;
    }

    private void RenderSteps(AiChatTurn turn, bool streaming)
    {
        var steps = turn.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            Action<AiStepLocation> open = location => _owner.ViewModel?.OpenLocation(location);
            var step = steps[i].Id is not null ? AiStepItem.FromAcp(steps[i], open) : AiStepItem.From(steps[i], streaming, open);
            if (i >= _steps.Count)
            {
                _steps.Add(step);
            }
            else if (!_steps[i].SameAs(step))
            {
                // It keeps the user's choice; a step whose details just appeared opens as it would have (a running command's output).
                step.IsExpanded = _steps[i].HasDetails ? _steps[i].IsExpanded : step.IsExpanded;
                _steps[i] = step;
            }
        }

        while (_steps.Count > steps.Count)
        {
            _steps.RemoveAt(_steps.Count - 1);
        }

        // One line for the steps, with the one running now while the turn streams: folded in compact, open in detailed
        // (where it heads the steps, so they don't read as the thought's), and either way a click flips it.
        var compact = IsCompact;
        if (compact != _renderedCompact)
        {
            _renderedCompact = compact;
            _summary.IsChecked = !compact;
        }

        _summary.IsVisible = _steps.Count > 0;
        if (_summary.IsVisible)
        {
            var line = AiTurnSummary.Describe(steps);
            if (streaming && AiTurnSummary.Current(steps) is { } current)
            {
                var running = AiStepItem.McpTitle(current.Target).Split('\n', 2)[0];
                line += " · " + Localizer.Format("msg.ai.summary.now", "now: {0}", running.Length > 40 ? running[..40] + "…" : running);
            }

            if (_summaryText.Text != line)
            {
                _summaryText.Text = line;
                ToolTip.SetTip(_summary, line);
            }
        }

        _stepsRail.IsVisible = _steps.Count > 0 && _summary.IsChecked == true;
    }

    /// <summary>
    /// The files the turn changed (ADR 0092), once it has ended: each with its name (opens it), Diff (against what it was
    /// before the turn) and Revert; a change the agent made by itself, outside the preview, says so.
    /// </summary>
    private void RenderChanges(AiChatTurn turn)
    {
        var changes = turn.IsStreaming ? [] : AiTurnSummary.ChangedFiles(turn.Steps);
        var key = string.Join("|", changes.Select(c => c.Path + ":" + c.NewText.Length + ":" + _reverted.Contains(c.Path)));
        if (key == _renderedChanges)
        {
            return;
        }

        _renderedChanges = key;
        _changes.Children.Clear();
        _changes.IsVisible = changes.Count > 0;
        if (changes.Count == 0)
        {
            return;
        }

        var header = new TextBlock
        {
            Text = changes.Count == 1 ? Localizer.Get("msg.ai.changedOneFile", "Changed 1 file") : Localizer.Format("msg.ai.changedFiles", "Changed {0} files", changes.Count),
            FontWeight = FontWeight.SemiBold,
        };
        header.Classes.Add("small");
        _changes.Children.Add(header);
        foreach (var change in changes)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8 };
            var icon = new Icon { Kind = change.OldText is null ? "file-add" : "edit", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            var open = new Button { Content = new TextBlock { Text = System.IO.Path.GetFileName(change.Path) } };
            open.Classes.Add("link");
            ToolTip.SetTip(open, change.Path);
            AutomationProperties.SetName(open, change.Path);
            open.Click += (_, _) => _owner.ViewModel?.OpenLocation(new AiStepLocation(change.Path));
            row.Children.Add(open);
            if (_reverted.Contains(change.Path))
            {
                row.Children.Add(Muted(Localizer.Get("msg.ai.reverted", "reverted")));
            }
            else
            {
                row.Children.Add(Muted("·"));
                var diff = new Button { Content = Localizer.Get("msg.ai.diff", "Diff") };
                diff.Classes.Add("link");
                AutomationProperties.SetName(diff, Localizer.Format("msg.ai.diffOf", "Compare {0} with before", change.Path));
                diff.Click += (_, _) => _ = _owner.ViewModel?.OpenChangeDiffAsync(change);
                row.Children.Add(diff);
                row.Children.Add(Muted("·"));
                var revert = new Button { Content = Localizer.Get("msg.ai.revert", "Revert") };
                revert.Classes.Add("link");
                AutomationProperties.SetName(revert, Localizer.Format("msg.ai.revertOf", "Revert {0}", change.Path));
                revert.Click += async (_, _) =>
                {
                    if (_owner.ViewModel is { } vm && await vm.RevertChangeAsync(change).ConfigureAwait(true))
                    {
                        _reverted.Add(change.Path);
                        RenderChanges(_item.Turn);
                    }
                };
                row.Children.Add(revert);
                if (!change.Reviewed)
                {
                    var note = Muted(Localizer.Get("msg.ai.notReviewed", "written by the agent, not previewed"));
                    note.Classes.Add("small");
                    row.Children.Add(note);
                }
            }

            _changes.Children.Add(row);
        }
    }

    /// <summary>An external agent's thinking, plan and notices (M48); the built-in assistant has none.</summary>
    private void RenderAgentParts(AiChatTurn turn)
    {
        var thought = turn.Thought;
        _thought.IsVisible = thought.Length > 0;
        _thoughtLabel.Text = turn.IsStreaming && turn.Answer.Length == 0 && turn.Steps.Count == 0
            ? Localizer.Get("msg.acp.thinkingNow", "Thinking…")
            : Localizer.Get("msg.acp.thought", "Thought");
        if (_thoughtText.Text != thought)
        {
            _thoughtText.Text = thought;
        }

        _plan.Children.Clear();
        if (turn.Plan is { Count: > 0 } plan)
        {
            var title = new TextBlock { Text = Localizer.Get("msg.acp.plan", "Plan"), FontWeight = FontWeight.SemiBold };
            title.Classes.Add("small");
            _plan.Children.Add(title);
            foreach (var entry in plan)
            {
                var line = new TextBlock { Text = (entry.Status == "completed" ? "☑ " : entry.Status == "in_progress" ? "◐ " : "☐ ") + entry.Content, TextWrapping = TextWrapping.Wrap };
                line.Classes.Add("small");
                AutomationProperties.SetName(line, entry.Content + " (" + entry.Status.Replace('_', ' ') + ")");
                if (entry.Status == "completed")
                {
                    line.Classes.Add("muted");
                }

                _plan.Children.Add(line);
            }
        }

        _plan.IsVisible = _plan.Children.Count > 0;
        _notices.Children.Clear();
        foreach (var notice in turn.Notices)
        {
            var icon = new Icon { Kind = notice.Severity is "warning" or "error" ? notice.Severity : "info", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
            var text = new TextBlock { Text = notice.Text, TextWrapping = TextWrapping.Wrap };
            text.Classes.Add("small");
            text.Classes.Add(notice.Severity == "error" ? "error" : "muted");
            var line = new DockPanel { Children = { icon, text } };
            DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
            _notices.Children.Add(line);
        }

        _notices.IsVisible = _notices.Children.Count > 0;
    }

    private AiCodeBlockView ShowCodeBlock(MarkdownCodeBlock block, bool streaming)
    {
        var index = block.Index;
        AiCodeBlockView view;
        if (index < _codeBlocks.Count)
        {
            view = _codeBlocks[index];
            if (view.Parent is Panel previous)
            {
                // It moves from the answer's previous rendering into this one.
                previous.Children.Remove(view);
            }
        }
        else
        {
            view = new AiCodeBlockView(_owner.ViewModel!.EditorServices, _owner.ViewModel.EditorFont);
            _codeBlocks.Add(view);
        }

        var parsed = index < _blocks.Count ? _blocks[index] : null;
        view.Show(parsed?.Language ?? block.Info.Split(' ', 2)[0], parsed?.Path, block.Code, streaming, block.IsClosed);
        return view;
    }

    /// <summary>A code block of the answer as it is now, by its index (the buttons act on the latest text).</summary>

    private void RenderStatus(AiChatTurn turn, string answer)
    {
        _status.Children.Clear();
        if (turn.IsStreaming)
        {
            _status.Children.Add(Muted(answer.Length == 0 && turn.Steps.Count == 0
                ? Localizer.Get("msg.ai.waiting", "Waiting for the answer…")
                : Localizer.Get("msg.ai.writing", "Writing…")));
        }
        else if (turn.Error is { } error)
        {
            var icon = new Icon { Kind = "error", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 6, 0) };
            var text = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
            text.Classes.Add("error");
            var line = new DockPanel { Children = { icon, text } };
            DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
            _status.Children.Add(line);
            if (turn.NeedsConfiguration)
            {
                var configure = new Button { Content = Localizer.Get("msg.ai.configure", "Configure") };
                configure.Classes.Add("iconText");
                configure.Classes.Add("compact");
                ButtonIcon.SetKind(configure, "gear");
                AutomationProperties.SetName(configure, Localizer.Get("msg.ai.configureTheProvider", "Configure the provider"));
                configure.Click += (_, _) => _ = _owner.ViewModel?.ConfigureAsync();
                _status.Children.Add(configure);
            }
        }
        else if (turn.FinishReason == "stopped")
        {
            _status.Children.Add(Muted(Localizer.Get("msg.ai.stopped", "Stopped.")));
        }
        else if (turn.FinishReason == "length")
        {
            _status.Children.Add(Muted(Localizer.Get("msg.ai.cut", "The answer reached its length limit.")));
        }
        else if (turn.FinishReason == "max_turn_requests")
        {
            _status.Children.Add(Muted(Localizer.Get("msg.acp.turnLimit", "The agent reached its limit of requests for one turn.")));
        }

        _status.IsVisible = _status.Children.Count > 0;
    }

    /// <summary>What went with the question (chips), what didn't and why, and how many secrets were redacted.</summary>
    private static WrapPanel? Sent(AiChatTurn turn)
    {
        var context = turn.Context;
        if (context.Included.Count == 0 && context.Excluded.Count == 0 && context.Redactions == 0)
        {
            return null;
        }

        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(row, AiChatViewModel.ContextSummary(turn));
        if (context.Included.Count > 0)
        {
            var label = new TextBlock { Text = Localizer.Get("msg.ai.sentLabel", "Sent:"), VerticalAlignment = VerticalAlignment.Center };
            label.Classes.Add("muted");
            label.Classes.Add("small");
            row.Children.Add(label);
            foreach (var name in context.Included.Select(i => i.Label).Distinct(StringComparer.Ordinal))
            {
                row.Children.Add(Chip(name, null, name));
            }
        }

        foreach (var excluded in context.Excluded)
        {
            row.Children.Add(Chip(
                Localizer.Format("msg.ai.notSent", "Not sent: {0}", excluded.Item.Label),
                "warning",
                Localizer.Format("msg.ai.notSentWhy", "{0} wasn't sent: {1}", excluded.Item.Label, excluded.Reason)));
        }

        if (context.Redactions > 0)
        {
            var redacted = Localizer.Format("msg.ai.redacted", "{0} secret(s) redacted", context.Redactions);
            row.Children.Add(Chip(redacted, "info", redacted));
        }

        return row;
    }

    private static Border Chip(string text, string? kind, string tooltip)
    {
        var chip = new Border { Child = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis } };
        chip.Classes.Add("chip");
        if (kind is not null)
        {
            chip.Classes.Add(kind);
        }

        ToolTip.SetTip(chip, tooltip);
        return chip;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("muted");
        return block;
    }

    /// <summary>
    /// The answer's row: the avatar (its first child) in a 36 px column, the answer (its second) beside it. In a pane
    /// narrower than <see cref="AvatarMinWidth"/> the answer takes the whole width, so its code keeps room.
    /// </summary>
    private sealed class AnswerRow : Panel
    {
        private const double AvatarMinWidth = 320;
        private const double AvatarColumn = 36;

        private bool _avatar = true;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Children is not [var avatar, var body])
            {
                return default;
            }

            // Hidden by its opacity, not IsVisible, so the row's own measure doesn't invalidate it.
            _avatar = double.IsInfinity(availableSize.Width) || availableSize.Width >= AvatarMinWidth;
            avatar.Opacity = _avatar ? 1 : 0;
            avatar.Measure(Size.Infinity);
            var indent = _avatar ? AvatarColumn : 0;
            body.Measure(new Size(Math.Max(0, availableSize.Width - indent), availableSize.Height));
            var width = double.IsInfinity(availableSize.Width) ? body.DesiredSize.Width + indent : availableSize.Width;
            return new Size(width, Math.Max(_avatar ? avatar.DesiredSize.Height : 0, body.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children is [var avatar, var body])
            {
                var indent = _avatar ? AvatarColumn : 0;
                avatar.Arrange(new Rect(avatar.DesiredSize));
                body.Arrange(new Rect(indent, 0, Math.Max(0, finalSize.Width - indent), finalSize.Height));
            }

            return finalSize;
        }
    }

    /// <summary>Your message's row: the bubble on the right, at most 86 % of the width (the design's).</summary>
    private sealed class BubbleRow : Decorator
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child is null)
            {
                return default;
            }

            var width = double.IsInfinity(availableSize.Width) ? availableSize.Width : availableSize.Width * 0.86;
            Child.Measure(new Size(width, availableSize.Height));
            return new Size(double.IsInfinity(availableSize.Width) ? Child.DesiredSize.Width : availableSize.Width, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child is { } child)
            {
                var width = Math.Min(child.DesiredSize.Width, finalSize.Width);
                child.Arrange(new Rect(finalSize.Width - width, 0, width, child.DesiredSize.Height));
            }

            return finalSize;
        }
    }
}
