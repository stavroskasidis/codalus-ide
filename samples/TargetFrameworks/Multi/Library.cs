using System.Reflection;
using System.Runtime.Versioning;

namespace Multi
{
    /// <summary>A multi-targeted library: each app gets the nearest of its three frameworks.</summary>
    public static class Library
    {
        public static string Framework => typeof(Library).GetTypeInfo().Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

#if NET8_0_OR_GREATER
        public static string Build => "net8.0 build";
#elif NETCOREAPP3_1
        public static string Build => "netcoreapp3.1 build";
#else
        public static string Build => "netstandard2.0 build";
#endif
    }
}
