using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tint;
using AvaloniaFontStyle = Avalonia.Media.FontStyle;
using TintFontStyle = Tint.FontStyle;

namespace Codalus.Shell.Editor;

/// <summary>
/// Colors an editor's document with a TextMate grammar (ADR 0016), tokenized by Tint off the UI thread. Each line keeps
/// the state the grammar ended it in and its tokens; an edit marks the lines it touched, and one worker on the thread
/// pool tokenizes from the first marked line on, in chunks, until a line ends in the state it ended in before. It reads
/// the text from a snapshot of the document (the UI thread never waits for it nor copies lines for it), and redraws
/// the visible lines it colored. A line waiting to be tokenized again keeps its last tokens meanwhile; one never
/// tokenized is drawn plain. The worker stops while the editor is hidden (<see cref="Pause"/>) and goes on from where
/// it was when it shows again.
/// </summary>
public sealed class TintColorizer : DocumentColorizingTransformer, IDisposable
{
    /// <summary>Only the start of a longer line is tokenized (its tokens past it aren't drawn anyway), as TextMateSharp's model did.</summary>
    private const int MaxLineLength = 10_000;

    /// <summary>A line taking longer is cut there and the next starts in the state it started in, as TextMateSharp's model did.</summary>
    private static readonly TimeSpan LineBudget = TimeSpan.FromSeconds(3);

    /// <summary>How long the worker tokenizes before it hands its lines over (and they're redrawn).</summary>
    private static readonly long ChunkTicks = Stopwatch.Frequency / 40;

    private const int ChunkLines = 2000;

    private readonly TextView _textView;
    private readonly TextMateSupport _support;
    private readonly string _scope;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly List<LineEntry> _lines = [];
    private TextDocument? _document;
    private ITextSourceVersion? _documentVersion;
    private Tokenizer? _tokenizer;
    private Palette? _palette;
    private int _version;
    private int _firstDirty;
    private bool _paused;
    private bool _running;
    private bool _disposed;
    private (int Start, int End)? _pendingRedraw;
    private bool _redrawPosted;

    public TintColorizer(TextView textView, TextMateSupport support, string scope, Theme theme, Action<string>? log)
    {
        _textView = textView;
        _support = support;
        _scope = scope;
        _log = log;
        SetThemeCore(theme);

        // A grammar that can't be compiled (logged by TextMateSupport) colors nothing: the editor goes without one rather
        // than keep a colorizer that never tokenizes and a scope that says it has a grammar.
        if (_tokenizer is null)
        {
            throw new InvalidOperationException($"the grammar {scope} could not be compiled");
        }

        textView.DocumentChanged += OnDocumentChanged;
        Attach(textView.Document);
    }

    /// <summary>The grammar's scope.</summary>
    public string Scope => _scope;

    /// <summary>Whether the worker may run (not paused nor disposed): it runs when there are lines to tokenize.</summary>
    public bool IsTokenizing
    {
        get
        {
            lock (_gate)
            {
                return !_paused && !_disposed && _tokenizer is not null;
            }
        }
    }

    /// <summary>The number of lines the colorizer knows.</summary>
    public int LineCount
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>Whether a zero-based line is tokenized and not waiting to be again.</summary>
    public bool IsLineTokenized(int line)
    {
        lock (_gate)
        {
            return (uint)line < (uint)_lines.Count && _lines[line] is { Dirty: false, Tokens: not null };
        }
    }

    /// <summary>The scopes of a zero-based line's token at a column, or null when the line has no tokens (tests).</summary>
    public IReadOnlyList<string>? ScopesAt(int line, int column)
    {
        Token[]? tokens;
        lock (_gate)
        {
            tokens = (uint)line < (uint)_lines.Count ? _lines[line].Tokens : null;
        }

        if (tokens is null)
        {
            return null;
        }

        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            if (tokens[i].Start <= column)
            {
                return tokens[i].Scopes.GetScopeNames();
            }
        }

