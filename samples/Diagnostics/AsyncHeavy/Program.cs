// AsyncHeavy: async work for the .NET Async tool and the threads, contention and exception views (M18b): fan-out of
// tasks that await timers and each other, one slow chain (SlowChainAsync, about 50 ms a round), a contended lock,
// first-chance exceptions thrown and caught (Worker.Validate throws InvalidOperationException for every eighth id), and
// a burst of blocking work items at the start that starves the thread pool. Runs for the seconds given (default 5;
// 0: until killed).
using System.Globalization;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 5;
var until = seconds <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"AsyncHeavy {Environment.ProcessId} started");

// Sync over async on the pool: more blocked work items than threads, so the pool injects threads (starvation).
var blocking = Enumerable.Range(0, Environment.ProcessorCount * 4).Select(_ => Task.Run(() => Task.Delay(400).Wait())).ToArray();

var gate = new object();
var shared = 0;
var rounds = 0;
var failures = 0;
while (DateTime.UtcNow < until)
{
    var work = Enumerable.Range(0, 32).Select(i => (Task)Worker.FetchAsync(i, gate, () => shared++, () => Interlocked.Increment(ref failures))).ToList();
    work.Add(Worker.SlowChainAsync(3));
    await Task.WhenAll(work);
    rounds++;
}

await Task.WhenAll(blocking);
Console.WriteLine($"AsyncHeavy finished: {rounds} rounds, {shared} increments, {failures} exceptions caught");

internal static class Worker
{
    public static async Task<int> FetchAsync(int id, object gate, Action increment, Action failed)
    {
        await Task.Delay(5 + (id % 7));
        lock (gate)
        {
            increment();
            Thread.SpinWait(10_000); // contention
        }

        try
        {
            Validate(id);
        }
        catch (InvalidOperationException)
        {
            failed();
        }

        return await ParseAsync(id);
    }

    private static void Validate(int id)
    {
        if (id % 8 == 0)
        {
            throw new InvalidOperationException($"Item {id} is not cached."); // [throw]
        }
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
