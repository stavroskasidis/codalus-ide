namespace Debuggee;

/// <summary>
/// The scenarios of the Codalus debugger's own features (M58; run with <c>--features</c>): hit counts, logpoints,
/// filters and dependencies in the debugger, Set Next Statement, Step Into Specific, returned values and frozen
/// threads. Tests find lines by the "// [name]" markers.
/// </summary>
public static class Features
{
    private static int s_ticks;
    private static volatile bool s_stop;

    public static int Run()
    {
        var count = 0; // [features-start]
        for (var i = 0; i < 6; i++)
        {
            count += i; // [features-loop]
        }

        var text = Shout(Join("a", Twice("b"))); // [features-nested]
        var length = Measure(text); // [features-measure]
        Console.WriteLine(text + " " + length); // [features-print]

        foreach (var name in new[] { "Tagger A", "Tagger B" })
        {
            var tagger = new Thread(() => Console.WriteLine(Mark(name))) { Name = name };
            tagger.Start();
            tagger.Join();
        }

        // Type proxies, Results View, [DebuggerDisplay] names, new, null and boxed arguments, object IDs.
        var basket = new Basket("fruit", ["apple", "pear"]);
        var linked = new LinkedList<int>([3, 1, 2]);
        var sorted = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 };
        var evens = Evens(7);
        var none = Evens(0);
        var empty = string.Empty;
        Console.WriteLine($"{basket.Items.Count} {linked.Count} {sorted.Count} {evens.Count()} {none.Count()} {empty.Length}"); // [features-views]
        basket = new Basket("empty", []);
        GC.KeepAlive(basket); // [features-ids]

        var ticker = new Thread(Tick) { Name = "Ticker" };
        ticker.Start();
        while (Volatile.Read(ref s_ticks) == 0)
        {
            Thread.Sleep(5);
        }

        Thread.Sleep(150); // [features-freeze]
        Thread.Sleep(150); // [features-frozen]
        Thread.Sleep(150); // [features-thawed]
        s_stop = true; // [features-stop]
        ticker.Join();
        Console.WriteLine("features done " + count); // [features-end]
        return count;
    }

    public static string Twice(string value) => value + value; // [features-twice]

    public static string Join(string left, string right) => left + "-" + right; // [features-join]

    public static string Shout(string value) => value.ToUpperInvariant() + "!"; // [features-shout]

    public static string Mark(string name) => "<" + name + ">"; // [features-mark]

    public static IEnumerable<int> Evens(int below)
    {
        for (var n = 0; n < below; n += 2)
        {
            yield return n;
        }
    }

    public static string Label(object? value, string? suffix) => (value?.ToString() ?? "nothing") + (suffix ?? "!");

    public static int Measure(string value)
    {
        var length = value.Length; // [features-measure-body]
        return length;
    }

    private static void Tick()
    {
        while (!s_stop)
        {
            Interlocked.Increment(ref s_ticks);
            Thread.Sleep(10);
        }
    }
}

/// <summary>A type with a proxy of its own: the proxy's public members are its view, its own are under Raw View.</summary>
[System.Diagnostics.DebuggerTypeProxy(typeof(BasketView))]
public sealed class Basket(string name, List<string> items)
{
    private readonly string _secret = "kept";

    public string Name { get; } = name;

    public List<string> Items { get; } = items;

    public override string ToString() => Name + _secret.Length;

    private sealed class BasketView(Basket basket)
    {
        private readonly Basket _basket = basket;

        public string Summary => $"{_basket.Name}: {_basket.Items.Count}";

        [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.RootHidden)]
        public Fruit[] Fruits => [.. _basket.Items.Select((item, index) => new Fruit(index, item))];

        [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
        public string Hidden => "not shown";
    }
}

/// <summary>A row of a proxy's view, named and shown by its <c>[DebuggerDisplay]</c>.</summary>
[System.Diagnostics.DebuggerDisplay("{Name,nq} ({Name.Length})", Name = "#{Index}")]
public sealed record Fruit(int Index, string Name);

/// <summary>A type to create from an expression: <c>new Crate("x", 2)</c>.</summary>
public sealed class Crate(string label, int size)
{
    public string Label { get; } = label;

    public int Size { get; } = size;
}
