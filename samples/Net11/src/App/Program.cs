using System.Globalization;
using Shapes;

// Tests find lines by the "// [name]" markers; keep each marker on its statement's line.
var circle = new Circle(2);
var runtime = Environment.Version; // [break]
Console.WriteLine($"runtime={runtime.Major} library={Circle.Framework} area={circle.Area.ToString("F2", CultureInfo.InvariantCulture)}");

if (args is ["--watch"])
{
    // Hot Reload tests edit the message and wait for the new one.
    while (true)
    {
        Console.WriteLine(Message()); // [loop]
        Thread.Sleep(250);
    }
}

static string Message() => "message: before"; // [message]
