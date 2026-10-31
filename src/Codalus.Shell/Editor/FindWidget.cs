using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>What Quick Find looks for.</summary>
public sealed record FindQuery(string Pattern, bool MatchCase, bool WholeWords, bool UseRegex);

/// <summary>
/// The matches of a <see cref="FindQuery"/> in a text, off the UI thread: at most a limit of them, whether there are more,
/// and the lines they're on (the overview ruler's marks). Whole words as AvaloniaEdit's search had them.
/// </summary>
internal static class FindSearch
{
    /// <summary>A search's matches (offsets and lengths, in order), whether it stopped at the limit, and their lines.</summary>
    internal sealed record Result(IReadOnlyList<(int Offset, int Length)> Matches, bool Capped, IReadOnlyList<int> Lines);

    /// <summary>The query as a regular expression; throws <see cref="ArgumentException"/> for an invalid one.</summary>
    public static Regex RegexOf(FindQuery query, TimeSpan timeout) =>
        new(query.UseRegex ? query.Pattern : Regex.Escape(query.Pattern), RegexOptions.Multiline | RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), timeout);

    /// <summary>
    /// The matches of <paramref name="regex"/> in <paramref name="text"/> between <paramref name="start"/> and
    /// <paramref name="end"/>, up to <paramref name="max"/>; empty ones are skipped. Null when <paramref name="stop"/>
    /// says the search is no longer wanted.
    /// </summary>
    public static Result? Matches(string text, Regex regex, bool wholeWords, int max, Func<bool>? stop = null, int start = 0, int end = int.MaxValue)
    {
        var matches = new List<(int, int)>();
        var lines = new List<int>();
        var source = wholeWords ? new StringTextSource(text) : null;
        var line = 1;
        var counted = 0;
        for (var match = regex.Match(text, start); match.Success && match.Index + match.Length <= end; match = match.NextMatch())
        {
            if (stop is not null && (matches.Count & 255) == 0 && stop())
            {
                return null;
            }

            if (match.Length == 0 || source is not null && !(IsWordBorder(source, match.Index) && IsWordBorder(source, match.Index + match.Length)))
            {
                continue;
            }

            if (matches.Count == max)
            {
                return new Result(matches, true, lines);
            }

            matches.Add((match.Index, match.Length));
            line += LineBreaks(text, counted, match.Index);
            counted = match.Index;
            if (lines.Count == 0 || lines[^1] != line)
            {
                lines.Add(line);
            }
        }

        return new Result(matches, false, lines);
    }

    private static bool IsWordBorder(ITextSource text, int offset) =>
        TextUtilities.GetNextCaretPosition(text, offset - 1, LogicalDirection.Forward, CaretPositioningMode.WordBorder) == offset;

    /// <summary>The line breaks (\n, \r\n or a lone \r) in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static int LineBreaks(string text, int from, int to)
    {
        var breaks = 0;
        for (var i = from; i < to; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                breaks++;
            }
        }

        return breaks;
    }
}

/// <summary>
/// Quick Find and Quick Replace over one editor (Ctrl+F, Ctrl+H), in its top right corner. It replaces AvaloniaEdit's
/// search panel, which searched the whole document on the UI thread at every key in the find box and at every edit while
/// open, with no limit: typing a letter of a common word into it froze the window for a second in a large file (M56).
/// Here a search runs off the UI thread over a snapshot of the text, a moment after the last key or edit, and is dropped
/// when the text or the query changed meanwhile; it highlights and counts at most <see cref="MaxMatches"/> matches
/// (then "20,000+"), and the highlights follow edits until the next search. Replace All finds every match the same way
/// and applies them as one undo step whose change handlers run once (<see cref="DocumentEdits"/>). Enter and Shift+Enter
/// go to the next and previous match, Escape closes it; F3 and Shift+F3 are the editor's commands.
/// </summary>
public sealed class FindWidget : Border, IBackgroundRenderer
{
    /// <summary>The most matches highlighted and counted.</summary>
    public const int MaxMatches = 20_000;

