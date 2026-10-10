namespace Debuggee;

/// <summary>The M14 debugger-depth scenarios (run with <c>--depth</c>). Tests find lines by the "// [name]" markers.</summary>
public static class Depth
{
    public static int Run()
    {
        var json = "{\"name\":\"Ada\",\"tags\":[\"math\",\"engines\"],\"age\":36}";
        var xml = "<person name=\"Ada\"><tag>math</tag></person>";
        var settings = new Settings { Retries = 3 };
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
        Console.WriteLine($"{json.Length} {xml.Length} {settings.Retries} {sum}"); // [depth-end]
        worker.Join();
        return sum;
    }

    public static int Square(int value) => value * value; // [depth-square]

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
