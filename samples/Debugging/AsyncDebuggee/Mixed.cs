namespace AsyncDebuggee;

/// <summary>
/// The two kinds of async code meeting: this program awaits the library built the other way (AsyncLib or
/// AsyncLib.RuntimeAsync), which awaits a callback of this program's.
/// </summary>
public static class Mixed
{
    public static async Task<int> RoundTripAsync(int value)
    {
        var sum = await AsyncLib.Library.AddLaterAsync(value, 1, TripleAsync); // [mixed-call]
        return sum; // [mixed-return]
    }

    private static async Task<int> TripleAsync(int value)
    {
        await Task.Yield(); // [mixed-yield]
        var tripled = value * 3; // [mixed-callback]
        return tripled;
    }
}