    private static readonly TimeSpan s_searchTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMilliseconds(250);
    private readonly TextEditor _editor;
    private readonly TextBox _find;
    private readonly TextBox _replace;
    private readonly ToggleButton _matchCase;
    private readonly ToggleButton _wholeWords;
    private readonly ToggleButton _regex;
    private readonly ToggleButton _replaceToggle;
    private readonly Grid _replaceRow;
    private readonly TextBlock _status;
    private readonly UiDebouncer _searchSoon;
    private TextDocument? _document;
    private TextSegmentCollection<TextSegment> _results;

    /// <summary>Changes when a search starts or is cancelled: a search whose number is no longer it stops, and its results are dropped.</summary>
    private int _generation;
    private bool _searching;
    private bool _capped;
    private bool _selectFirst;
    private string? _error;

    public FindWidget(TextEditor editor)
    {
        _editor = editor;
        _results = new TextSegmentCollection<TextSegment>();
        _searchSoon = new UiDebouncer(StartSearch, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
        Classes.Add("overlay");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 0, 18, 0);
        BorderThickness = new Thickness(1, 0, 1, 1);
        CornerRadius = new CornerRadius(0, 0, 4, 4);
        Padding = new Thickness(2, 3, 4, 3);
        IsVisible = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, Localizer.Get("findWidget.find", "Find"));

