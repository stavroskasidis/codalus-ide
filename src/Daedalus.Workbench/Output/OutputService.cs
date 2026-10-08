using System.Text;

namespace Daedalus.Workbench.Output;

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

    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>Incremented on every change, so views can cheaply detect staleness.</summary>
    public long Version { get; private set; }

    /// <summary>Text appended (or null when the channel was cleared or trimmed and must be re-read).</summary>
    public event EventHandler<string?>? Appended;

    public void Append(string text)
    {
        bool trimmed;
        lock (_gate)
        {
            _text.Append(text);
            trimmed = _text.Length > MaxLength;
            if (trimmed)
            {
                var cut = _text.Length - (MaxLength * 3 / 4);
                var newline = _text.ToString(cut, Math.Min(4096, _text.Length - cut)).IndexOf('\n', StringComparison.Ordinal);
                _text.Remove(0, cut + Math.Max(0, newline + 1));
            }

            Version++;
        }

        Appended?.Invoke(this, trimmed ? null : text);
    }

    public void AppendLine(string line) => Append(line + "\n");

    public void Clear()
    {
        lock (_gate)
        {
            _text.Clear();
            Version++;
        }

        Appended?.Invoke(this, null);
    }

    public string GetText()
    {
        lock (_gate)
        {
            return _text.ToString();
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
