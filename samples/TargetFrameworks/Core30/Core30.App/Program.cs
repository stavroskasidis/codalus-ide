using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

namespace Core30.App
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // C# 8.0, the default for netcoreapp3.0.
            var count = args.Length;
            var feature = count switch { 0 => "switch expression", _ => "switch expression" };

            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location)); // [break]
            Console.WriteLine("runtime=" + runtime + " app=" + Framework(typeof(Program)) + " lib=" + Lib.Greeter.Framework
                + " multi=" + Multi.Library.Framework + " feature=" + feature);
            Console.WriteLine(Lib.Greeter.Greet("netcoreapp3.0"));
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
}
