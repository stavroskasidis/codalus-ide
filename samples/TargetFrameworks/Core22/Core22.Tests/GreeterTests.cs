using System.IO;
using Xunit;

namespace Core22.Tests
{
    public class GreeterTests
    {
        [Fact]
        public void Greets_from_its_framework()
        {
            Assert.Equal("Hello, tests from " + Lib.Greeter.Framework, Lib.Greeter.Greet("tests"));
            Assert.EndsWith(",Version=v2.2", Lib.Greeter.Framework);
        }

        [Fact]
        public void Runs_on_its_runtime()
        {
            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location));
            Assert.StartsWith("2.2.", runtime);
        }
    }
}
