using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// The completion list's items, filtering and selection: what was typed filters and picks the best match (its matched
/// characters bold), and the filter bar's kinds narrow the items to those kinds. Rows are an icon in the kind's color and
/// the name; the selected row is highlighted with an accent bar.
/// </summary>
public sealed class IntelliSenseList
{
    public const int VisibleRows = 9;

    private readonly List<CompletionData> _items = [];
    private readonly HashSet<string> _filters = new(StringComparer.Ordinal);
    private readonly StackPanel _filterButtons = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly Func<string, string?> _keyLabel;
    private List<CompletionData> _current = [];
    private string? _query;

    public IntelliSenseList(Func<string, string?> keyLabel)
    {
        _keyLabel = keyLabel;
        ListBox = new ListBox
        {
            Classes = { "completion" },
            MaxHeight = VisibleRows * 22,
            Focusable = false,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<CompletionData>((_, _) => new CompletionRow(this), supportsRecycling: true),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(ListBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ListBox.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        ListBox.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            RequestInsertion(e);
        };
        FilterBar = new Border
        {
            Classes = { "completionFilters" },
            Child = _filterButtons,
        };
    }

    /// <summary>Every item, in the server's order.</summary>
    public IList<CompletionData> CompletionData => _items;

    /// <summary>The items shown: those matching the typed text and the filters.</summary>
    public IReadOnlyList<CompletionData> CurrentList => _current;

    public ListBox ListBox { get; }

    /// <summary>The filter bar: a toggle per kind among the items.</summary>
    public Border FilterBar { get; }

    /// <summary>The filter bar's toggles by filter id (tests).</summary>
    public IReadOnlyDictionary<string, ToggleButton> FilterToggles => _toggles;

    private readonly Dictionary<string, ToggleButton> _toggles = new(StringComparer.Ordinal);

    /// <summary>The filters that are on: while any is, only their kinds show.</summary>
    public IReadOnlySet<string> ActiveFilters => _filters;

    /// <summary>The text typed so far.</summary>
    public string Query => _query ?? string.Empty;

    public CompletionData? SelectedItem
    {
        get => ListBox.SelectedItem as CompletionData;
        set
        {
            ListBox.SelectedItem = value;
            if (value is not null)
            {
                ListBox.ScrollIntoView(value);
            }
        }
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Enter, Tab or a double click chose the selected item.</summary>
    public event EventHandler? InsertionRequested;

    /// <summary>The filtering changed (rows redraw their matched characters).</summary>
    internal event EventHandler? Filtered;

    public void RequestInsertion(EventArgs e) => InsertionRequested?.Invoke(this, e);

    /// <summary>Builds the filter bar from the kinds among the items (call once the items are added).</summary>
    public void BuildFilters()
    {
        _filterButtons.Children.Clear();
        _toggles.Clear();
        var present = _items.Select(i => i.Filter).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var filter in SymbolKinds.Filters.Where(f => present.Contains(f.Id)))
        {
            var key = _keyLabel(filter.Command);
            var tip = key is null ? filter.Title : Localizer.Format("editor.completion.filterTip", "{0} ({1})", filter.Title, key);
            var toggle = new ToggleButton
            {
                Classes = { "toolbar", "completionFilter" },
                Focusable = false,
                Content = new Icon { Kind = SymbolKinds.IconOf(filter.Glyph), Width = 16, Height = 16 },
                Tag = filter.Id,
            };
            ToolTip.SetTip(toggle, tip);
            AutomationProperties.SetName(toggle, filter.Title);
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked == true ? _filters.Add(filter.Id) : _filters.Remove(filter.Id))
                {
                    Refresh();
                }
            };
            _toggles[filter.Id] = toggle;
            _filterButtons.Children.Add(toggle);
        }

