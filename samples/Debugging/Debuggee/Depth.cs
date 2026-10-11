using System.Diagnostics;

namespace Debuggee;

/// <summary>The M14 debugger-depth scenarios (run with <c>--depth</c>). Tests find lines by the "// [name]" markers.</summary>
public static class Depth
{
    public static int Run()
    {
        var json = "{\"name\":\"Ada\",\"tags\":[\"math\",\"engines\"],\"age\":36}";
        var xml = "<person name=\"Ada\"><tag>math</tag></person>";
        var settings = new Settings { Retries = 3 };
        var order = new Order { Id = 7, Customer = "Ada" };
        order.Lines.Add("engine");
        // A 1x1 PNG (the image visualizer) and a list (the grid visualizer), M14b.
        var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var squares = Enumerable.Range(1, 12).Select(n => n * n).ToList();
        var orders = new[] { order, new Order { Id = 8, Customer = "Grace" } };
        // A dictionary with a removed entry (the sample plugin visualizer).
        var ages = new Dictionary<string, int> { ["Ada"] = 36, ["Charles"] = 79, ["Grace"] = 85 };
        ages.Remove("Charles");
        var sum = 0; // [depth-start]
        for (var i = 1; i <= 5; i++)
        {
            sum += i; // [depth-loop]
            sum += Square(i); // [depth-square-call]
        }

        try
        {
            Wrap();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("wrapped " + ex.InnerException?.Message);
        }

        try
        {
            _ = int.Parse("not a number", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            Console.WriteLine("format");
        }

        var worker = new Thread(() => Thread.Sleep(200)) { Name = "Depth worker" };
        worker.Start();
        Console.WriteLine($"{json.Length} {xml.Length} {settings.Retries} {sum} {order.Id} {pixel.Length} {squares.Count} {orders.Length} {ages.Count}"); // [depth-end]
        worker.Join();

        // Two named threads through one line (breakpoint filters, M14b).
        foreach (var name in new[] { "Filter A", "Filter B" })
        {
            var tagger = new Thread(() => Console.WriteLine(Tag(name))) { Name = name };
            tagger.Start();
            tagger.Join();
        }

        return sum;
    }

    public static int Square(int value) => value * value; // [depth-square]

    public static string Tag(string name) => "[" + name + "]"; // [depth-tag]

    private static void Wrap()
    {
        try
        {
            throw new ArgumentException("the inner one"); // [depth-inner]
        }
        catch (ArgumentException inner)
        {
            throw new InvalidOperationException("the outer one", inner); // [depth-outer]
        }
    }
}

public sealed class Settings
{
    public int Retries { get; set; }
}

/// <summary>The debugger attributes (M14b): the display string, and members the debugger hides.</summary>
[DebuggerDisplay("Order {Id}: {Customer,nq} ({Lines.Count} lines)")]
public sealed class Order
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _version = 1;

    public int Id { get; init; }

    public string Customer { get; init; } = string.Empty;

    public List<string> Lines { get; } = [];

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Internal => "hidden " + _version;
}
