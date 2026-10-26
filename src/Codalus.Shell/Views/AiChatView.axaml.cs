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
    private readonly List<DropDownButton> _choicePickers = [];
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
    /// A picker's menu (M48: who answers; M51: the agent's choices): one radio item per choice, the current one checked,
    /// built as it opens so it shows the choices of now, then groups of actions, each after a separator (D43: Connect to
    /// each one-click agent not connected yet; More agents… and Manage Agents…).
    /// </summary>
    private static FilledMenuFlyout Choices(Func<IReadOnlyList<string>> items, Func<int> selected, Action<int> choose, Func<IReadOnlyList<IReadOnlyList<AiAgentMenuAction>>>? actions = null) =>
        new FilledMenuFlyout(flyout =>
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

            // Never an empty menu: it would open as nothing at all.
            if (flyout.Items.Count == 0)
            {
                var none = MenuEntry.Create(Codalus.Workbench.Localization.Localizer.Get("aiChat.nothingToChoose", "Nothing to choose now"), null);
                none.IsEnabled = false;
                flyout.Items.Add(none);
            }
        });


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
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = vm;
        _dirty.Clear();
        BuildChoicePickers();
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.TurnChanged += OnTurnChanged;
        vm.Cleared += OnCleared;
        vm.Turns.CollectionChanged += OnTurnsChanged;
        vm.UpdateContextPreview();
        vm.EnsureAgentStarted();
        _follow.Reset();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiChatViewModel.AgentChoicePickers))
        {
            BuildChoicePickers();
        }
        else if (e.PropertyName is nameof(AiChatViewModel.SelectedAgent) or nameof(AiChatViewModel.IsAgentBlocked))
        {
            // The chat shows an agent it may run: start it, so its choices show before the first question.
            _vm?.EnsureAgentStarted();
        }
    }

    /// <summary>The pickers of the agent's choices (tests).</summary>
    internal IReadOnlyList<DropDownButton> ChoicePickers => _choicePickers;

    /// <summary>
    /// One picker per choice of the external agent (M51), after the agent picker: its current value's name, and a menu of
    /// its values with the current one checked. The agent's updates come often (each one refreshes the chat), so pickers
    /// are updated in place, and made again only when the agent's set of choices changes and none of their menus is open:
    /// a picker removed while its menu was open left the menu open on nothing, and its light dismiss swallowed the next
    /// clicks on every picker (0.8.0's release candidate).
    /// </summary>
    private void BuildChoicePickers()
    {
        var choices = _vm?.AgentChoicePickers ?? [];
        if (!choices.Select(c => c.Id).SequenceEqual(_choicePickers.Select(p => (string)p.Tag!), StringComparer.Ordinal))
        {
            if (_choicePickers.Any(p => p.Flyout is { IsOpen: true }))
            {
                return; // again when the menu closes
            }

            foreach (var old in _choicePickers)
            {
                Toolbar.Children.Remove(old);
            }

            _choicePickers.Clear();
            var at = Toolbar.Children.IndexOf(AgentPicker) + 1;
            foreach (var choice in choices)
            {
                var id = choice.Id;
                var flyout = Choices(
                    () => _vm?.AgentChoicePickers.FirstOrDefault(c => c.Id == id)?.ValueNames ?? [],
                    () => _vm?.AgentChoicePickers.FirstOrDefault(c => c.Id == id)?.SelectedIndex ?? -1,
                    i => _ = _vm?.ChooseAsync(id, i));
                flyout.Closed += (_, _) => BuildChoicePickers();
                var picker = new DropDownButton
                {
                    Tag = id,
                    Content = new TextBlock { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, MaxWidth = 140, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    Flyout = flyout,
                };
                picker.Classes.Add("toolbar");
                Toolbar.Children.Insert(at++, picker);
                _choicePickers.Add(picker);
            }
        }

        // The current values, in place.
        for (var i = 0; i < choices.Count; i++)
        {
            var picker = _choicePickers[i];
            if (picker.Content is TextBlock label && label.Text != choices[i].CurrentName)
            {
                label.Text = choices[i].CurrentName;
            }

            ToolTip.SetTip(picker, choices[i].AccessibleName);
            Avalonia.Automation.AutomationProperties.SetName(picker, choices[i].AccessibleName);
        }
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
