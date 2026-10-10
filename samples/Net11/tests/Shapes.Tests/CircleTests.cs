using Shapes;
using Xunit;

namespace Shapes.Tests;

public class CircleTests
{
    [Fact]
    public void Area_of_a_unit_circle_is_pi() => Assert.Equal(Math.PI, new Circle(1).Area, 10);

    [Fact]
    public void The_library_matches_the_test_framework() =>
        Assert.Equal(Environment.Version.Major >= 11 ? "net11.0" : "net10.0", Circle.Framework);
}
