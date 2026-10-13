using Calculator;

namespace TUnitTests;

public class CalculatorTests
{
    [Test]
    public async Task Adds()
    {
        var sum = Arithmetic.Add(2, 3); // [breakpoint]
        await Assert.That(sum).IsEqualTo(5);
    }

    [Test]
    public async Task Fails() => await Assert.That(Arithmetic.Add(2, 3)).IsEqualTo(4);

    [Test]
    [Skip("Not today")]
    public void Skipped()
    {
    }

    [Test]
    [Arguments(6, 3, 2)]
    [Arguments(9, 3, 3)]
    public async Task Divides(int a, int b, int expected) => await Assert.That(Arithmetic.Divide(a, b)).IsEqualTo(expected);

    [Test]
    public void WritesOutput() => Console.WriteLine("hello from tunit");

    [Test]
    public void Hangs()
    {
        if (Environment.GetEnvironmentVariable("CODALUS_FIXTURE_HANG") == "1")
        {
            Thread.Sleep(TimeSpan.FromMinutes(10));
        }
    }
}
