using Codalus.Platform;

namespace Codalus.Workbench.Tests;

/// <summary>
/// <see cref="ShellIntegration.RevealStartInfo"/>: Explorer gets <c>/select,</c> unquoted with the path quoted after it,
/// so a path with spaces selects the item instead of opening Documents; Finder and xdg-open get plain arguments.
/// </summary>
public sealed class ShellIntegrationTests
{
    [Theory]
    [InlineData(@"D:\Projects\Diablo II - The Board Game\Diablo II - The Board Game.sln")]
    [InlineData(@"D:\Projects\Diablo II - The Board Game")]
    [InlineData(@"C:\src\app\Program.cs")]
    public void Explorer_gets_the_select_switch_unquoted_and_the_path_quoted(string path)
    {
        var start = ShellIntegration.RevealStartInfo(path, PlatformKind.Windows);

        Assert.Equal("explorer.exe", start.FileName);
        Assert.Equal($"/select,\"{path}\"", start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void Finder_gets_the_path_as_one_argument()
    {
        var start = ShellIntegration.RevealStartInfo("/Users/me/Diablo II - The Board Game/Game.sln", PlatformKind.MacOS);

        Assert.Equal("open", start.FileName);
        Assert.Equal(["-R", "/Users/me/Diablo II - The Board Game/Game.sln"], start.ArgumentList);
    }
}
