using Debuggee;

// Tests find lines by the "// [name]" markers; keep each marker on its statement's line.
if (args.Length == 2 && args[0] == "--wait-for") // [first]
{
    // Attach tests: wait until the test creates the file, then run as usual.
    Console.WriteLine("waiting");
    while (!File.Exists(args[1])) // [wait-check]
    {
        Thread.Sleep(50); // [wait]
    }
}

if (args.Length == 2 && args[0] == "--main-returns")
{
    // Break All tests: Main returns while a foreground thread waits for the file. The main thread stays in the runtime,
    // waiting for it, with no managed frames; it says "main returned" once the runtime has made the main thread a
    // background thread, which it does once Main has returned.
    var main = Thread.CurrentThread;
    var waiter = new Thread(() =>
    {
        while (!main.IsBackground)
        {
            Thread.Sleep(10);
        }

        Console.WriteLine("main returned");
        while (!File.Exists(args[1])) // [waiter-check]
        {
            Thread.Sleep(50); // [waiter]
        }
    }) { Name = "Waiter" };
    waiter.Start();
    return 0;
}

if (args is ["--depth"])
{
    return Depth.Run(); // [depth-call]
}

if (args is ["--datatip"])
{
    return DataTip.Run(); // [datatip-call]
}

if (args is ["--read"])
{
    // A real console (debugging in the terminal, M14c): read a line and echo it.
    Console.WriteLine("echo: " + Console.ReadLine()); // [read]
}

if (args is ["--crash"])
{
    // Just-In-Time debugging (M14c): an unhandled exception ends the program, and the runtime writes a dump.
    throw new InvalidOperationException("crashed on purpose"); // [crash]
}

var numbers = new List<int> { 1, 2, 3, 4, 5 };
var total = 0;
for (var i = 0; i < numbers.Count; i++)
{
    total += numbers[i]; // [loop]
}

var person = new Person("Ada", 36);
var description = Describer.Describe(person); // [call]
Console.WriteLine($"total={total} {description}"); // [print]

var worker = new Thread(Worker.Run) { Name = "Worker" };
worker.Start();
worker.Join();

try
{
    Thrower.Throw();
}
catch (InvalidOperationException ex)
{
    Console.WriteLine("caught " + ex.Message);
}

Console.WriteLine("done"); // [done]
return total;
