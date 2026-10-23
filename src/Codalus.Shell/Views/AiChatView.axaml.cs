using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// The AI Chat window (M28a; redesigned in M34h, docs/design/controls/AiChat.dc.html). The conversation is a virtualized
/// list of turns (<see cref="AiChatTurnView"/>): only the turns in view are built, and an answer re-renders as it streams
/// (at most every 100 ms). It follows the end while you are there and goes to each new question
/// (<see cref="AiChatFollower"/>). Enter sends and Shift+Enter starts a new line. With an external agent (M48), its
/// slash commands are offered under <c>/</c> and Tab takes the first.
/// </summary>
public sealed partial class AiChatView : UserControl, IPaneView
{
    private static readonly TimeSpan s_renderInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<AiChatTurn, AiChatTurnView> _realized = [];
    private readonly HashSet<AiChatTurn> _dirty = [];
    private readonly DispatcherTimer _render;
    private readonly AiChatFollower _follow;
    private AiChatViewModel? _vm;

    public AiChatView()
    {
        InitializeComponent();
        _render = new DispatcherTimer { Interval = s_renderInterval };
        _render.Tick += (_, _) => RenderDirty();
        TurnsList.ItemTemplate = new FuncDataTemplate<AiChatTurnItem?>((item, _) => item is null ? null : new AiChatTurnView(item, this), supportsRecycling: false);
        _follow = new AiChatFollower(Scroller);
        Provider.Click += (_, _) => _ = _vm?.ChooseProviderAsync();
        AgentPicker.Flyout = Choices(
            () => _vm?.AgentChoices ?? [],
            () => _vm?.SelectedAgentIndex ?? 0,
            i => { if (_vm is { } vm) { vm.SelectedAgentIndex = i; } },
            () => _vm?.AgentMenuActions ?? []);
        MoreAgents.Click += (_, _) => _ = _vm?.MoreAgentsAsync();
        ModePicker.Flyout = Choices(() => _vm?.ModeNames ?? [], () => _vm?.SelectedModeIndex ?? -1, i => { if (_vm is { } vm) { vm.SelectedModeIndex = i; } });
        CommandList.SelectionChanged += (_, _) =>
        {
            if (CommandList.SelectedItem is AiCommandSuggestion suggestion)
            {
                UseCommand(suggestion);
            }
        };
        ChooseProvider.Click += (_, _) => _ = _vm?.ChooseProviderAsync();
        Configure.Click += (_, _) => _ = _vm?.ConfigureAsync();
        NewChat.Click += (_, _) => _vm?.NewChat();
        StopButton.Click += (_, _) => _vm?.Stop();
        StopOffBar.Click += (_, _) => _vm?.Stop();
        Log.Click += (_, _) => _vm?.ShowLog();
        TurnOn.Click += (_, _) => _ = _vm?.TurnOnAsync();
        TurnOnBar.Click += (_, _) => _ = _vm?.TurnOnAsync();
        Send.Click += (_, _) => OnSendClick();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.GotFocus += (_, _) => _vm?.UpdateContextPreview();
        DataContextChanged += (_, _) => Attach(DataContext as AiChatViewModel);
    }

    internal AiChatViewModel? ViewModel => _vm;

    /// <summary>The turns built now, the ones in view (tests).</summary>
    internal IReadOnlyCollection<AiChatTurnView> RealizedTurns => _realized.Values;

    /// <summary>Whether the conversation sticks to its end now (tests).</summary>
    internal bool IsFollowing => _follow.IsFollowing;

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

    /// <summary>
    /// A picker's menu (M48: who answers, and the agent's mode): one radio item per choice, the current one checked,
    /// built as it opens so it shows the choices of now, then groups of actions, each after a separator (D43: Connect to
    /// each one-click agent not connected yet; More agents… and Manage Agents…).
    /// </summary>
    private static MenuFlyout Choices(Func<IReadOnlyList<string>> items, Func<int> selected, Action<int> choose, Func<IReadOnlyList<IReadOnlyList<AiAgentMenuAction>>>? actions = null)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var current = selected();
            var names = items();
            for (var i = 0; i < names.Count; i++)
            {
                var index = i;
                var item = new MenuItem { Header = names[i], ToggleType = MenuItemToggleType.Radio, IsChecked = i == current };
                Avalonia.Automation.AutomationProperties.SetName(item, names[i]);
                item.Click += (_, _) => choose(index);
                flyout.Items.Add(item);
            }

            foreach (var group in actions?.Invoke() ?? [])
            {
                if (group.Count == 0)
                {
                    continue;
                }

                flyout.Items.Add(new Separator());
                foreach (var action in group)
                {
                    flyout.Items.Add(MenuEntry.Create(action.Label, action.Icon, click: () => _ = action.Run()));
                }
            }
        };
        return flyout;
    }

    /// <summary>A one-click agent's button in the off state: Connect to it (D43).</summary>
    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _ = _vm?.ConnectAsync(id);
        }
    }

    /// <summary>An external agent's slash command goes into the input, ready for its argument.</summary>
    private void UseCommand(AiCommandSuggestion suggestion)
    {
        _vm?.UseCommand(suggestion);
        CommandList.SelectedItem = null;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
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
            _vm.Turns.CollectionChanged -= OnTurnsChanged;
        }

        _vm = vm;
        _dirty.Clear();
        if (vm is null)
        {
            return;
        }

        vm.TurnChanged += OnTurnChanged;
        vm.Cleared += OnCleared;
        vm.Turns.CollectionChanged += OnTurnsChanged;
        vm.UpdateContextPreview();
        _follow.Reset();
    }

    /// <summary>
    /// Enter sends (Shift+Enter is a new line). While a turn is answered it sends nothing and keeps the text, as Send is
    /// Stop then; the view model refuses it too.
    /// </summary>
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
            if (!vm.IsAnswering)
            {
                _ = vm.SendAsync();
            }
        }
    }

    /// <summary>
    /// Send hides as Stop takes its place, so the focus goes back to the input box rather than being lost: where Enter
    /// leaves it, where the next question is typed, with Stop the next Tab stop.
    /// </summary>
    private void OnSendClick()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        _ = vm.SendAsync();
        if (vm.IsAnswering && InputBox.IsEffectivelyVisible)
        {
            InputBox.Focus();
        }
    }

    private void OnCleared(object? sender, EventArgs e)
    {
        _dirty.Clear();
        _follow.Reset();
    }

    /// <summary>A new question: the view goes to it, wherever the user had scrolled (the turn's state doesn't matter).</summary>
    private void OnTurnsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            _follow.Reset();
        }
    }

    private void OnTurnChanged(object? sender, AiChatTurn turn)
    {
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
