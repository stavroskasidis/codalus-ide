using System.Runtime.InteropServices;
using Xunit;

namespace MultiTargetTests;

public class FrameworkTests
{
    [Fact]
    public void ReportsFramework() => Assert.StartsWith(".NET", RuntimeInformation.FrameworkDescription);
}
