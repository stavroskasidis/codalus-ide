using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

namespace Net100.App;

public static class Program
{
    public static int Main(string[] args)
    {
        // C# 14.0, the default for net10.0.
        var holder = new Holder { Name = " field keyword " };
        var feature = holder.Name;

        var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location)); // [break]
        Console.WriteLine("runtime=" + runtime + " app=" + Framework(typeof(Program)) + " lib=" + Lib.Greeter.Framework
            + " multi=" + Multi.Library.Framework + " feature=" + feature);
        Console.WriteLine(Lib.Greeter.Greet("net10.0"));
        if (Environment.GetEnvironmentVariable("TARGETFRAMEWORKS_CRYPTO") == "1")
        {
            // Hashing goes through OpenSSL on Linux (eng/compat-containers.sh).
            Console.WriteLine("sha256=" + System.Security.Cryptography.SHA256.Create().ComputeHash(new byte[] { 1 }).Length);
        }

        // [next]
        return 0;
    }

    private static string Framework(Type type) => type.GetTypeInfo().Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
}

public class Holder
{
    public string Name { get; set => field = value.Trim(); } = "";
}
