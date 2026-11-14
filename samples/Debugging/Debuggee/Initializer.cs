namespace Debuggee;

/// <summary>
/// A stop while another thread runs a class constructor (<c>--initializer</c>): a func-eval that needs the class waits
/// for that constructor, which never ends while its thread is suspended, unless the debugger lets the thread run.
/// </summary>
public static class Initializer
{
    public static int Run()
    {
        var thread = new Thread(() => _ = Slow.Value) { Name = "Initializer", IsBackground = true };
        thread.Start();
        Started.Wait();
        Console.WriteLine("initializing"); // [initializer-stop]
        thread.Join();
        return Slow.Twice() - 84;
    }

    /// <summary>Set once <see cref="Slow"/>'s constructor runs (outside the class, so reading it doesn't start it).</summary>
    internal static readonly ManualResetEventSlim Started = new();
}

/// <summary>A class whose constructor takes two seconds.</summary>
public static class Slow
{
    static Slow()
    {
        Initializer.Started.Set();
        Thread.Sleep(2000); // [slow-cctor]
        Value = 42;
    }

    public static int Value { get; }

    public static int Twice() => Value * 2;
}
