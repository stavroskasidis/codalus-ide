#nullable enable
// Leaky: a leak for memory snapshots and their diffs (M18b): a static cache that only grows, with event handlers that
// keep their subscribers alive. Runs for the seconds given (default 30; 0: until killed), printing the heap size every second.
using System.Globalization;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 30;
var until = seconds <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"Leaky {Environment.ProcessId} started");
var next = DateTime.UtcNow;
var id = 0;
while (DateTime.UtcNow < until)
{
    for (var i = 0; i < 1_000; i++)
    {
        var order = new Order(id++, new string('x', 64));
        Cache.Orders[order.Id] = order;
        Cache.Changed += order.OnChanged; // never unsubscribed
    }

    if (DateTime.UtcNow >= next)
    {
        Console.WriteLine($"{Cache.Orders.Count} orders, {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        next = DateTime.UtcNow.AddSeconds(1);
    }

    Thread.Sleep(50);
}

internal sealed record Order(int Id, string Notes)
{
    public void OnChanged(object? sender, EventArgs e)
    {
    }
}

internal static class Cache
{
    public static readonly Dictionary<int, Order> Orders = [];

    public static event EventHandler? Changed;

    public static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
}
