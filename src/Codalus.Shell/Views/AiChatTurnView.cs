using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
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
        if (turn.IsAgent)
        {
            var mode = new Border { Child = new TextBlock { Text = Localizer.Get("msg.ai.agentChip", "Agent") } };
            mode.Classes.Add("chip");
            mode.Classes.Add("info");
            ToolTip.SetTip(mode, Localizer.Get("msg.ai.askedInAgentMode", "Asked in agent mode"));
            who.Children.Add(mode);
        }

        var steps = new ItemsControl { ItemsSource = _steps, ItemTemplate = s_stepTemplate };
        AutomationProperties.SetName(steps, Localizer.Get("msg.ai.whatTheAssistantDid", "What the assistant did"));
        _stepsRail = new Border { Child = steps, IsVisible = false };
        _stepsRail.Classes.Add("steps");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var body = new StackPanel { Spacing = 8, Children = { who, _stepsRail, _answer, _status } };
        Children.Add(new AnswerRow { Children = { avatar, body } });
    }

    public AiChatTurn Turn => _item.Turn;

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
        RenderSteps(turn, streaming);

        var answer = turn.Answer;
        if (answer != _rendered || streaming != _renderedStreaming)
        {
            _rendered = answer;
            _renderedStreaming = streaming;
            _blocks = AiCodeBlocks.Parse(answer);
            var used = 0;
            Control CodeBlock(int index, string info, string code)
            {
                used = Math.Max(used, index + 1);
                return ShowCodeBlock(index, info, code, streaming);
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
            var step = AiStepItem.From(steps[i], streaming, location => _owner.ViewModel?.OpenLocation(location));
            if (i >= _steps.Count)
            {
                _steps.Add(step);
            }
            else if (!_steps[i].SameAs(step))
            {
                step.IsExpanded = _steps[i].IsExpanded;
                _steps[i] = step;
            }
        }

        while (_steps.Count > steps.Count)
        {
            _steps.RemoveAt(_steps.Count - 1);
        }

        _stepsRail.IsVisible = _steps.Count > 0;
    }

    private AiCodeBlockView ShowCodeBlock(int index, string info, string code, bool streaming)
    {
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
            var at = index;
            view = new AiCodeBlockView(
                _owner.ViewModel!.EditorServices,
                _owner.ViewModel.EditorFont,
                () => { if (Block(at) is { } block) { _ = _owner.ViewModel?.CopyAsync(block.Code); } },
                () => { if (Block(at) is { } block) { _ = _owner.ViewModel?.InsertAsync(_item.Turn, block); } },
                () => { if (Block(at) is { } block) { _ = _owner.ViewModel?.ApplyAsync(_item.Turn, block); } });
            _codeBlocks.Add(view);
        }

        var parsed = index < _blocks.Count ? _blocks[index] : null;
        view.Show(parsed?.Language ?? info.Split(' ', 2)[0], parsed?.Path, code, streaming);
        return view;
    }

    /// <summary>A code block of the answer as it is now, by its index (the buttons act on the latest text).</summary>
    private AiCodeBlock? Block(int index) => AiCodeBlocks.Parse(_item.Turn.Answer) is var blocks && index < blocks.Count ? blocks[index] : null;

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
