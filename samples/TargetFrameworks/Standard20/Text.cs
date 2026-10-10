using System.Reflection;
using System.Runtime.Versioning;

namespace Standard20
{
    /// <summary>A .NET Standard 2.0 library, referenced by every framework.</summary>
    public static class Text
    {
        public static string Framework => typeof(Text).GetTypeInfo().Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        public static string Shout(string text) => text.ToUpperInvariant() + "!";
    }
}
