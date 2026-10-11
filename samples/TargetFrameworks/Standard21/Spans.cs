using System;
using System.Reflection;
using System.Runtime.Versioning;

namespace Standard21
{
    /// <summary>A .NET Standard 2.1 library (spans in the API), referenced from netcoreapp3.0 on.</summary>
    public static class Spans
    {
        public static string Framework => typeof(Spans).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        public static int Sum(ReadOnlySpan<int> values)
        {
            var sum = 0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum;
        }
    }
}
