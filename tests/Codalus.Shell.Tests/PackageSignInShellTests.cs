using Codalus.Workbench.Packages;

namespace Codalus.Shell.Tests;

/// <summary>Package source sign-in in the shell (deferred from M9a); the flow itself is in Codalus.Projects.Tests' PackageSignInTests.</summary>
public sealed class PackageSignInShellTests
{
    [Fact]
    public void Device_code_messages_give_their_page_and_code()
    {
        Assert.Equal(("https://microsoft.com/devicelogin", "ABCD1234"),
            ShellHost.DeviceCode("[CredentialProvider]To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate."));
        Assert.Equal(("https://login.example.invalid/device", "FAKE-1234"),
            ShellHost.DeviceCode("Open https://login.example.invalid/device. Then enter the code FAKE-1234."));
        Assert.Null(ShellHost.DeviceCode("Restoring packages…"));
    }

    [Theory]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized).", true)]
    [InlineData("Response status code does not indicate success: 403 (Forbidden).", true)]
    [InlineData("Consider re-running the command with --interactive for `dotnet`", true)]
    [InlineData("No such host is known.", false)]
    public void Errors_that_need_a_sign_in_are_recognized(string message, bool expected) =>
        Assert.Equal(expected, PackageService.NeedsSignIn(message));
}
