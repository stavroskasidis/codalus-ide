namespace AsyncLib;

/// <summary>Built as state machines (AsyncLib) and as runtime async (AsyncLib.RuntimeAsync), M61.</summary>
public static class Library
{
    public static async Task<int> AddLaterAsync(int left, int right, Func<int, Task<int>> transform)
    {
        await Task.Delay(10);
        var sum = left + right; // [lib-sum]
        var transformed = await transform(sum); // [lib-transform]
        return transformed + 1; // [lib-return]
    }
}
