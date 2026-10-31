using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Terminal;
using Codalus.Shell.Theming;
using Codalus.Workbench.Output;

namespace Codalus.Shell.Controls;

/// <summary>
/// Shows an output channel in a read-only text editor with its lines colored (M34h): errors in the theme's error text
/// color, warnings in its warning color, successes in its added-text green (<see cref="OutputClassifier"/>), standard
/// error as errors, and ANSI colors from processes as colors instead of raw escapes (<see cref="AnsiDecoder"/>).
/// <para>
/// Appends arrive on any thread: they are decoded there (escapes stripped, runs found) and applied in one batch per
/// dispatcher pass, so a chatty channel can't flood the UI thread. Lines are classified only as the editor draws them,
/// which it does for the lines in view, so a 4 MB channel costs what its visible lines cost.
/// </para>
/// <para>
/// The editor keeps at most <see cref="MaxLength"/> characters: a batch that goes past it drops as many of the oldest
/// lines (M56: each trim of a flooded channel decoded it again and reloaded the editor, 3M characters for each 1M
/// appended). A view that goes away pauses (<see cref="Pause"/>); shown again, it reloads only if its channel changed.
/// </para>
/// </summary>
public sealed class OutputPresenter
{
    /// <summary>The most text the editor keeps, as much as a channel does.</summary>
    internal const int MaxLength = OutputChannel.MaxLength;

    private readonly TextEditor _editor;
    private readonly Lock _decoding = new(); // the decoder and the channel followed: what a producer holds while it decodes
    private readonly Lock _gate = new(); // the queue: all the UI thread's flush takes
    private readonly AnsiDecoder _decoder = new();
    private readonly Queue<Chunk> _pending = new();
    private readonly OutputColorizer _colorizer;
    private OutputChannel? _channel;
    private OutputChannel? _paused;
    private long _pausedVersion;
    private long _loaded; // the version of the snapshot loaded: the appends up to it are in its text
    private long _seen; // the latest version decoded
    private int _flushScheduled;
    private ScrollViewer? _viewer;

    // Styled runs of the document, sorted by offset; the UI thread's (the decoder's runs are moved here on flush).
    private List<OutputRun> _runs = [];

    public OutputPresenter(TextEditor editor)
    {
        _editor = editor;
        _colorizer = new OutputColorizer(this);
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.ActualThemeVariantChanged += (_, _) => ThemeChanged();
        editor.ResourcesChanged += (_, _) => ThemeChanged();

        // Read-only output has nothing to undo: the stack kept each append, and each line dropped, until a reload.
        editor.Document.UndoStack.SizeLimit = 0;
    }

    /// <summary>The channel shown, or null.</summary>
    public OutputChannel? Channel => _channel;

    /// <summary>The styled runs (tests).</summary>
    internal IReadOnlyList<OutputRun> Runs => _runs;

    /// <summary>How many times the editor's whole text was replaced (tests).</summary>
    internal int Loads { get; private set; }

    /// <summary>
    /// Shows <paramref name="channel"/> (null: nothing), from its start; the channel <see cref="Pause"/> left goes on from
    /// what's shown when nothing came to it meanwhile.
    /// </summary>
    public void Show(OutputChannel? channel)
    {
        if (channel is not null && ReferenceEquals(channel, _channel))
        {
            return;
        }

        Decoded? loaded = null;
        lock (_decoding)
        {
            if (_channel is not null)
            {
                _channel.TextAppended -= OnAppended;
            }

            var resume = channel is not null && ReferenceEquals(channel, _paused) && channel.Version == _pausedVersion;
            _paused = null;
            _channel = channel;

            // Followed before the snapshot is read, and its appends wait for this lock: those in it are dropped, the rest applied.
            if (channel is not null)
            {
                channel.TextAppended += OnAppended;
            }

            if (resume)
            {
                _loaded = _pausedVersion; // what's shown (and still queued) is the channel's
            }
            else
            {
                lock (_gate)
                {
                    _pending.Clear();
                }

                var snapshot = channel?.GetSnapshot();
                _loaded = _seen = snapshot?.Version ?? 0;
                loaded = Decode(snapshot);
            }
        }

        if (loaded is not null)
        {
            Load(loaded);
        }
    }

