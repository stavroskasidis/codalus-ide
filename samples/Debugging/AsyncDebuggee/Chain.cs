namespace AsyncDebuggee;

/// <summary>A chain of awaits, for the call stack: Outer awaits Middle, which awaits Inner, each resuming later.</summary>
public static class Chain
{
    public static async Task<int> OuterAsync(int seed)
    {
        var middle = await MiddleAsync(seed + 1); // [chain-outer]
        return middle;
    }

    private static async Task<int> MiddleAsync(int seed)
    {
        await Task.Yield();
        var inner = await InnerAsync(seed * 2); // [chain-middle]
        return inner + 1;
    }

    private static async Task<int> InnerAsync(int seed)
    {
        await Task.Delay(10);
        var deepest = seed + 100; // [chain-inner]
        return deepest; // [chain-inner-return]
    }
}
