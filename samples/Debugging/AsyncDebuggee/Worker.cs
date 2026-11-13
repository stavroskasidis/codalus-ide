namespace AsyncDebuggee;

/// <summary>
/// Locals before and after awaits: a timer (Task.Delay) and Task.Yield resume on a thread-pool thread. Task.Yield
/// always suspends the method; a short delay may have run out by the time a step reaches its await.
/// </summary>
public sealed class Worker(string name)
{
    public string Name { get; } = name;

    public async Task<int> RunAsync(int count)
    {
        var before = count * 10; // [run-before]
        await Task.Delay(20); // [run-delay]
        var afterDelay = before + 1; // [run-after-delay]
        await Task.Yield(); // [run-yield]
        var afterYield = afterDelay + 1; // [run-after-yield]
        var doubled = await DoubleAsync(afterYield); // [run-double]
        var text = $"{Name}:{doubled}"; // [run-text]
        Console.WriteLine(text); // [run-print]
        return doubled; // [run-return]
    } // [run-close]

    private async Task<int> DoubleAsync(int value)
    { // [double-open]
        await Task.Yield(); // [double-yield]
        var result = value * 2; // [double-result]
        return result; // [double-return]
    }
}
