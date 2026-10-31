// Hot Reload while debugging (M55): the tests edit Greeter.Message while the program runs or is stopped, and read
// what it prints. With an argument, it waits for that file before its loop (attach tests).
[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(UpdateHandler))]

if (args.Length > 0)
{
    Console.WriteLine("waiting");
    while (!File.Exists(args[0]))
    {
        Thread.Sleep(50);
    }
}

var later = Path.Combine(AppContext.BaseDirectory, "later");
for (var i = 0; i < 2000; i++)
{
    Console.WriteLine(Greeter.Message(i)); // [call]
    if (File.Exists(later))
    {
        Greeter.CallLater();
    }

    Thread.Sleep(50);
}

static class Greeter
{
    // Its own method, compiled on its first call: HotReloadLib loads then, not with Main.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void CallLater() => Console.WriteLine(HotReloadLib.Later.Text());

    public static string Message(int i)
    {
        var text = "hello " + i; // [message]
        return text;
    }
}

// Called after each edit the debugger applies, through the startup hook's agent (ADR 0090), as dotnet watch's calls it.
static class UpdateHandler
{
    public static void UpdateApplication(Type[]? types) => Console.WriteLine("updated: " + string.Join(",", types?.Select(t => t.Name) ?? []));
}
