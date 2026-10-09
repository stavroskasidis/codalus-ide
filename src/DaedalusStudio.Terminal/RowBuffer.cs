namespace DaedalusStudio.Terminal;

/// <summary>
/// The rows of a screen: a circular buffer with the visible screen at its end and scrollback before it, so scrolling a
/// full buffer costs O(1). Index 0 is the oldest scrollback row.
/// </summary>
internal sealed class RowBuffer
{
    private TerminalRow[] _rows;
    private int _start;

    public RowBuffer(int capacity, int columns, int initial)
    {
        _rows = new TerminalRow[Math.Max(capacity, initial)];
        for (var i = 0; i < initial; i++)
        {
            _rows[i] = new TerminalRow(columns);
        }

        Count = initial;
    }

    public int Count { get; private set; }

    public int Capacity => _rows.Length;

    public TerminalRow this[int index]
    {
        get => _rows[(_start + index) % _rows.Length];
        set => _rows[(_start + index) % _rows.Length] = value;
    }

    /// <summary>Appends a row; when full, the oldest row is dropped (returns true).</summary>
    public bool Push(TerminalRow row)
    {
        if (Count < _rows.Length)
        {
            _rows[(_start + Count) % _rows.Length] = row;
            Count++;
            return false;
        }

        _rows[_start] = row;
        _start = (_start + 1) % _rows.Length;
        return true;
    }

    /// <summary>Removes the newest row.</summary>
    public TerminalRow Pop()
    {
        var row = this[Count - 1];
        Count--;
        return row;
    }

    /// <summary>Drops every row but the last <paramref name="keep"/>.</summary>
    public void TrimTo(int keep)
    {
        if (keep >= Count)
        {
            return;
        }

        var drop = Count - keep;
        _start = (_start + drop) % _rows.Length;
        Count = keep;
    }

    /// <summary>Changes the capacity, keeping the newest rows.</summary>
    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(capacity, 1);
        var keep = Math.Min(Count, capacity);
        var rows = new TerminalRow[capacity];
        for (var i = 0; i < keep; i++)
        {
            rows[i] = this[Count - keep + i];
        }

        _rows = rows;
        _start = 0;
        Count = keep;
    }
}
