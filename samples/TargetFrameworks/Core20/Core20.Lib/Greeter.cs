using System.Reflection;
using System.Runtime.Versioning;

namespace Core20.Lib
{
    /// <summary>The netcoreapp2.0 library.</summary>
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

        public static int Total(int a, int b) => a + b;
    }
}
