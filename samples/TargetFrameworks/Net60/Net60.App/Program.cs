using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

namespace Net60.App;

public static class Program
{
    public static int Main(string[] args)
    {
        // C# 10.0, the default for net6.0.
        var point = new Point(1, 2);
        var feature = point with { Y = 3 } is { Y: 3 } ? "record structs" : "?";

        var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location)); // [break]
        Console.WriteLine("runtime=" + runtime + " app=" + Framework(typeof(Program)) + " lib=" + Lib.Greeter.Framework
            + " multi=" + Multi.Library.Framework + " feature=" + feature);
        Console.WriteLine(Lib.Greeter.Greet("net6.0"));
        if (Environment.GetEnvironmentVariable("TARGETFRAMEWORKS_CRYPTO") == "1")
        {
            // Hashing goes through OpenSSL on Linux (eng/compat-containers.sh).
            Console.WriteLine("sha256=" + System.Security.Cryptography.SHA256.Create().ComputeHash(new byte[] { 1 }).Length);
        }

        if (args.Length > 0 && args[0] == "--watch")
        {
            // Hot Reload from .NET 6 (the Watch launch profile): an edit to Message() shows while this runs.
            for (var i = 0; i < 2400; i++)
            {
                Console.WriteLine(Message());
                System.Threading.Thread.Sleep(250);
            }
        }

        // [next]
        return 0;
    }

    private static string Message() => "message: before";

    private static string Framework(Type type) => type.GetTypeInfo().Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
}

public record struct Point(int X, int Y);
