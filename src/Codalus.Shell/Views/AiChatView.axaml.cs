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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
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
        private readonly StackPanel _status = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
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

            panel.Children.Add(_steps);
            panel.Children.Add(_answer);
            panel.Children.Add(_status);
            Root = panel;
        }

        public Control Root { get; }

        public void Render()
        {
            var steps = _turn.Steps;
            _steps.Children.Clear();
            foreach (var step in steps)
            {
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
        }

        /// <summary>Copy, Insert and Apply under a code block (by its index in the answer).</summary>
        private StackPanel? Footer(int index)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
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
