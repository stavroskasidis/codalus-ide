namespace WinFormsApp;

/// <summary>What the counter control counts, apart from the UI (the tests use it).</summary>
public sealed class Counter
{
    /// <summary>The count.</summary>
    public int Value { get; private set; }

    /// <summary>The highest count.</summary>
    public int Maximum { get; init; } = 10;

    /// <summary>Adds one, up to <see cref="Maximum"/>; whether it did.</summary>
    public bool Increment()
    {
        if (Value >= Maximum)
        {
            return false;
        }

        Value++;
        return true;
    }

    public void Reset() => Value = 0;
}
