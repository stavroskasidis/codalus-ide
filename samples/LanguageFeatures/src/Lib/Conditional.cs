namespace Lib;

public static class Conditional
{
#if NET10_0
    public static string Framework => "net10.0";

    public static string ModernOnly() => "only on .NET 10";
#else
    public static string Framework => "netstandard2.0";

    public static string LegacyOnly() => "only on .NET Standard";
#endif
}
