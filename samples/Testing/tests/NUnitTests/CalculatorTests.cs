using Calculator;
using NUnit.Framework;

namespace NUnitTests;

[TestFixture]
[Category("Math")]
public class CalculatorTests
{
    [Test]
    public void Adds()
    {
        var sum = Arithmetic.Add(2, 3); // [breakpoint]
        Assert.That(sum, Is.EqualTo(5));
    }

    [Test]
    public void Fails() => Assert.That(Arithmetic.Add(2, 3), Is.EqualTo(4));

    [Test]
    [Ignore("Not today")]
    public void Skipped()
    {
    }

    [TestCase(6, 3, 2)]
    [TestCase(9, 3, 3)]
    public void Divides(int a, int b, int expected) => Assert.That(Arithmetic.Divide(a, b), Is.EqualTo(expected));

    [Test]
    public void WritesOutput()
    {
        TestContext.Out.WriteLine("hello from nunit");
        Assert.Pass();
    }

    [Test]
    public void Hangs()
    {
        if (Environment.GetEnvironmentVariable("DAEDALUS_FIXTURE_HANG") == "1")
        {
            Thread.Sleep(TimeSpan.FromMinutes(10));
        }
    }
}
