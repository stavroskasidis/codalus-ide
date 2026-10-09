using Calculator;
using Xunit;
using Xunit.Abstractions;

namespace XunitV2Tests;

public class CalculatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Adds()
    {
        var sum = Arithmetic.Add(2, 3); // [breakpoint]
        Assert.Equal(5, sum);
    }

    [Fact]
    public void Fails() => Assert.Equal(4, Arithmetic.Add(2, 3));

    [Fact(Skip = "Not today")]
    public void Skipped()
    {
    }

    [Theory]
    [InlineData(6, 3, 2)]
    [InlineData(9, 3, 3)]
    public void Divides(int a, int b, int expected) => Assert.Equal(expected, Arithmetic.Divide(a, b));

    [Fact]
    public void WritesOutput()
    {
        output.WriteLine("hello from xunit v2");
        Assert.True(true);
    }

    [Fact]
    public void Hangs()
    {
        if (Environment.GetEnvironmentVariable("DAEDALUS_FIXTURE_HANG") == "1")
        {
            Thread.Sleep(TimeSpan.FromMinutes(10));
        }
    }
}
