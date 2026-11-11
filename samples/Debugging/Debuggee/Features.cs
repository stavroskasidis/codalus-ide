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
