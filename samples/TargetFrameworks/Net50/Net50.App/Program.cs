using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

namespace Net50.App
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // C# 9.0, the default for net5.0.
            var point = new Point(1, 2);
            var feature = point with { Y = 3 } is { Y: 3 } ? "records" : "?";

            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location)); // [break]
            Console.WriteLine("runtime=" + runtime + " app=" + Framework(typeof(Program)) + " lib=" + Lib.Greeter.Framework
                + " multi=" + Multi.Library.Framework + " feature=" + feature);
            Console.WriteLine(Lib.Greeter.Greet("net5.0"));
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

    public record Point(int X, int Y);
}
