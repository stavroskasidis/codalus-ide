// AsyncHeavy: async work for the .NET Async tool (M18b): fan-out of tasks that await timers and each other, one slow
// chain, and a contended lock. Runs for the seconds given (default 5).
using System.Globalization;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 5;
var until = DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"AsyncHeavy {Environment.ProcessId} started");
var gate = new object();
var shared = 0;
var rounds = 0;
while (DateTime.UtcNow < until)
{
    var work = Enumerable.Range(0, 32).Select(i => (Task)Worker.FetchAsync(i, gate, () => shared++)).ToList();
    work.Add(Worker.SlowChainAsync(3));
    await Task.WhenAll(work);
    rounds++;
}

Console.WriteLine($"AsyncHeavy finished: {rounds} rounds, {shared} increments");

internal static class Worker
{
    public static async Task<int> FetchAsync(int id, object gate, Action increment)
    {
        await Task.Delay(5 + (id % 7));
        lock (gate)
        {
            increment();
            Thread.SpinWait(10_000); // contention
        }

        return await ParseAsync(id);
    }

    private static async Task<int> ParseAsync(int id)
    {
        await Task.Yield();
        return id * 2;
    }

    public static async Task SlowChainAsync(int depth)
    {
        if (depth == 0)
        {
            await Task.Delay(50);
            return;
        }

        await SlowChainAsync(depth - 1);
    }
}
