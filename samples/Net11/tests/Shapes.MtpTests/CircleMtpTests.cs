using Shapes;
using Xunit;

namespace Shapes.MtpTests;

public class CircleMtpTests
{
    [Fact]
    public void Runs_on_net11() => Assert.Equal(("net11.0", 11), (Circle.Framework, Environment.Version.Major));
}
