using System.Text;

namespace Codalus.Workbench.Output;

/// <summary>Text appended to an <see cref="OutputChannel"/>: <see cref="Text"/> is null when the channel was cleared.</summary>
public sealed class OutputAppendedEventArgs(string? text, bool isError) : EventArgs
{
    /// <summary>The text appended, or null when the channel was cleared.</summary>
    public string? Text { get; } = text;

    /// <summary>Whether the text came from a process's standard error (M34h: the Output window colors it as an error).</summary>
    public bool IsError { get; } = isError;

    /// <summary>
    /// The channel's <see cref="OutputChannel.Version"/> after this change: a view that read a snapshot of this version or
    /// a later one has the text already.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// How many characters the channel dropped from its start with this append, to stay within
    /// <see cref="OutputChannel.MaxLength"/> (0: none). The text appended still follows what a view has: one that keeps
    /// its own text drops its own oldest lines instead of reading the channel again.
    /// </summary>
    public int Trimmed { get; init; }
}

/// <summary>A channel's text and the ranges of it that came from standard error, read together.</summary>
public sealed record OutputSnapshot(string Text, IReadOnlyList<(int Start, int Length)> ErrorRanges)
{
    /// <summary>The channel's <see cref="OutputChannel.Version"/> when it was read.</summary>
    public long Version { get; init; }
}

/// <summary>
/// A named, append-only text channel in the Output window. Appends are thread-safe; the view polls
/// <see cref="Version"/> or listens to <see cref="Appended"/> and batches UI updates.
/// Old text is trimmed beyond <see cref="MaxLength"/> characters.
/// </summary>
public sealed class OutputChannel(string id, string name)
{
    public const int MaxLength = 4 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();

    // The ranges that came from standard error, in positions counted from the channel's first character ever (so a trim
    // only drops the ones before the new start); adjacent ranges merge, so a stream of error lines is one range.
    private readonly List<(long Start, long End)> _errors = [];
    private long _trimmed;

    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>Incremented on every change, so views can cheaply detect staleness.</summary>
    public long Version { get; private set; }

    /// <summary>Text appended (or null when the channel was cleared or trimmed and must be re-read).</summary>
    public event EventHandler<string?>? Appended;

    /// <summary>
    /// As <see cref="Appended"/>, with whether the text came from standard error (M34h) and the version it made; a trim
    /// comes with the text appended (M56: re-reading the whole channel at each trim reloaded 3M characters into the
    /// Output window for each 1M appended).
    /// </summary>
    public event EventHandler<OutputAppendedEventArgs>? TextAppended;

    public void Append(string text) => Append(text, isError: false);

    public void AppendLine(string line) => Append(line + "\n");

    /// <summary>Appends text a process wrote to standard error: the Output window shows it in the error color (M34h).</summary>
    public void AppendError(string text) => Append(text, isError: true);

    /// <summary>Appends a line a process wrote to standard error.</summary>
    public void AppendErrorLine(string line) => AppendError(line + "\n");

    private void Append(string text, bool isError)
    {
        var removed = 0;
        long version;
        lock (_gate)
        {
            if (isError && text.Length > 0)
            {
                var start = _trimmed + _text.Length;
                var end = start + text.Length;
                if (_errors.Count > 0 && _errors[^1].End == start)
                {
                    _errors[^1] = (_errors[^1].Start, end);
                }
                else
                {
                    _errors.Add((start, end));
                }
            }

            _text.Append(text);
            if (_text.Length > MaxLength)
            {
                var cut = _text.Length - (MaxLength * 3 / 4);
                var newline = _text.ToString(cut, Math.Min(4096, _text.Length - cut)).IndexOf('\n', StringComparison.Ordinal);
                removed = cut + Math.Max(0, newline + 1);
                _text.Remove(0, removed);
                _trimmed += removed;
                _errors.RemoveAll(r => r.End <= _trimmed);
            }

            version = ++Version;
        }

        Appended?.Invoke(this, removed > 0 ? null : text);
        TextAppended?.Invoke(this, new OutputAppendedEventArgs(text, isError) { Version = version, Trimmed = removed });
    }

    public void Clear()
    {
        long version;
        lock (_gate)
        {
            _trimmed += _text.Length;
            _text.Clear();
            _errors.Clear();
            version = ++Version;
        }

        Appended?.Invoke(this, null);
        TextAppended?.Invoke(this, new OutputAppendedEventArgs(null, isError: false) { Version = version });
    }

    public string GetText()
    {
        lock (_gate)
        {
            return _text.ToString();
        }
    }

    /// <summary>The text and the ranges of it that came from standard error, as one read.</summary>
    public OutputSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var ranges = new List<(int Start, int Length)>(_errors.Count);
            foreach (var (start, end) in _errors)
            {
                var from = (int)Math.Max(0, start - _trimmed);
                ranges.Add((from, (int)(end - _trimmed) - from));
            }

            return new OutputSnapshot(_text.ToString(), ranges) { Version = Version };
        }
    }
}

public sealed class OutputService
{
    private readonly Lock _gate = new();
    private readonly List<OutputChannel> _channels = [];

    public event EventHandler<OutputChannel>? ChannelAdded;

    /// <summary>Raised when something asks for a channel to be revealed in the Output window.</summary>
    public event EventHandler<OutputChannel>? ShowRequested;

    public IReadOnlyList<OutputChannel> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels];
            }
        }
    }

    /// <summary>Returns the channel with <paramref name="id"/>, creating it on first use.</summary>
    public OutputChannel GetOrCreate(string id, string? name = null)
    {
        OutputChannel channel;
        lock (_gate)
        {
            if (_channels.FirstOrDefault(c => c.Id == id) is { } existing)
            {
                return existing;
            }

            channel = new OutputChannel(id, name ?? id);
            _channels.Add(channel);
        }

        ChannelAdded?.Invoke(this, channel);
        return channel;
    }

    public void Show(OutputChannel channel) => ShowRequested?.Invoke(this, channel);
}
