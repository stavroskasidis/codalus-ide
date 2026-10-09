using Calculator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace MSTestMtpTests;

[TestClass]
public class CalculatorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Math")]
    public void Adds()
    {
        var sum = Arithmetic.Add(2, 3); // [breakpoint]
        Assert.AreEqual(5, sum);
    }

    [TestMethod]
    public void Fails() => Assert.AreEqual(4, Arithmetic.Add(2, 3));

    [TestMethod]
    [Ignore("Not today")]
    public void Skipped()
    {
    }

    [TestMethod]
    [DataRow(6, 3, 2)]
    [DataRow(9, 3, 3)]
    public void Divides(int a, int b, int expected) => Assert.AreEqual(expected, Arithmetic.Divide(a, b));

    [TestMethod]
    public void WritesOutput()
    {
        TestContext.WriteLine("hello from mstest mtp");
        Console.WriteLine("console from mstest mtp");
    }

    [TestMethod]
    public void Hangs()
    {
        if (Environment.GetEnvironmentVariable("DAEDALUS_FIXTURE_HANG") == "1")
        {
            Thread.Sleep(TimeSpan.FromMinutes(10));
        }
    }
}
