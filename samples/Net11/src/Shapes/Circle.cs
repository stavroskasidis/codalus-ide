namespace Shapes;

public sealed record Circle(double Radius)
{
    public double Area => Math.PI * Radius * Radius;

    /// <summary>The .NET this library was built for: <c>net10.0</c> or <c>net11.0</c>.</summary>
    public static string Framework =>
#if NET11_0_OR_GREATER
        "net11.0";
#else
        "net10.0";
#endif
}