    /// <summary>
    /// Stops following the channel while the view isn't shown, its text kept: <see cref="Show"/> of the same channel goes
    /// on from it when nothing came meanwhile (showing the Output window again read up to 4M characters each time, M56).
    /// </summary>
    public void Pause()
    {
        lock (_decoding)
        {
            if (_channel is null)
            {
                return;
            }

            _channel.TextAppended -= OnAppended;
            _paused = _channel;
            _pausedVersion = _seen;
            _channel = null;
        }
    }

    /// <summary>The colors of a line kind or an ANSI run, as the editor draws them (tests).</summary>
    internal IBrush? BrushFor(OutputLineKind kind) => _colorizer.BrushFor(kind);

    /// <summary>The color an ANSI run's style draws in, after the contrast floor (tests).</summary>
    internal IBrush? BrushFor(OutputStyle style) => _colorizer.BrushFor(style);

    private void OnAppended(object? sender, OutputAppendedEventArgs e)
    {
        lock (_decoding)
        {
            if (!ReferenceEquals(sender, _channel) || e.Version <= _loaded)
            {
                return; // another channel's, one paused, or in the snapshot loaded
            }

            _seen = Math.Max(_seen, e.Version);
            Chunk chunk;
            if (e.Text is null)
            {
                // Cleared: what's queued before it is moot.
                _decoder.Reset();
                chunk = new Chunk(new Decoded(string.Empty, []), Reload: true);
            }
            else
            {
                // The channel's own trims change nothing here: the editor drops its oldest lines itself (Trim).
                var text = new StringBuilder(e.Text.Length);
                var runs = new List<OutputRun>();
                _decoder.Rebase();
                _decoder.Decode(e.Text, e.IsError, text, runs);
                if (text.Length == 0)
                {
                    return;
                }

                chunk = new Chunk(new Decoded(text.ToString(), runs), Reload: false);
            }

            lock (_gate)
            {
                if (chunk.Reload)
                {
                    _pending.Clear();
                }

                _pending.Enqueue(chunk);
            }
        }

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    /// <summary>Decodes a snapshot of the channel from its start (the caller holds the decoding lock).</summary>
    private Decoded Decode(OutputSnapshot? snapshot)
    {
        _decoder.Reset();
        if (snapshot is null)
        {
            return new Decoded(string.Empty, []);
        }

        var text = new StringBuilder(snapshot.Text.Length);
        var runs = new List<OutputRun>();
        var source = snapshot.Text.AsSpan();
        var at = 0;
        foreach (var (start, length) in snapshot.ErrorRanges)
        {
            var from = Math.Clamp(start, at, source.Length);
            var to = Math.Clamp(start + length, from, source.Length);
            _decoder.Decode(source[at..from], isError: false, text, runs);
            _decoder.Decode(source[from..to], isError: true, text, runs);
            at = to;
        }

        _decoder.Decode(source[at..], isError: false, text, runs);
        return new Decoded(text.ToString(), runs);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        var reload = false;
        var appended = new StringBuilder();
        var runs = new List<OutputRun>();
        lock (_gate)
        {
            while (_pending.TryDequeue(out var chunk))
            {
                if (chunk.Reload)
                {
                    reload = true;
                    appended.Clear();
                    runs.Clear();
                }

                // A chunk's runs count from its own start: from where it goes in the batch.
                foreach (var run in chunk.Text.Runs)
                {
                    runs.Add(run with { Start = run.Start + appended.Length });
                }

                appended.Append(chunk.Text.Text);
            }
        }

        if (appended.Length > MaxLength)
        {
            // More than the editor keeps came at once: only its end shows, what was shown before it gone anyway.
            var cut = LineAfter(appended, appended.Length - (MaxLength * 3 / 4));
            Load(new Decoded(appended.ToString(cut, appended.Length - cut), Rebased(runs, 0, cut)));
            return;
        }

        if (reload)
        {
            Load(new Decoded(string.Empty, []));
        }

        if (appended.Length == 0)
        {
            return;
        }

        var document = _editor.Document;
        var at = document.TextLength;
        foreach (var run in runs)
        {
            _runs.Add(run with { Start = run.Start + at });
        }

        double removedHeight;
        document.BeginUpdate();
        try
        {
            document.Insert(at, appended.ToString());
            removedHeight = Trim();
        }
        finally
        {
            document.EndUpdate();
        }

        // A reader scrolled up keeps the lines in view: they'd move up by what was dropped.
        if (removedHeight > 0 && _viewer is { } viewer)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, viewer.Offset.Y - removedHeight));
        }
    }

    /// <summary>
    /// Drops the oldest lines past <see cref="MaxLength"/>, about as many as each batch adds, so the editor never reloads
    /// (its line tree loses the lines dropped). Returns their height when the view isn't at its end (0 when it is: it
    /// follows the end, and moving it would stop that).
    /// </summary>
    private double Trim()
    {
        var document = _editor.Document;
        var excess = document.TextLength - MaxLength;
        if (excess <= 0)
        {
            return 0;
        }

        var line = document.GetLineByOffset(excess);
        var cut = line.Offset == excess || line.NextLine is not { } next || next.Offset - excess > 4096 ? excess : next.Offset;
        if (cut < document.TextLength && char.IsLowSurrogate(document.GetCharAt(cut)))
        {
            cut++;
        }

        _viewer ??= _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var height = _viewer is { } viewer && viewer.Offset.Y + viewer.Viewport.Height < viewer.Extent.Height - 2
            ? _editor.TextArea.TextView.GetVisualTopByDocumentLine(document.GetLineByOffset(cut).LineNumber)
            : 0;
        document.Remove(0, cut);
        _runs = Rebased(_runs, FirstRunAfter(cut), cut);
        return height;
    }

    /// <summary>The runs from <paramref name="first"/> on, counted from <paramref name="cut"/> (the text before it gone).</summary>
    private static List<OutputRun> Rebased(List<OutputRun> runs, int first, int cut)
    {
        var rebased = new List<OutputRun>(Math.Max(0, runs.Count - first));
        for (var i = first; i < runs.Count; i++)
        {
            if (runs[i].End > cut)
            {
                var start = Math.Max(runs[i].Start, cut);
                rebased.Add(new OutputRun(start - cut, runs[i].End - start, runs[i].Style));
            }
        }

        return rebased;
    }

    /// <summary>Where the line after <paramref name="offset"/> starts, if it's near (else <paramref name="offset"/>).</summary>
    private static int LineAfter(StringBuilder text, int offset)
    {
        var newline = text.ToString(offset, Math.Min(4096, text.Length - offset)).IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? offset : offset + newline + 1;
    }

    private void Load(Decoded decoded)
    {
        Loads++;
        _runs = decoded.Runs;
        _editor.Text = decoded.Text;
    }


    private void ThemeChanged()
    {
        _colorizer.Invalidate();
        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>The first run that ends after <paramref name="offset"/>.</summary>
    private int FirstRunAfter(int offset)
    {
        int low = 0, high = _runs.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (_runs[mid].End <= offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private sealed record Decoded(string Text, List<OutputRun> Runs);

    private readonly record struct Chunk(Decoded Text, bool Reload);

    /// <summary>Colors each line the editor builds: its kind, then standard error and ANSI runs over it.</summary>
    private sealed class OutputColorizer(OutputPresenter owner) : DocumentColorizingTransformer
    {
        private readonly Dictionary<OutputStyle, IBrush?> _ansi = [];
        private IBrush? _error;
        private IBrush? _warning;
        private IBrush? _success;
        private TerminalTheme? _palette;
        private Color _background;
        private double _minimumContrast;
        private bool _resolved;

        public void Invalidate()
        {
            _resolved = false;
            _ansi.Clear();
        }

        public IBrush? BrushFor(OutputLineKind kind)
        {
            Resolve();
            return kind switch
            {
                OutputLineKind.Error => _error,
                OutputLineKind.Warning => _warning,
                OutputLineKind.Success => _success,
                _ => null,
            };
        }

        public IBrush? BrushFor(OutputStyle style)
        {
            Resolve();
            if (style.Foreground < 0)
            {
                return (style.Attributes & OutputAttributes.Error) != 0 ? _error : null;
            }

            if (!_ansi.TryGetValue(style, out var brush))
            {
                var bright = !style.IsRgb && ((style.Attributes & OutputAttributes.Bold) != 0 || style.Foreground is >= 8 and < 16);
                var color = style.IsRgb
                    ? Color.FromRgb((byte)(style.Foreground >> 16), (byte)(style.Foreground >> 8), (byte)style.Foreground)
                    : _palette!.PaletteColor((style.Attributes & OutputAttributes.Bold) != 0 && style.Foreground < 8 ? style.Foreground + 8 : style.Foreground);

                // Bold and bright colors stand out more than the plain ones (M51): where both fell short of the theme's
                // contrast (green and bright green on Harbor Light), the plain one and the bright one were raised to the
                // same edge and looked alike.
                brush = new ImmutableSolidColorBrush(Readable(color, _background, bright ? _minimumContrast * BrightContrast : _minimumContrast));
                _ansi[style] = brush;
            }

            return brush;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0)
            {
                return;
            }

            var document = CurrentContext.Document;
            var start = line.Offset;
            var end = line.EndOffset;
            var runs = owner._runs;
            var first = owner.FirstRunAfter(start);

            // Standard error is the base: a line that says it's a warning or a success keeps that color over it.
            for (var i = first; i < runs.Count && runs[i].Start < end; i++)
            {
                if ((runs[i].Style.Attributes & OutputAttributes.Error) != 0 && BrushFor(OutputLineKind.Error) is { } error)
                {
                    Paint(Math.Max(start, runs[i].Start), Math.Min(end, runs[i].End), error, OutputAttributes.None);
                }
            }

            var kind = OutputClassifier.Classify(document.GetText(start, Math.Min(line.Length, OutputClassifier.MaxClassifiedLength)));
            if (BrushFor(kind) is { } brush)
            {
                Paint(start, end, brush, OutputAttributes.None);
            }

            // The process's own colors and attributes win: they're the most specific. Bold is the bright color at the
            // regular weight (M50): a bold typeface in the Output window's font was hard to read.
            for (var i = first; i < runs.Count && runs[i].Start < end; i++)
            {
                var style = runs[i].Style;
                var foreground = style.Foreground >= 0 ? BrushFor(style) : null;
                var attributes = style.Attributes & (OutputAttributes.Italic | OutputAttributes.Underline);
                if (foreground is not null || attributes != OutputAttributes.None)
                {
                    Paint(Math.Max(start, runs[i].Start), Math.Min(end, runs[i].End), foreground, attributes);
                }
            }
        }

        private void Paint(int start, int end, IBrush? foreground, OutputAttributes attributes)
        {
            if (end <= start)
            {
                return;
            }

            ChangeLinePart(start, end, element =>
            {
                if (foreground is not null)
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                }

                if (attributes != OutputAttributes.None)
                {
                    if ((attributes & OutputAttributes.Italic) != 0)
                    {
                        var typeface = element.TextRunProperties.Typeface;
                        element.TextRunProperties.SetTypeface(new Typeface(typeface.FontFamily, FontStyle.Italic, typeface.Weight));
                    }

                    if ((attributes & OutputAttributes.Underline) != 0)
                    {
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    }
                }
            });
        }

        private void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            var editor = owner._editor;
            var variant = editor.ActualThemeVariant;
            IBrush? Token(string key) => editor.TryFindResource(key, variant, out var value) ? value as IBrush : null;
            _error = Token("Shell.Error.Foreground");
            _warning = Token("Shell.Warning.Foreground");
            _success = Token("Shell.Diff.Added.Foreground");
            _background = (editor.Background ?? Token("Shell.Editor.Background")) is ISolidColorBrush background ? background.Color : Colors.Black;
            var highContrast = ThemeService.IsHighContrast(variant);
            _minimumContrast = highContrast ? 7.0 : 4.5;
            _palette = TerminalTheme.For(highContrast ? "highContrast" : IsLight(variant) ? "light" : "dark");
            _resolved = true;
        }

        private static bool IsLight(ThemeVariant? variant)
        {
            for (var v = variant; v is not null; v = v.InheritVariant)
            {
                if (v == ThemeVariant.Light)
                {
                    return true;
                }

                if (v == ThemeVariant.Dark)
                {
                    return false;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// <paramref name="color"/>, moved toward black on a light background or white on a dark one until it meets
    /// <paramref name="ratio"/> against <paramref name="background"/> (VS Code's minimum contrast for terminal colors):
    /// a process's yellow stays yellow, readable in every theme.
    /// </summary>
    /// <summary>How much more contrast bold and bright ANSI colors need than plain ones.</summary>
    internal const double BrightContrast = 1.4;

    internal static Color Readable(Color color, Color background, double ratio)
    {
        if (Contrast(color, background) >= ratio)
        {
            return color;
        }

        var target = Luminance(background) > 0.18 ? Colors.Black : Colors.White;
        for (var step = 1; step <= 20; step++)
        {
            var t = step / 20.0;
            var mixed = Color.FromRgb(Mix(color.R, target.R, t), Mix(color.G, target.G, t), Mix(color.B, target.B, t));
            if (Contrast(mixed, background) >= ratio)
            {
                return mixed;
            }
        }

        return target;

        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t));
    }

    internal static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
