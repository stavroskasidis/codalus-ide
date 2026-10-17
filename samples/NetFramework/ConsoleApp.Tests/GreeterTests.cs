using Library;
using Xunit;

namespace ConsoleApp.Tests
{
    public class GreeterTests
    {
        [Fact]
        public void Greets_by_name()
        {
            Assert.Equal("Hello from .NET Framework, Ada!", new Greeter("Ada").Greet());
        }

        [Fact]
        public void Runs_on_the_desktop_clr()
        {
            Assert.StartsWith(".NET Framework", Greeter.RuntimeName);
        }
    }
}
