using Library;
using NUnit.Framework;

namespace NUnitTests
{
    [TestFixture]
    public class GreeterTests
    {
        [Test]
        public void Greets_by_name()
        {
            Assert.That(new Greeter("Ada").Greet(), Is.EqualTo("Hello from .NET Framework, Ada!"));
        }

        [TestCase("Grace")]
        [TestCase("Linus")]
        public void Greets_everyone(string name)
        {
            StringAssert.Contains(name, new Greeter(name).Greet());
        }

        [Test]
        public void Runs_on_the_desktop_clr()
        {
            StringAssert.StartsWith(".NET Framework", Greeter.RuntimeName);
        }
    }
}