        return null;
    }

    /// <summary>The worker's state and the first lines', for a test's failure.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return $"tokenizer {(_paused || _disposed ? "stopped" : _running ? "running" : "idle")}; lines "
                + string.Join(", ", _lines.Take(20).Select((l, i) => $"{i + 1} {(l.Dirty || l.Tokens is null ? "waiting" : "tokenized")} ({l.Tokens?.Length ?? 0} tokens)"));
        }
    }

    /// <summary>Stops the worker (the editor is hidden); edits meanwhile still mark their lines.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    /// <summary>Starts the worker again from the first line waiting.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            KickLocked();
        }
    }

    /// <summary>Colors with another theme: lines keep their colors until they're tokenized again with it.</summary>
    public void SetTheme(Theme theme)
    {
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_palette?.Theme, theme))
            {
                return;
            }
        }

        SetThemeCore(theme);
    }

    private void SetThemeCore(Theme theme)
    {
        var tokenizer = _support.TokenizerForScope(_scope, theme);
        if (tokenizer is not null)
        {
            tokenizer.TimeBudget = LineBudget;
        }

        var palette = new Palette(theme);
        lock (_gate)
        {
            _tokenizer = tokenizer;
            _palette = palette;
            foreach (var line in _lines)
            {
                line.Dirty = true;
            }

            _firstDirty = 0;
            _version++;
            KickLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _version++;
            _tokenizer = null;
            _lines.Clear();
        }

        _textView.DocumentChanged -= OnDocumentChanged;
        if (_document is { } document)
        {
            document.Changed -= OnChanged;
        }

        _document = null;
    }

    // ------------------------------------------------------------------ the document

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => Attach(_textView.Document);

    private void Attach(TextDocument? document)
    {
        if (_document is { } old)
        {
            old.Changed -= OnChanged;
        }

        _document = document;
        if (document is not null)
        {
            document.Changed += OnChanged;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lines.Clear();
            if (document is not null)
            {
                foreach (var line in document.Lines)
                {
                    _lines.Add(new LineEntry { Length = line.Length, TotalLength = line.TotalLength, Dirty = true });
                }
            }

            _documentVersion = document?.Version;
            _firstDirty = 0;
            _version++;
            KickLocked();
        }
    }

    /// <summary>
    /// An edit (UI thread): the lines it replaced become as many as it left, and those are marked. A line that moved keeps
    /// its tokens; a line edited keeps them until it's tokenized again.
    /// </summary>
    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_document is not { } document)
        {
            return;
        }

        var first = document.GetLineByOffset(e.Offset).LineNumber - 1;
        var last = document.GetLineByOffset(e.Offset + e.InsertionLength).LineNumber - 1;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var delta = document.LineCount - _lines.Count;
            if (delta > 0)
            {
                var added = new LineEntry[delta];
                for (var i = 0; i < delta; i++)
                {
                    added[i] = new LineEntry { Dirty = true };
                }

                _lines.InsertRange(Math.Min(first + 1, _lines.Count), added);
            }
            else if (delta < 0)
            {
                var start = Math.Min(first + 1, _lines.Count);
                _lines.RemoveRange(start, Math.Min(-delta, _lines.Count - start));
            }

            if (_lines.Count != document.LineCount)
            {
                // Can't happen; start afresh rather than draw the wrong lines' colors.
                _lines.Clear();
                foreach (var line in document.Lines)
                {
                    _lines.Add(new LineEntry { Length = line.Length, TotalLength = line.TotalLength, Dirty = true });
                }

                first = 0;
                last = _lines.Count - 1;
            }

            // The line before may have lost or gained the end of its line break (a "\n" after its "\r").
            for (var i = Math.Max(0, first - 1); i <= last && i < _lines.Count; i++)
            {
                var line = document.GetLineByNumber(i + 1);
                var entry = _lines[i];
                if (i >= first || entry.Length != line.Length)
                {
                    entry.Dirty = true;
                }

                entry.Length = line.Length;
                entry.TotalLength = line.TotalLength;
            }

            _documentVersion = document.Version;
            _firstDirty = Math.Min(_firstDirty, Math.Max(0, first - 1));
            _version++;
            KickLocked();
        }
    }

    // ------------------------------------------------------------------ the worker

    private void KickLocked()
    {
        if (_running || _paused || _disposed || _tokenizer is null || _document is null || FirstDirtyLocked() < 0)
        {
            return;
        }

        _running = true;
        _ = Task.Run(Work);
    }

    private int FirstDirtyLocked()
    {
        for (var i = _firstDirty; i < _lines.Count; i++)
        {
            if (_lines[i].Dirty)
            {
                _firstDirty = i;
                return i;
            }
        }

        _firstDirty = _lines.Count;
        return -1;
    }

    private async Task Work()
    {
        var buffer = new char[256];
        var entries = new List<(int Length, int TotalLength, StateStack? OldEnd, bool NextDirty)>();
        var results = new List<(Token[] Tokens, StateStack End)>();
        while (true)
        {
            Tokenizer tokenizer;
            Palette palette;
            TextDocument document;
            ITextSourceVersion? documentVersion;
            int version, first, offset;
            StateStack state;
            entries.Clear();
            results.Clear();
            lock (_gate)
            {
                var dirty = _paused || _disposed ? -1 : FirstDirtyLocked();
                if (dirty < 0 || _tokenizer is null || _palette is null || _document is null)
                {
                    _running = false;
                    return;
                }

                tokenizer = _tokenizer;
                palette = _palette;
                document = _document;
                documentVersion = _documentVersion;
                version = _version;
                first = dirty;
                state = first == 0 ? StateStack.Initial : _lines[first - 1].End ?? StateStack.Initial;
                offset = 0;
                for (var i = 0; i < first; i++)
                {
                    offset += _lines[i].TotalLength;
                }

                for (var i = first; i < _lines.Count && entries.Count < ChunkLines; i++)
                {
                    var line = _lines[i];
                    entries.Add((line.Length, line.TotalLength, line.End, i + 1 < _lines.Count && _lines[i + 1].Dirty));
                }
            }

            var snapshot = document.CreateSnapshot();
            if (!ReferenceEquals(snapshot.Version, documentVersion) || snapshot is not RopeTextSource source)
            {
                await Task.Delay(1).ConfigureAwait(false); // an edit not handed over yet
                continue;
            }

            var rope = source.GetRope();
            var started = Stopwatch.GetTimestamp();
            var lineOffset = offset;
            for (var i = 0; i < entries.Count; i++)
            {
                var (length, totalLength, oldEnd, nextDirty) = entries[i];
                var count = Math.Min(length, MaxLineLength);
                if (buffer.Length < count)
                {
                    buffer = new char[Math.Max(count, buffer.Length * 2)];
                }

                Token[] tokens;
                StateStack end;
                try
                {
                    rope.CopyTo(lineOffset, buffer, 0, count);
                    tokens = tokenizer.TokenizeLine(buffer.AsSpan(0, count), state, out end).ToArray();
                    if (tokenizer.StoppedEarly)
                    {
                        end = state;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A grammar that fails on a line leaves it plain and starts the next one afresh.
                    tokens = [];
                    end = StateStack.Initial;
                    _log?.Invoke($"TextMate: {_scope}: {ex.Message}");
                }

                results.Add((tokens, end));
                lineOffset += totalLength;
                state = end;
                if (end.Equals(oldEnd) && !nextDirty)
                {
                    break; // the lines after start as they did: their tokens stand
                }

                if (Stopwatch.GetTimestamp() - started > ChunkTicks)
                {
                    break;
                }
            }

            lock (_gate)
            {
                if (_version != version || _disposed)
                {
                    continue; // edited, re-themed or reset meanwhile: start again from its first line waiting
                }

                for (var i = 0; i < results.Count; i++)
                {
                    var entry = _lines[first + i];
                    entry.Tokens = results[i].Tokens;
                    entry.End = results[i].End;
                    entry.Palette = palette;
                    entry.Dirty = false;
                }

                var next = first + results.Count;
                if (next < _lines.Count && !results[^1].End.Equals(entries[results.Count - 1].OldEnd))
                {
                    _lines[next].Dirty = true;
                }

                _firstDirty = next;
                QueueRedrawLocked(offset, lineOffset);
            }
        }
    }

    private void QueueRedrawLocked(int start, int end)
    {
        _pendingRedraw = _pendingRedraw is var (s, e) ? (Math.Min(s, start), Math.Max(e, end)) : (start, end);
        if (_redrawPosted)
        {
            return;
        }

        _redrawPosted = true;
        Dispatcher.UIThread.Post(RedrawPending);
    }

    private void RedrawPending()
    {
        (int Start, int End)? range;
        lock (_gate)
        {
            range = _pendingRedraw;
            _pendingRedraw = null;
            _redrawPosted = false;
            if (_disposed)
            {
                return;
            }
        }

        if (range is not var (start, end) || _textView.Document is not { } document || !_textView.VisualLinesValid || _textView.VisualLines.Count == 0)
        {
            return;
        }

        // Only what's in view: a range before it would have the view measured again for nothing.
        var visibleStart = _textView.VisualLines[0].FirstDocumentLine.Offset;
        var visibleEnd = _textView.VisualLines[^1].LastDocumentLine.EndOffset;
        start = Math.Max(start, visibleStart);
        end = Math.Min(Math.Min(end, visibleEnd), document.TextLength);
        if (end >= start)
        {
            _textView.Redraw(start, end - start);
        }
    }

    // ------------------------------------------------------------------ drawing

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length > VisualLine.LENGTH_LIMIT)
        {
            return; // long lines aren't colored (as AvaloniaEdit.TextMate left them)
        }

        Token[]? tokens;
        Palette? palette;
        lock (_gate)
        {
            var index = line.LineNumber - 1;
            if ((uint)index >= (uint)_lines.Count)
            {
                return;
            }

            tokens = _lines[index].Tokens;
            palette = _lines[index].Palette;
        }

        if (tokens is null || palette is null)
        {
            return;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var start = token.Start;
            var end = Math.Min(i + 1 < tokens.Length ? tokens[i + 1].Start : token.End, line.Length);
            if (start >= end)
            {
                continue;
            }

            var foreground = token.Foreground != palette.DefaultForeground ? palette.Brush(token.Foreground) : null;
            var background = token.Background != palette.DefaultBackground ? palette.Brush(token.Background) : null;
            var style = token.FontStyle is TintFontStyle.NotSet ? TintFontStyle.None : token.FontStyle;
            var italic = (style & TintFontStyle.Italic) != 0 ? AvaloniaFontStyle.Italic : AvaloniaFontStyle.Normal;
            var weight = (style & TintFontStyle.Bold) != 0 ? FontWeight.Bold : FontWeight.Regular;
            var underline = (style & TintFontStyle.Underline) != 0;
            ChangeLinePart(line.Offset + start, line.Offset + end, element =>
            {
                var properties = element.TextRunProperties;
                if (foreground is not null)
                {
                    properties.SetForegroundBrush(foreground);
                }

                if (background is not null)
                {
                    properties.SetBackgroundBrush(background);
                }

                if (underline)
                {
                    properties.SetTextDecorations(TextDecorations.Underline);
                }

                if (properties.Typeface.Style != italic || properties.Typeface.Weight != weight)
                {
                    properties.SetTypeface(new Typeface(properties.Typeface.FontFamily, italic, weight));
                }
            });
        }
    }

    /// <summary>A line's length, the state the grammar ends it in and its tokens (with the theme they were colored in).</summary>
    private sealed class LineEntry
    {
        public int Length;
        public int TotalLength;
        public StateStack? End;
        public Token[]? Tokens;
        public Palette? Palette;
        public bool Dirty;
    }

    /// <summary>A theme's brushes by color id.</summary>
    private sealed class Palette
    {
        private readonly IBrush?[] _brushes;

        public Palette(Theme theme)
        {
            Theme = theme;
            DefaultForeground = theme.Defaults.ForegroundId;
            DefaultBackground = theme.Defaults.BackgroundId;
            _brushes = new IBrush?[theme.ColorMap.Count];
            for (var id = 1; id < _brushes.Length; id++)
            {
                if (Parse(theme.ColorMap.GetColor(id)) is { } color)
                {
                    _brushes[id] = new ImmutableSolidColorBrush(color);
                }
            }
        }

        public Theme Theme { get; }

        public int DefaultForeground { get; }

        public int DefaultBackground { get; }

        public IBrush? Brush(int id) => (uint)id < (uint)_brushes.Length ? _brushes[id] : null;
    }

    /// <summary>A theme color (<c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>), or null.</summary>
    internal static Color? Parse(string? hex)
    {
        if (hex is null || hex.Length < 4 || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return hex.Length switch
        {
            7 => Color.FromUInt32(0xFF000000 | value),
            9 => Color.FromUInt32((value >> 8) | ((value & 0xFF) << 24)),
            4 => Color.FromRgb((byte)(((value >> 8) & 0xF) * 17), (byte)(((value >> 4) & 0xF) * 17), (byte)((value & 0xF) * 17)),
            _ => null,
        };
    }
}
