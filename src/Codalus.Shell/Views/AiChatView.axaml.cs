using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;

namespace Codalus.Shell.Views;

/// <summary>
/// The AI Chat window (M28a; redesigned in M34h, docs/design/controls/AiChat.dc.html). The conversation is a virtualized
/// list of turns (<see cref="AiChatTurnView"/>): only the turns in view are built, and an answer re-renders as it streams
/// (at most every 100 ms). It follows the end while you are there, as log views do. Enter sends and Shift+Enter starts a
/// new line.
/// </summary>
public sealed partial class AiChatView : UserControl, IPaneView
{
    private static readonly TimeSpan s_renderInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<AiChatTurn, AiChatTurnView> _realized = [];
    private readonly HashSet<AiChatTurn> _dirty = [];
    private readonly DispatcherTimer _render;
    private readonly FollowTail _follow;
    private AiChatViewModel? _vm;

    public AiChatView()
    {
        InitializeComponent();
        _render = new DispatcherTimer { Interval = s_renderInterval };
        _render.Tick += (_, _) => RenderDirty();
        TurnsList.ItemTemplate = new FuncDataTemplate<AiChatTurnItem?>((item, _) => item is null ? null : new AiChatTurnView(item, this), supportsRecycling: false);
        _follow = FollowTail.Attach(ConversationHost);
        Provider.Click += (_, _) => _ = _vm?.ChooseProviderAsync();
        ChooseProvider.Click += (_, _) => _ = _vm?.ChooseProviderAsync();
        Configure.Click += (_, _) => _ = _vm?.ConfigureAsync();
        NewChat.Click += (_, _) => _vm?.NewChat();
        StopButton.Click += (_, _) => _vm?.Stop();
        Log.Click += (_, _) => _vm?.ShowLog();
        TurnOn.Click += (_, _) => _ = _vm?.TurnOnAsync();
        TurnOnBar.Click += (_, _) => _ = _vm?.TurnOnAsync();
        Send.Click += (_, _) => _ = _vm?.SendAsync();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.GotFocus += (_, _) => _vm?.UpdateContextPreview();
        DataContextChanged += (_, _) => Attach(DataContext as AiChatViewModel);
    }

    internal AiChatViewModel? ViewModel => _vm;

    /// <summary>The turns built now, the ones in view (tests).</summary>
    internal IReadOnlyCollection<AiChatTurnView> RealizedTurns => _realized.Values;

    public void FocusPrimary()
    {
        if (InputBox.IsEffectivelyVisible)
        {
            InputBox.Focus();
        }
        else if (TurnOnBar.IsEffectivelyVisible)
        {
            TurnOnBar.Focus();
        }
        else
        {
            TurnOn.Focus();
        }
    }

    /// <summary>A turn came into view (its view renders itself).</summary>
    internal void Realized(AiChatTurnView view) => _realized[view.Turn] = view;

    internal void Unrealized(AiChatTurnView view)
    {
        if (_realized.TryGetValue(view.Turn, out var current) && current == view)
        {
            _realized.Remove(view.Turn);
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
        _dirty.Clear();
        if (vm is null)
        {
            return;
        }

        vm.TurnChanged += OnTurnChanged;
        vm.Cleared += OnCleared;
        vm.UpdateContextPreview();
        _follow.Reset();
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
        _dirty.Clear();
        _follow.Reset();
    }

    private void OnTurnChanged(object? sender, AiChatTurn turn)
    {
        if (_vm is { Turns: { Count: > 0 } turns } && turns[^1].Turn == turn && turn.Steps.Count == 0 && turn.Answer.Length == 0 && turn.IsStreaming)
        {
            // A new question: show its end.
            _follow.Reset();
        }

        if (!_realized.TryGetValue(turn, out var view))
        {
            return; // out of view: it renders when it comes back
        }

        if (!turn.IsStreaming)
        {
            // The end renders at once: the buttons and any error appear without waiting for the timer.
            _dirty.Remove(turn);
            view.Render();
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
            if (_realized.TryGetValue(turn, out var view))
            {
                view.Render();
            }
        }

        _dirty.Clear();
    }
}
