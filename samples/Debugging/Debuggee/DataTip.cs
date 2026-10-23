using System.Collections.ObjectModel;

namespace Debuggee;

/// <summary>
/// The data tip scenarios (D44, run with <c>--datatip</c>): a type of your own deriving from a library's, and the
/// collections the shell's views read from their fields (List, Dictionary with a free slot, HashSet with a free slot,
/// Queue that has wrapped, Stack). Tests find lines by the "// [name]" markers.
/// </summary>
public static class DataTip
{
    public static int Run()
    {
        var inventory = new Inventory { "bolt", "nut" };
        var forecasts = new List<Forecast> { new(1, "Freezing"), new(2, "Bracing"), new(3, "Chilly") };
        var properties = new Dictionary<string, object> { ["server.Features"] = 1, ["removed"] = 0, ["application.Services"] = "services" };
        properties["__MiddlewareDescriptions"] = new List<string> { "HostFiltering" };
        properties.Remove("removed"); // a free slot between used ones
        var ids = new HashSet<int> { 3, 5, 8 };
        ids.Add(13);
        ids.Remove(5); // a free slot between used ones
        var queue = new Queue<string>(4);
        queue.Enqueue("first");
        queue.Enqueue("second");
        queue.Enqueue("third");
        queue.Enqueue("fourth");
        queue.Dequeue();
        queue.Dequeue();
        queue.Enqueue("fifth");
        var stack = new Stack<int>();
        stack.Push(1);
        stack.Push(2);
        stack.Push(3);
        var squares = new[] { 1, 4, 9 };
        var summary = "Balmy";
        Console.WriteLine($"{inventory.Count} {forecasts.Count} {properties.Count} {ids.Count} {queue.Count} {stack.Count} {squares.Length} {summary}"); // [datatip]
        return 0;
    }
}

/// <summary>Your own type over a library's: its private field inline with the lock, Collection's under Non-Public members.</summary>
public sealed class Inventory : Collection<string>
{
    public static string Summaries = "Freezing, Bracing";

    private readonly string logger = "inventory log";

    public int Size => Count;

    public string Session => throw new InvalidOperationException("Session has not been configured for this application or request.");

    public override string ToString() => logger;
}

public sealed record Forecast(int Day, string Summary);
