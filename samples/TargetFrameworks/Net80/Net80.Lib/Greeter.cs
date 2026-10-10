using System.Reflection;
using System.Runtime.Versioning;

namespace Net80.Lib
{
    /// <summary>The net8.0 library.</summary>
    public static class Greeter
    {
        public static string Framework => typeof(Greeter).GetTypeInfo().Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        public static string Greet(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Standard20.Text.Shout("nobody");
            }

            return "Hello, " + name + " from " + Framework + (Total(1, 2) == 3 ? string.Empty : "?");
        }

        /// <summary>Through the .NET Standard 2.1 library.</summary>
        public static int Total(int a, int b) => Standard21.Spans.Sum(new[] { a, b });
    }
}
