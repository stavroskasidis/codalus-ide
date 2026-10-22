namespace Codalus.Workbench.Debugging;

/// <summary>
/// The values seen at the last stop, by evaluate name (D44, decision 12): a value shown now that differs from the one
/// shown for the same expression when the program last stopped is drawn in red, in the data tip, pinned tips and the
/// variable windows. A value not looked at during a stop keeps the one from the stop it was last seen at.
/// </summary>
public sealed class StopValues
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previous = new(StringComparer.Ordinal);

    /// <summary>The program stopped again: what was seen becomes the last stop's.</summary>
    public void NextStop()
    {
        lock (_gate)
        {
            foreach (var (expression, value) in _current)
            {
                _known[expression] = value;
            }

            _previous = new Dictionary<string, string>(_known, StringComparer.Ordinal);
            _current = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Notes a value shown now; true when it differs from its value at the last stop.</summary>
    public bool Record(string expression, string value)
    {
        lock (_gate)
        {
            _current[expression] = value;
            return _previous.TryGetValue(expression, out var before) && before != value;
        }
    }

    /// <summary>The session ended.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _known.Clear();
            _current.Clear();
            _previous.Clear();
        }
    }
}
