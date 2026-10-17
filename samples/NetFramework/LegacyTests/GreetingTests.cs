using Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegacyTests
{
    [TestClass]
    public class GreetingTests
    {
        [TestMethod]
        public void Greets_by_name()
        {
            Assert.AreEqual("Hello from .NET Framework, Grace!", new Greeter("Grace").Greet());
        }

        [DataTestMethod]
        [DataRow("Ada")]
        [DataRow("Linus")]
        public void Greets_everyone(string name)
        {
            StringAssert.Contains(new Greeter(name).Greet(), name);
        }
    }
}