        _find = new TextBox { Width = 220, VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = Localizer.Get("findWidget.find", "Find") };
        AutomationProperties.SetName(_find, Localizer.Get("findWidget.find", "Find"));
        _replace = new TextBox { Width = 220, VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = Localizer.Get("findWidget.replace", "Replace") };
        AutomationProperties.SetName(_replace, Localizer.Get("findWidget.replace", "Replace"));
        _matchCase = Toggle("Aa", Localizer.Get("findWidget.matchCase", "Match case"));
        _wholeWords = Toggle("ab", Localizer.Get("findWidget.matchWholeWord", "Match whole word"));
        _regex = Toggle(".*", Localizer.Get("findWidget.useRegularExpressions", "Use regular expressions"));
        _replaceToggle = new ToggleButton { Classes = { "toolbar" }, Content = new Icon { Kind = "chevron-right" }, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 2, 0) };
        ToolTip.SetTip(_replaceToggle, Localizer.Get("findWidget.toggleReplace", "Toggle Replace"));
        AutomationProperties.SetName(_replaceToggle, Localizer.Get("findWidget.toggleReplace", "Toggle Replace"));
        _status = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), MinWidth = 70 };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var findRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                _find, _matchCase, _wholeWords, _regex, _status,
                Button("arrow-up", Localizer.Get("findWidget.previousMatch", "Previous Match (Shift+Enter)"), FindPrevious),
                Button("arrow-down", Localizer.Get("findWidget.nextMatch", "Next Match (Enter)"), () => FindNext()),
                Button("close", Localizer.Get("findWidget.close", "Close (Escape)"), Close),
            },
        };
        var replaceNext = Button("replace", Localizer.Get("findWidget.replaceNext", "Replace (Alt+R)"), ReplaceNext);
        var replaceAll = new Button
        {
            Classes = { "toolbar", "iconText" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { new Icon { Kind = "replace" }, new TextBlock { Text = "*", VerticalAlignment = VerticalAlignment.Center } } },
        };
        ToolTip.SetTip(replaceAll, Localizer.Get("findWidget.replaceAll", "Replace All (Alt+A)"));
        AutomationProperties.SetName(replaceAll, Localizer.Get("findWidget.replaceAll", "Replace All (Alt+A)"));
        replaceAll.Click += (_, _) => ReplaceAll();
        _replaceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 3, 0, 0), IsVisible = false };
        _replaceRow.Children.Add(_replace);
        Grid.SetColumn(replaceNext, 1);
        _replaceRow.Children.Add(replaceNext);
        Grid.SetColumn(replaceAll, 2);
        _replaceRow.Children.Add(replaceAll);
        var rows = new StackPanel { Children = { findRow, _replaceRow } };
        var layout = new DockPanel();
        DockPanel.SetDock(_replaceToggle, Avalonia.Controls.Dock.Left);
        layout.Children.Add(_replaceToggle);
        layout.Children.Add(rows);
        Child = layout;

        _find.TextChanged += (_, _) => QueryChanged();
        _matchCase.IsCheckedChanged += (_, _) => QueryChanged();
        _wholeWords.IsCheckedChanged += (_, _) => QueryChanged();
        _regex.IsCheckedChanged += (_, _) => QueryChanged();
        _replaceToggle.IsCheckedChanged += (_, _) => IsReplaceMode = _replaceToggle.IsChecked == true;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.TextArea.DocumentChanged += OnDocumentReplaced;
        Attach(editor.Document);
    }

    /// <summary>Whether the widget shows (and searches).</summary>
    public bool IsOpen { get; private set; }

    public string SearchPattern
    {
        get => _find.Text ?? string.Empty;
        set => _find.Text = value;
    }

    public bool MatchCase
    {
        get => _matchCase.IsChecked == true;
        set => _matchCase.IsChecked = value;
    }

    public bool WholeWords
    {
        get => _wholeWords.IsChecked == true;
        set => _wholeWords.IsChecked = value;
    }

    public bool UseRegex
    {
        get => _regex.IsChecked == true;
        set => _regex.IsChecked = value;
    }

    /// <summary>The replace row shows (never in a read-only editor).</summary>
    public bool IsReplaceMode
    {
        get => _replaceRow.IsVisible;
        set
        {
            var replace = value && !_editor.IsReadOnly;
            _replaceRow.IsVisible = replace;
            _replaceToggle.IsChecked = replace;
            ((Icon)_replaceToggle.Content!).Kind = replace ? "chevron-down" : "chevron-right";
        }
    }

    public string ReplacePattern
    {
        get => _replace.Text ?? string.Empty;
        set => _replace.Text = value;
    }

    /// <summary>The matches highlighted and counted (at most <see cref="MaxMatches"/>).</summary>
    public int MatchCount => _results.Count;

    /// <summary>Whether there are more matches than <see cref="MaxMatches"/>.</summary>
    public bool IsCapped => _capped;

    /// <summary>Changes when a search's results replace the last ones (the overview ruler redraws its marks).</summary>
    public int ResultsVersion { get; private set; }

    /// <summary>The lines of the matches when they were found (the overview ruler's marks).</summary>
    public IReadOnlyList<int> MatchLines { get; private set; } = [];

    /// <summary>A search waits for its moment or runs (tests).</summary>
    internal bool IsSearching => _searchSoon.IsPending || _searching;

    /// <summary>The searches run (tests: one per pause in typing, not one per key or edit).</summary>
    internal int SearchesRun { get; private set; }

    /// <summary>What the status shows: the match count, the current match, or why there are none.</summary>
    internal string Status => _status.Text ?? string.Empty;

    /// <summary>Raised when a search's results arrive.</summary>
    public event EventHandler? ResultsChanged;

    public KnownLayer Layer => KnownLayer.Background;

    private TextArea Area => _editor.TextArea;

    private static ToggleButton Toggle(string text, string tip)
    {
        var toggle = new ToggleButton { Classes = { "toolbar" }, Content = text, FontWeight = FontWeight.SemiBold, Padding = new Thickness(4, 0), Margin = new Thickness(1, 0, 0, 0) };
        ToolTip.SetTip(toggle, tip);
        AutomationProperties.SetName(toggle, tip);
        return toggle;
    }

    private static Button Button(string icon, string tip, Action action)
    {
        var button = new Button { Classes = { "toolbar" }, Content = new Icon { Kind = icon } };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        IsOpen = true;
        IsVisible = true;
        Area.TextView.BackgroundRenderers.Add(this);
        if (SearchPattern.Length > 0)
        {
            _selectFirst = false;
            StartSearch();
        }
    }

    public void Close() => Hide(focusText: true);

    private void Hide(bool focusText)
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        IsVisible = false;
        Area.TextView.BackgroundRenderers.Remove(this);
        CancelSearch();
        ClearResults();
        if (focusText)
        {
            Area.Focus();
        }
    }

    /// <summary>Focuses the find box with its text selected.</summary>
    public void Reactivate()
    {
        void FocusFind()
        {
            _find.Focus();
            _find.SelectAll();
        }

        FocusFind();
        Dispatcher.UIThread.Post(FocusFind, DispatcherPriority.Input); // just shown, it may not take the focus before it's laid out
    }

    /// <summary>Stops following the editor (it's closing).</summary>
    public void Detach()
    {
        Hide(focusText: false);
        _editor.TextArea.DocumentChanged -= OnDocumentReplaced;
        Attach(null);
    }

    // ------------------------------------------------------------------ searching

    private void QueryChanged()
    {
        _selectFirst = true;
        if (!IsOpen)
        {
            return;
        }

        CancelSearch(); // an older query's matches never show
        if (SearchPattern.Length == 0)
        {
            ClearResults();
            return;
        }

        _searchSoon.Request();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (IsOpen && SearchPattern.Length > 0)
        {
            CancelSearch(); // a search of the text before the edit stops; its matches would be dropped
            _searchSoon.Request(); // the highlights follow the edit meanwhile
        }
    }

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        Attach(Area.Document);
        if (IsOpen && SearchPattern.Length > 0)
        {
            _searchSoon.Request();
        }
    }

    private void Attach(TextDocument? document)
    {
        if (_document is not null)
        {
            _document.TextChanged -= OnTextChanged;
        }

        _document = document;
        _results = document is null ? new TextSegmentCollection<TextSegment>() : new TextSegmentCollection<TextSegment>(document);
        if (document is not null)
        {
            document.TextChanged += OnTextChanged;
        }
    }

    private void CancelSearch()
    {
        Interlocked.Increment(ref _generation);
        _searching = false;
    }

    private void StartSearch() => _ = SearchAsync();

    private async Task SearchAsync()
    {
        CancelSearch();
        if (!IsOpen || _document is not { } document || SearchPattern.Length == 0)
        {
            return;
        }

        var generation = Volatile.Read(ref _generation);
        var query = new FindQuery(SearchPattern, MatchCase, WholeWords, UseRegex);
        var version = document.Version;
        var snapshot = document.CreateSnapshot();
        var selectFirst = _selectFirst;
        var from = Math.Max(Area.Caret.Offset - Area.Selection.Length, 0);
        _searching = true;
        SearchesRun++;
        var (result, error) = await Task.Run(() => Search(snapshot, query, MaxMatches, () => Volatile.Read(ref _generation) != generation)).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _generation) || !ReferenceEquals(version, _document?.Version))
        {
            return; // the query or the text changed meanwhile: a newer search follows
        }

        _searching = false;
        _error = error;
        Apply(result);
        if (selectFirst && result is { Matches.Count: > 0 })
        {
            _selectFirst = false;
            FindNext(from);
        }
    }

    /// <summary>A search over a snapshot, off the UI thread: its matches, or why there are none.</summary>
    private static (FindSearch.Result? Result, string? Error) Search(ITextSource snapshot, FindQuery query, int max, Func<bool>? stop)
    {
        try
        {
            return (FindSearch.Matches(snapshot.Text, FindSearch.RegexOf(query, s_searchTimeout), query.WholeWords, max, stop), null);
        }
        catch (ArgumentException)
        {
            return (null, Localizer.Get("findWidget.invalidPattern", "Invalid regular expression"));
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, Localizer.Get("findWidget.tooSlow", "The expression takes too long"));
        }
    }

    /// <summary>
    /// Makes a search's matches the highlighted ones: the ones already there (moved by the edits since) stay, so a search
    /// after an edit changes a few segments, not all of them.
    /// </summary>
    private void Apply(FindSearch.Result? result)
    {
        var matches = result?.Matches ?? [];
        var stale = new List<TextSegment>();
        var index = 0;
        foreach (var segment in _results)
        {
            while (index < matches.Count && matches[index].Offset < segment.StartOffset)
            {
                index++;
            }

            if (index < matches.Count && matches[index] == (segment.StartOffset, segment.Length))
            {
                index++;
            }
            else
            {
                stale.Add(segment);
            }
        }

        foreach (var segment in stale)
        {
            _results.Remove(segment);
        }

        if (_results.Count < matches.Count)
        {
            var kept = new HashSet<int>(_results.Select(s => s.StartOffset));
            foreach (var (offset, length) in matches)
            {
                if (!kept.Contains(offset))
                {
                    _results.Add(new TextSegment { StartOffset = offset, Length = length });
                }
            }
        }

        _capped = result?.Capped == true;
        MatchLines = result?.Lines ?? [];
        ResultsVersion++;
        UpdateStatus();
        Area.TextView.InvalidateLayer(KnownLayer.Selection);
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearResults()
    {
        _results.Clear();
        _capped = false;
        _error = null;
        MatchLines = [];
        ResultsVersion++;
        UpdateStatus();
        Area.TextView.InvalidateLayer(KnownLayer.Selection);
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus()
    {
        var count = _results.Count;
        if (SearchPattern.Length == 0)
        {
            _status.Text = string.Empty;
            return;
        }

        if (_error is { } error)
        {
            _status.Text = error;
            return;
        }

        var current = CurrentIndex();
        _status.Text = (count, current, _capped) switch
        {
            (0, _, _) => Localizer.Get("findWidget.noMatches", "No matches found"),
            (_, >= 0, false) => Localizer.Format("findWidget.matchOf", "{0:N0} of {1:N0}", current + 1, count),
            (_, >= 0, true) => Localizer.Format("findWidget.matchOfCapped", "{0:N0} of {1:N0}+", current + 1, count),
            (1, _, _) => Localizer.Get("findWidget.oneMatch", "1 match"),
            (_, _, false) => Localizer.Format("findWidget.matches", "{0:N0} matches", count),
            _ => Localizer.Format("findWidget.matchesCapped", "{0:N0}+ matches", count),
        };
    }

    /// <summary>The selected match's place among the matches, or -1.</summary>
    private int CurrentIndex()
    {
        var selection = Area.Selection;
        if (selection.IsEmpty || selection.SurroundingSegment is not { } selected)
        {
            return -1;
        }

        var index = 0;
        foreach (var segment in _results)
        {
            if (segment.StartOffset == selected.Offset && segment.Length == selected.Length)
            {
                return index;
            }

            if (segment.StartOffset > selected.Offset)
            {
                return -1;
            }

            index++;
        }

        return -1;
    }

    // ------------------------------------------------------------------ moving between matches

    /// <summary>Selects the next match after the caret (or <paramref name="startOffset"/>), wrapping around.</summary>
    public void FindNext(int startOffset = -1)
    {
        var from = startOffset == -1 ? Area.Caret.Offset : startOffset;
        var next = NextAfter(from) ?? (_capped && LastResult() is { } last && from >= last.EndOffset ? Beyond(from) : null) ?? First();
        if (next is { } match)
        {
            Select(match.Offset, match.Length);
        }
    }

    /// <summary>Selects the match before the selection, wrapping around.</summary>
    public void FindPrevious()
    {
        var before = Math.Max(Area.Caret.Offset - Area.Selection.Length, 0);
        (int Offset, int Length)? previous = null;
        if (_capped && LastResult() is { } last && before > last.EndOffset)
        {
            previous = Before(last.EndOffset, before) ?? (last.StartOffset, last.Length);
        }

        previous ??= PreviousBefore(before) ?? (LastResult() is { } wrap ? (wrap.StartOffset, wrap.Length) : null);
        if (previous is { } match)
        {
            Select(match.Offset, match.Length);
        }
    }

    private (int Offset, int Length)? NextAfter(int offset)
    {
        for (var segment = _results.FindFirstSegmentWithStartAfter(offset); segment is not null; segment = _results.GetNextSegment(segment))
        {
            if (segment.Length > 0)
            {
                return (segment.StartOffset, segment.Length);
            }
        }

        return null;
    }

    private (int Offset, int Length)? First() => NextAfter(0);

    private (int Offset, int Length)? PreviousBefore(int offset)
    {
        var segment = _results.FindFirstSegmentWithStartAfter(offset);
        for (segment = segment is null ? _results.LastSegment : _results.GetPreviousSegment(segment); segment is not null; segment = _results.GetPreviousSegment(segment))
        {
            if (segment.Length > 0 && segment.StartOffset < offset)
            {
                return (segment.StartOffset, segment.Length);
            }
        }

        return null;
    }

    private TextSegment? LastResult()
    {
        for (var segment = _results.LastSegment; segment is not null; segment = _results.GetPreviousSegment(segment))
        {
            if (segment.Length > 0)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>Past the matches counted (there were more): the next one after <paramref name="offset"/>, searched now.</summary>
    private (int Offset, int Length)? Beyond(int offset) => Step(offset, int.MaxValue, last: false);

    /// <summary>Past the matches counted: the last one in [<paramref name="from"/>, <paramref name="to"/>), searched now.</summary>
    private (int Offset, int Length)? Before(int from, int to) => Step(from, to, last: true);

    private (int Offset, int Length)? Step(int from, int to, bool last)
    {
        if (_document is not { } document)
        {
            return null;
        }

        try
        {
            var regex = FindSearch.RegexOf(new FindQuery(SearchPattern, MatchCase, WholeWords, UseRegex), s_stepTimeout);
            var found = FindSearch.Matches(document.Text, regex, WholeWords, last ? int.MaxValue : 1, start: Math.Min(from, document.TextLength), end: to)!.Matches;
            return found.Count == 0 ? null : last ? found[^1] : found[0];
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Selects a match as AvaloniaEdit's search did: the caret at its end, scrolled into view below the widget.</summary>
    private void Select(int offset, int length)
    {
        Area.Caret.Offset = offset + length;
        Area.Selection = Selection.Create(Area, offset, offset + length);
        Area.Caret.BringCaretToView(Bounds.Height + Area.TextView.DefaultLineHeight);
        Area.Caret.Show();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ replacing

    /// <summary>Replaces the selected match (or the next one) and goes to the next.</summary>
    public void ReplaceNext()
    {
        if (!IsReplaceMode || _editor.IsReadOnly)
        {
            return;
        }

        var start = Math.Max(Area.Caret.Offset - Area.Selection.Length, 0);
        if (NextAfter(start) is not { } match || !(Area.Selection.SurroundingSegment is { } selected && selected.Offset == match.Offset && selected.Length == match.Length))
        {
            FindNext(start);
            return;
        }

        Area.Selection.ReplaceSelectionWithText(ReplacePattern);
        FindNext();
    }

    public void ReplaceAll() => _ = ReplaceAllAsync();

    /// <summary>
    /// Replaces every match, not only the ones counted: found off the UI thread over a snapshot (again if the text changed
    /// meanwhile), applied as one undo step.
    /// </summary>
    public async Task ReplaceAllAsync()
    {
        if (!IsReplaceMode || _editor.IsReadOnly || SearchPattern.Length == 0 || _document is not { } document)
        {
            return;
        }

        var query = new FindQuery(SearchPattern, MatchCase, WholeWords, UseRegex);
        var replacement = ReplacePattern;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var version = document.Version;
            var snapshot = document.CreateSnapshot();
            var (result, error) = await Task.Run(() => Search(snapshot, query, int.MaxValue, stop: null)).ConfigureAwait(true);
            if (!ReferenceEquals(version, document.Version) || _document != document)
            {
                continue; // edited meanwhile: the matches moved
            }

            if (result is null)
            {
                _error = error;
                UpdateStatus();
                return;
            }

            if (Area.ReadOnlySectionProvider is { } readOnly && result.Matches.Any(m => !readOnly.CanInsert(m.Offset)))
            {
                return;
            }

            DocumentEdits.Apply(document, [.. result.Matches.Select(m => (m.Offset, m.Length, replacement))]);
            return;
        }
    }

    // ------------------------------------------------------------------ keys and drawing

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var primary = e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta;
        switch (e.Key)
        {
            case Key.F when primary:
                Reactivate(); // Find again in the widget: its text selected, as in the editor
                break;
            case Key.H when primary:
                IsReplaceMode = true;
                break;
            case Key.Enter when _replace.IsKeyboardFocusWithin:
                ReplaceNext();
                break;
            case Key.Enter or Key.F3 when shift:
                FindPrevious();
                break;
            case Key.Enter or Key.F3:
                FindNext();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.R when e.KeyModifiers == KeyModifiers.Alt:
                ReplaceNext();
                break;
            case Key.A when e.KeyModifiers == KeyModifiers.Alt:
                ReplaceAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true; // not through to the text under it
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || _results.Count == 0)
        {
            return;
        }

        // Only the visible matches, as AvaloniaEdit's search drew them.
        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var brush = _editor.SearchResultsBrush;
        foreach (var match in _results.FindOverlappingSegments(start, end - start))
        {
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 0 };
            builder.AddSegment(textView, match);
            if (builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(brush, null, geometry);
            }
        }
    }
}