        FilterBar.IsVisible = _toggles.Count > 0;
    }

    /// <summary>Turns a filter on or off (its toggle, or Alt and its letter); false when no item has that kind.</summary>
    public bool ToggleFilter(string id)
    {
        if (!_toggles.TryGetValue(id, out var toggle))
        {
            return false;
        }

        toggle.IsChecked = toggle.IsChecked != true;
        return true;
    }

    /// <summary>Filters by the typed text and selects the best match.</summary>
    public void SelectItem(string text)
    {
        if (text == _query)
        {
            return;
        }

        _query = text;
        Refresh();
    }

    /// <summary>Filters again (the typed text or the filters changed) and selects the best match.</summary>
    public void Refresh()
    {
        var query = Query;
        var suggested = SelectedItem;
        var shown = new List<CompletionData>();
        CompletionData? best = null;
        var bestQuality = -1;
        double bestPriority = 0;
        foreach (var item in _items)
        {
            if (_filters.Count > 0 && (item.Filter is not { } filter || !_filters.Contains(filter)))
            {
                continue;
            }

            var quality = CompletionMatcher.Match(item.Text, query, out var positions);
            if (quality <= 0)
            {
                continue;
            }

            if (!string.Equals(item.Text, item.Label, StringComparison.Ordinal))
            {
                CompletionMatcher.Match(item.Label, query, out positions);
            }

            item.Matches = positions;
            var priority = item == suggested ? double.PositiveInfinity : item.Priority;
            if (quality > bestQuality || (quality == bestQuality && priority > bestPriority))
            {
                best = item;
                bestQuality = quality;
                bestPriority = priority;
            }

            shown.Add(item);
        }

        _current = shown;
        ListBox.ItemsSource = shown;
        SelectedItem = best;
        Filtered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The list's keys: the arrows, Page Up and Down, Home and End move the selection; Enter and Tab commit.</summary>
    public bool HandleKey(Key key)
    {
        var count = _current.Count;
        if (count == 0)
        {
            return false;
        }

        var index = ListBox.SelectedIndex;
        switch (key)
        {
            case Key.Down:
                Select((index + 1) % count);
                return true;
            case Key.Up:
                Select(index <= 0 ? count - 1 : index - 1);
                return true;
            case Key.PageDown:
                Select(Math.Min(count - 1, Math.Max(0, index) + VisibleRows - 1));
                return true;
            case Key.PageUp:
                Select(Math.Max(0, index - (VisibleRows - 1)));
                return true;
            case Key.Home:
                Select(0);
                return true;
            case Key.End:
                Select(count - 1);
                return true;
            case Key.Enter or Key.Tab when SelectedItem is not null:
                RequestInsertion(EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    private void Select(int index) => SelectedItem = _current[index];

    /// <summary>A row: the kind's icon, the name with its matched characters bold, and the item's detail, muted.</summary>
    private sealed class CompletionRow : DockPanel
    {
        private readonly IntelliSenseList _list;
        private readonly Icon _icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly HighlightedText _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        private readonly TextBlock _detail = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, MaxWidth = 220 };

        public CompletionRow(IntelliSenseList list)
        {
            _list = list;
            var bar = new Border { Classes = { "completionAccent" } };
            SetDock(bar, Avalonia.Controls.Dock.Left);
            SetDock(_icon, Avalonia.Controls.Dock.Left);
            SetDock(_detail, Avalonia.Controls.Dock.Right);
            Children.Add(bar);
            Children.Add(_icon);
            Children.Add(_detail);
            Children.Add(_name);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _list.Filtered += OnFiltered;
            Update();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _list.Filtered -= OnFiltered;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Update();
        }

        private void OnFiltered(object? sender, EventArgs e) => Update();

        private void Update()
        {
            if (DataContext is not CompletionData data)
            {
                return;
            }

            _icon.Kind = data.Icon;
            _name.Source = data.Label;
            _name.Highlights = [.. data.Matches];
            _detail.Text = data.Item.Detail;
            _detail.IsVisible = data.Item.Detail is { Length: > 0 };
            AutomationProperties.SetName(this, data.Label);
        }
    }
}

/// <summary>
/// The completion list's popup (a classic IDE's): the list under the caret's line with the filter bar at its bottom, and
/// the selected item's description in a tooltip to its right (resolved lazily), with its colorized signature, overload
/// count and summary. It follows the typed text (<see cref="StartOffset"/> to <see cref="EndOffset"/>) and closes when
/// the caret leaves it.
/// </summary>
public sealed class CompletionPopup : IntelliSensePopup
{
    private readonly Popup _tip;
    private readonly Border _tipBorder;
    private readonly Func<EditorTheme> _theme;
    private int _tipVersion;

    public CompletionPopup(TextArea textArea, Func<EditorTheme> theme, Func<string, string?> keyLabel) : base(textArea)
    {
        _theme = theme;
        StartOffset = EndOffset = textArea.Caret.Offset;
        CompletionList = new IntelliSenseList(keyLabel);
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(CompletionList.FilterBar, Avalonia.Controls.Dock.Bottom);
        layout.Children.Add(CompletionList.FilterBar);
        layout.Children.Add(CompletionList.ListBox);
        Child = new Border
        {
            Classes = { "intelliSense", "completionPopup" },
            Child = layout,
        };

        _tipBorder = new Border { Classes = { "intelliSense", "completionTip" } };
        _tip = new Popup
        {
            IsLightDismissEnabled = false,
            WindowManagerAddShadowHint = true,
            Focusable = false,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopRight,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.ResizeY,
            HorizontalOffset = 2,
            Child = _tipBorder,
        };
        LogicalChildren.Add(_tip);

        CompletionList.SelectionChanged += (_, _) => UpdateTip();
        CompletionList.InsertionRequested += (_, _) => Commit();
        textArea.Caret.PositionChanged += OnCaretChanged;
        if (textArea.Document is { } document)
        {
            document.Changing += OnDocumentChanging;
        }

        Closed += (_, _) =>
        {
            _tip.IsOpen = false;
            textArea.Caret.PositionChanged -= OnCaretChanged;
            if (textArea.Document is { } document)
            {
                document.Changing -= OnDocumentChanging;
            }
        };
    }

    public IntelliSenseList CompletionList { get; }

    /// <summary>The start of the text being completed (what was typed filters the list).</summary>
    public int StartOffset { get; set; }

    /// <summary>The end of the text being completed.</summary>
    public int EndOffset { get; set; }

    /// <summary>Closes when the caret moves back to <see cref="StartOffset"/> (after Ctrl+Space or a ".", not a "(").</summary>
    public bool CloseWhenCaretAtBeginning { get; set; }

    /// <summary>The description tooltip beside the selected item, while it shows (tests).</summary>
    public Popup DescriptionTip => _tip;

    /// <summary>The tooltip's content (tests).</summary>
    public Control? DescriptionContent => _tipBorder.Child;

    public override double PreferredHeight =>
        (Math.Min(Math.Max(CompletionList.CurrentList.Count, 1), IntelliSenseList.VisibleRows) * RowHeight) + (CompletionList.FilterBar.IsVisible ? 26 : 0) + 4;

    public override double AnchorInset => 26;

    private double RowHeight => this.TryFindResource("Shell.Size.Row", out var value) && value is double row ? row : 22;

    /// <summary>The selected item, committed over the typed text.</summary>
    private void Commit()
    {
        if (CompletionList.SelectedItem is not { } item)
        {
            return;
        }

        // Closed first: committing may open another popup (a snippet's choices, signature help after "(").
        Hide();
        item.Complete(new AnchorSegment(TextArea.Document, StartOffset, Math.Max(0, EndOffset - StartOffset)));
    }

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        // As AvaloniaEdit's completion window: deleting right before the start (Backspace after ".") closes the list.
        if (e.Offset + e.RemovalLength == StartOffset && e.RemovalLength > 0)
        {
            Hide();
        }

        StartOffset = e.GetNewOffset(StartOffset, AnchorMovementType.BeforeInsertion);
        EndOffset = e.GetNewOffset(EndOffset, AnchorMovementType.AfterInsertion);
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            return;
        }

        var offset = TextArea.Caret.Offset;
        if (offset == StartOffset && CloseWhenCaretAtBeginning)
        {
            Hide();
            return;
        }

        if (offset < StartOffset || offset > EndOffset)
        {
            Hide();
            return;
        }

        CompletionList.SelectItem(TextArea.Document.GetText(StartOffset, offset - StartOffset));
        if (CompletionList.CurrentList.Count == 0)
        {
            Hide();
        }
    }

    /// <summary>Shows the selected item's description beside it once resolved; hides it while there's none.</summary>
    private void UpdateTip()
    {
        var version = ++_tipVersion;
        if (CompletionList.SelectedItem is not { } item)
        {
            _tip.IsOpen = false;
            return;
        }

        var resolve = item.Resolve();
        if (resolve.IsCompleted)
        {
            ShowTip(item, version);
            return;
        }

        _ = resolve.ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowTip(item, version)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ShowTip(CompletionData item, int version)
    {
        if (version != _tipVersion || !IsOpen || CompletionList.SelectedItem != item)
        {
            return;
        }

        if (Describe(item.Resolved ?? item.Item) is not { } content)
        {
            _tip.IsOpen = false;
            return;
        }

        _tipBorder.Child = content;

        // Beside the selected row once it's laid out (a row scrolled into view is realized at the next layout).
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _tipVersion || !IsOpen)
            {
                return;
            }

            if (CompletionList.ListBox.ContainerFromItem(item) is not Control row)
            {
                _tip.IsOpen = false;
                return;
            }

            _tip.PlacementTarget = row;
            _tip.PlacementRect = new Rect(0, 0, row.Bounds.Width + 6, row.Bounds.Height);
            _tip.IsOpen = true;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The tooltip's content: the classified description (the C# service), else the documentation as plain text under
    /// the item's detail; null when there's nothing to say.
    /// </summary>
    internal Control? Describe(CompletionItem item)
    {
        if (item.Description is { } description && (description.Signature.Length > 0 || description.Sections.Length > 0))
        {
            return new DescriptionView(description, _theme(), showIcon: false) { MaxWidth = 480 };
        }

        var documentation = item.Documentation?.Value is { Length: > 0 } markdown ? ClassifiedInlines.PlainText(markdown) : null;
        if (documentation is null && item.Detail is not { Length: > 0 })
        {
            return null;
        }

        var panel = new StackPanel { Spacing = 4, MaxWidth = 480 };
        if (item.Detail is { Length: > 0 } detail && documentation?.Contains(detail, StringComparison.Ordinal) != true)
        {
            panel.Children.Add(new TextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        }

        if (documentation is { Length: > 0 })
        {
            panel.Children.Add(new TextBlock { Text = documentation, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        return panel;
    }
}
