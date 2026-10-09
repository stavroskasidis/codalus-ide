namespace Lib;

/// <summary>Something with an area.</summary>
public interface IShape
{
    /// <summary>The area in square units.</summary>
    double Area();
}

/// <summary>Base class of the sample shapes.</summary>
public abstract class Shape : IShape
{
    public abstract double Area();

    /// <summary>Describes the shape.</summary>
    /// <param name="precision">Digits after the decimal point.</param>
    public virtual string Describe(int precision) => $"{GetType().Name}: {Area().ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture)}";
}

public sealed class Circle(double radius) : Shape
{
    public double Radius { get; } = radius;

    public override double Area() => System.Math.PI * Radius * Radius;

    public override string Describe(int precision) => "Circle " + base.Describe(precision);
}

#region Squares
public sealed partial class Square : Shape
{
    public override double Area() => Side * Side;
}
#endregion

public static class Geometry
{
    /// <summary>Scales a shape's area.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="factor">The scale factor.</param>
    public static double Scale(IShape shape, double factor) => shape.Area() * factor;

    /// <summary>Scales a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="factor">The scale factor.</param>
    /// <param name="round">Whether to round the result.</param>
    public static double Scale(double value, double factor, bool round = false) => round ? System.Math.Round(value * factor) : value * factor;
}
