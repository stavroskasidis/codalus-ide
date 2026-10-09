namespace Debuggee;

public sealed record Person(string Name, int Age);

public static class Describer
{
    public static string Describe(Person person)
    {
        var text = $"{person.Name} is {person.Age}"; // [describe]
        return text;
    }
}

public static class Worker
{
    public static void Run()
    {
        var name = Thread.CurrentThread.Name; // [worker]
        Console.WriteLine("worker " + name);
    }
}

public static class Thrower
{
    public static void Throw() => throw new InvalidOperationException("boom"); // [throw]
}
