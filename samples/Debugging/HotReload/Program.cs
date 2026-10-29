// Hot Reload while debugging (M55): the tests edit Greeter.Message while the program runs or is stopped, and read
// what it prints. With an argument, it waits for that file before its loop (attach tests).
if (args.Length > 0)
{
    Console.WriteLine("waiting");
    while (!File.Exists(args[0]))
    {
        Thread.Sleep(50);
    }
}

for (var i = 0; i < 2000; i++)
{
    Console.WriteLine(Greeter.Message(i)); // [call]
    Thread.Sleep(50);
}

static class Greeter
{
    public static string Message(int i)
    {
        var text = "hello " + i; // [message]
        return text;
    }
}
