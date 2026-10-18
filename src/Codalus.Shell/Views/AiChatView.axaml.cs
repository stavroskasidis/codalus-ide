using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// The AI Chat window (M28a): turns rendered as Markdown with Copy, Insert and Apply under each code block, the answer
/// re-rendered as it streams (at most every 100 ms), Enter sends and Shift+Enter starts a new line.
/// </summary>
public sealed partial class AiChatView : UserControl, IPaneView
{
    private static readonly TimeSpan s_renderInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<AiChatTurn, TurnView> _turns = [];
    private readonly HashSet<AiChatTurn> _dirty = [];
    private readonly DispatcherTimer _render;
    private AiChatViewModel? _vm;

    public AiChatView()
    {
        InitializeComponent();
        _render = new DispatcherTimer { Interval = s_renderInterval };
        _render.Tick += (_, _) => RenderDirty();
        Provider.Click += (_, _) => _ = _vm?.ChooseProviderAsync();
        Configure.Click += (_, _) => _ = _vm?.ConfigureAsync();
        NewChat.Click += (_, _) => _vm?.NewChat();
        StopButton.Click += (_, _) => _vm?.Stop();
        Log.Click += (_, _) => _vm?.ShowLog();
        TurnOn.Click += (_, _) => _ = _vm?.TurnOnAsync();
        Send.Click += (_, _) => _ = _vm?.SendAsync();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        CommandList.SelectionChanged += (_, _) =>
        {
            if (CommandList.SelectedItem is AiCommandSuggestion suggestion)
            {
                UseCommand(suggestion);
            }
        };
        InputBox.GotFocus += (_, _) => _vm?.UpdateContextPreview();
        DataContextChanged += (_, _) => Attach(DataContext as AiChatViewModel);
    }

    public void FocusPrimary()
    {
        if (InputBox.IsVisible)
        {
            InputBox.Focus();
        }
        else
        {
            TurnOn.Focus();
        }
    }

    private void Attach(AiChatViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.TurnChanged -= OnTurnChanged;
            _vm.Cleared -= OnCleared;
        }

        _vm = vm;
        _turns.Clear();
        TurnsPanel.Children.Clear();
        if (vm is null)
        {
            return;
        }

        vm.TurnChanged += OnTurnChanged;
        vm.Cleared += OnCleared;
        foreach (var turn in vm.Chat.Turns)
        {
            OnTurnChanged(this, turn);
        }

