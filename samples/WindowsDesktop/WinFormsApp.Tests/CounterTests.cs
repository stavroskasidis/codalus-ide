namespace WinFormsApp.Tests;

[TestClass]
public sealed class CounterTests
{
    [TestMethod]
    public void Increment_stops_at_the_maximum()
    {
        var counter = new Counter { Maximum = 2 };
        Assert.IsTrue(counter.Increment());
        Assert.IsTrue(counter.Increment());
        Assert.IsFalse(counter.Increment());
        Assert.AreEqual(2, counter.Value);
    }

    [TestMethod]
    public void Reset_goes_back_to_zero()
    {
        var counter = new Counter();
        counter.Increment();
        counter.Reset();
        Assert.AreEqual(0, counter.Value);
    }
}