        vm.UpdateContextPreview();
    }

    /// <summary>An external agent's slash command goes into the input, ready for its argument.</summary>
    private void UseCommand(AiCommandSuggestion suggestion)
    {
        _vm?.UseCommand(suggestion);
        CommandList.SelectedItem = null;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _vm is { HasCommandSuggestions: true } chat)
        {
            e.Handled = true;
            UseCommand(chat.CommandSuggestions[0]);
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _vm is { } vm)
        {
            e.Handled = true;
            _ = vm.SendAsync();
        }
    }

    private void OnCleared(object? sender, EventArgs e)
    {
        _turns.Clear();
        _dirty.Clear();
        TurnsPanel.Children.Clear();
    }

    private void OnTurnChanged(object? sender, AiChatTurn turn)
    {
        if (!_turns.TryGetValue(turn, out var existing))
        {
            var view = new TurnView(turn, this);
            _turns[turn] = view;
            TurnsPanel.Children.Add(view.Root);
            view.Render();
            ScrollToEnd(force: true);
            return;
        }

        if (!turn.IsStreaming)
        {
            // The end renders at once: the buttons and any error appear without waiting for the timer.
            _dirty.Remove(turn);
            existing.Render();
            ScrollToEnd(force: false);
            return;
        }

        _dirty.Add(turn);
        if (!_render.IsEnabled)
        {
            _render.Start();
        }
    }

    private void RenderDirty()
    {
        _render.Stop();
        foreach (var turn in _dirty)
        {
            if (_turns.TryGetValue(turn, out var view))
            {
                view.Render();
            }
        }

        _dirty.Clear();
        ScrollToEnd(force: false);
    }

    /// <summary>Follows the answer while the view is at (or near) the end, as log views do.</summary>
    private void ScrollToEnd(bool force)
    {
        var atEnd = Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 40;
        if (force || atEnd)
        {
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    /// <summary>One turn: the question, what went with it, and the answer.</summary>
    private sealed class TurnView
    {
        private readonly AiChatTurn _turn;
        private readonly AiChatView _owner;
        private readonly ContentControl _answer = new();
        private readonly StackPanel _steps = new() { Spacing = 2 };
        private readonly WrapPanel _status = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
        private string? _rendered;

        public TurnView(AiChatTurn turn, AiChatView owner)
        {
            _turn = turn;
            _owner = owner;
            var question = new SelectableTextBlock { Text = turn.Question, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
            var questionBox = new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(4), Child = question };
            questionBox.Bind(Border.BackgroundProperty, questionBox.GetResourceObservable("Shell.Hover"));
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(questionBox);
            if (AiChatViewModel.ContextSummary(turn) is { Length: > 0 } summary)
            {
                var context = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                context.Classes.Add("muted");
                panel.Children.Add(context);
            }

            if (turn.ExternalAgent is { } agent)
            {
                var who = new TextBlock { Text = Localizer.Format("msg.acp.answeredBy", "{0} · external agent", agent), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                who.Classes.Add("muted");
                panel.Children.Add(who);
            }

            panel.Children.Add(_thought);
            panel.Children.Add(_plan);
            panel.Children.Add(_steps);
            panel.Children.Add(_answer);
            panel.Children.Add(_notices);
            panel.Children.Add(_status);
            Root = panel;
        }

        private static readonly FontFamily s_monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
        private readonly ContentControl _thought = new();
        private readonly StackPanel _plan = new() { Spacing = 1 };
        private readonly StackPanel _notices = new() { Spacing = 2 };
        private readonly Dictionary<string, bool> _open = new(StringComparer.Ordinal);

        /// <summary>An expander that keeps its state across renders (they rebuild the steps).</summary>
        private Expander Section(string key, string header, Control content, bool openByDefault)
        {
            var expander = new Expander
            {
                Header = header,
                Content = content,
                IsExpanded = _open.TryGetValue(key, out var open) ? open : openByDefault,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 2),
            };
            AutomationProperties.SetName(expander, header);
            expander.Expanded += (_, _) => _open[key] = true;
            expander.Collapsed += (_, _) => _open[key] = false;
            return expander;
        }

        private static SelectableTextBlock Code(string text) =>
            new() { Text = text, FontFamily = s_monospace, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        private static string Glyph(string? status) => status switch
        {
            "completed" => "✓",
            "failed" => "✗",
            "cancelled" => "⊘",
            "in_progress" => "◐",
            _ => "○",
        };

        /// <summary>The thought (collapsed), the plan and the notices of an external agent's turn (M48).</summary>
        private void RenderAgentParts()
        {
            var thought = _turn.Thought;
            _thought.Content = thought.Length == 0 ? null : Section("thought", Localizer.Get("msg.acp.thinking", "Thinking"), Code(thought), openByDefault: false);

            _plan.Children.Clear();
            if (_turn.Plan is { Count: > 0 } plan)
            {
                var title = new TextBlock { Text = Localizer.Get("msg.acp.plan", "Plan"), FontWeight = FontWeight.SemiBold, FontSize = 11 };
                _plan.Children.Add(title);
                foreach (var entry in plan)
                {
                    var line = new TextBlock { Text = (entry.Status == "completed" ? "☑ " : entry.Status == "in_progress" ? "◐ " : "☐ ") + entry.Content, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                    AutomationProperties.SetName(line, entry.Content + " (" + entry.Status.Replace('_', ' ') + ")");
                    if (entry.Status == "completed")
                    {
                        line.Classes.Add("muted");
                    }

                    _plan.Children.Add(line);
                }
            }

            _notices.Children.Clear();
            foreach (var notice in _turn.Notices)
            {
                var line = new TextBlock { Text = "ⓘ " + notice.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                if (notice.Severity is "warning" or "error")
                {
                    line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable(notice.Severity == "error" ? "Shell.Error.Foreground" : "Shell.Warning.Foreground"));
                }
                else
                {
                    line.Classes.Add("muted");
                }

                _notices.Children.Add(line);
            }
        }

        /// <summary>An external agent's tool call: its status, kind and title; its locations; its diffs and output (M48).</summary>
        private StackPanel AgentStep(AiAgentStep step)
        {
            var panel = new StackPanel { Spacing = 2 };
            var header = new TextBlock
            {
                Text = Glyph(step.Status) + " " + step.Kind + ": " + step.Target + (step.Outcome is { } outcome && outcome != "done" ? " → " + outcome : ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            };
            AutomationProperties.SetName(header, Localizer.Format("msg.acp.stepName", "{0} {1}: {2}", step.Kind ?? "", step.Target, (step.Status ?? "pending").Replace('_', ' ')));
            if (step.IsError)
            {
                header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("Shell.Warning.Foreground"));
            }
            else
            {
                header.Classes.Add("muted");
            }

            panel.Children.Add(header);
            if (step.Locations.Count > 0)
            {
                var links = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, Margin = new Thickness(14, 0, 0, 0) };
                foreach (var location in step.Locations.Take(10))
                {
                    var text = location.Path.Replace('\\', '/') + (location.Line is { } line ? ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                    var link = new Button { Content = text, FontSize = 11, Padding = new Thickness(0) };
                    link.Classes.Add("link");
                    ToolTip.SetTip(link, Localizer.Get("msg.acp.openTheFileAtThisLine", "Open the file at this line"));
                    AutomationProperties.SetName(link, Localizer.Format("msg.acp.openLocation", "Open {0}", text));
                    link.Click += (_, _) => _owner._vm?.OpenAgentLocation(location.Path, location.Line);
                    links.Children.Add(link);
                }

                panel.Children.Add(links);
            }

            foreach (var diff in step.Diffs)
            {
                var lines = string.Join('\n', AiDiffText.Lines(diff.OldText, diff.NewText).Select(l => l.Kind + " " + l.Text));
                var title = diff.Reviewed
                    ? Localizer.Format("msg.acp.diffReviewed", "Changes in {0} (reviewed in the preview)", diff.Path)
                    : Localizer.Format("msg.acp.diffByAgent", "Changes in {0} (made by the agent itself, read-only)", diff.Path);
                panel.Children.Add(Section("diff:" + step.Id + ":" + diff.Path, title, Code(lines), openByDefault: false));
            }

            if (step.Output is { Length: > 0 } output)
            {
                var running = step.TerminalId is not null && step.Status is "in_progress" or "pending";
                panel.Children.Add(Section("output:" + step.Id, Localizer.Get("msg.acp.output", "Output"), Code(output), openByDefault: running));
            }

            return panel;
        }

        public Control Root { get; }

        public void Render()
        {
            var steps = _turn.Steps;
            _steps.Children.Clear();
            RenderAgentParts();
            foreach (var step in steps)
            {
                if (step.Id is not null)
                {
                    _steps.Children.Add(AgentStep(step));
                    continue;
                }

                var line = new TextBlock
                {
                    Text = "⚙ " + step.Tool + (step.Target.Length > 0 ? " " + step.Target : "") + (step.Outcome is { } outcome ? " → " + outcome : " …"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                };
                if (step.IsError)
                {
                    line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable("Shell.Warning.Foreground"));
                }
                else
                {
                    line.Classes.Add("muted");
                }

                _steps.Children.Add(line);
            }

            var answer = _turn.Answer;
            if (answer != _rendered || !_turn.IsStreaming)
            {
                _rendered = answer;
                _answer.Content = answer.Length == 0
                    ? null
                    : MarkdownRenderer.Render(answer, target => _owner._vm?.OpenLink(target), _turn.IsStreaming ? null : Footer, new Thickness(0));
            }

            _status.Children.Clear();
            if (_turn.IsStreaming)
            {
                _status.Children.Add(Muted(answer.Length == 0 ? Localizer.Get("msg.ai.waiting", "Waiting for the answer…") : Localizer.Get("msg.ai.writing", "Writing…")));
            }
            else if (_turn.Error is { } error)
            {
                var text = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Shell.Error.Foreground"));
                _status.Children.Add(text);
                if (_turn.NeedsConfiguration)
                {
                    _status.Children.Add(Button(Localizer.Get("msg.ai.configure", "Configure"), Localizer.Get("msg.ai.configureTheProvider", "Configure the provider"), () => _ = _owner._vm?.ConfigureAsync()));
                }
            }
            else if (_turn.FinishReason == "stopped")
            {
                _status.Children.Add(Muted(Localizer.Get("msg.ai.stopped", "Stopped.")));
            }
            else if (_turn.FinishReason == "length")
            {
                _status.Children.Add(Muted(Localizer.Get("msg.ai.cut", "The answer reached its length limit.")));
            }
            else if (_turn.FinishReason == "max_turn_requests")
            {
                _status.Children.Add(Muted(Localizer.Get("msg.acp.turnLimit", "The agent reached its limit of requests for one turn.")));
            }
        }

        /// <summary>
        /// Copy, Insert and Apply under a code block (by its index in the answer). They wrap onto another line in a narrow
        /// pane (M34h: in a row, Apply… was cut off at the tool window's width).
        /// </summary>
        private WrapPanel? Footer(int index)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
            AiCodeBlock? Block() => AiCodeBlocks.Parse(_turn.Answer) is var blocks && index < blocks.Count ? blocks[index] : null;
            row.Children.Add(Button(Localizer.Get("msg.ai.copy", "Copy"), Localizer.Get("msg.ai.copyTheCode", "Copy the code"), () =>
            {
                if (Block() is { } block)
                {
                    _ = _owner._vm?.CopyAsync(block.Code);
                }
            }));
            row.Children.Add(Button(Localizer.Get("msg.ai.insert", "Insert at Cursor"), Localizer.Get("msg.ai.insertTheCodeAtTheCursor", "Insert the code at the cursor"), () =>
            {
                if (Block() is { } block)
                {
                    _ = _owner._vm?.InsertAsync(_turn, block);
                }
            }));
            row.Children.Add(Button(Localizer.Get("msg.ai.apply", "Apply…"), Localizer.Get("msg.ai.applyTheCodeAfterAPreview", "Apply the code after a preview"), () =>
            {
                if (Block() is { } block)
                {
                    _ = _owner._vm?.ApplyAsync(_turn, block);
                }
            }));
            return row;
        }

        private static TextBlock Muted(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.Classes.Add("muted");
            return block;
        }

        private static Button Button(string label, string accessibleName, Action click)
        {
            var button = new Button { Content = label, Padding = new Thickness(8, 2) };
            AutomationProperties.SetName(button, accessibleName);
            button.Click += (_, _) => click();
            return button;
        }
    }
}
