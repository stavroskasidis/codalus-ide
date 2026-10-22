using Codalus.Platform;

namespace Codalus.Workbench.Tests;

/// <summary>Typed paths (path boxes, New Project's location): <c>~</c> and environment variables, each OS's way.</summary>
public sealed class UserPathsTests
{
    private static readonly Dictionary<string, string> s_variables = new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/ann",
        ["REPOS"] = "/work/repos",
        ["USERPROFILE"] = @"C:\Users\Ann",
    };

    private static string Expand(string text, bool windows, string home = "/home/ann") =>
        UserPaths.Expand(text, name => s_variables.GetValueOrDefault(name), home, windows);

    [Fact]
    public void The_home_folder()
    {
        Assert.Equal("/home/ann", Expand("~", windows: false));
        Assert.Equal("/home/ann/source/repos", Expand("~/source/repos", windows: false));
        Assert.Equal(@"C:\Users\Ann\source", Expand(@"~\source", windows: true, home: @"C:\Users\Ann\"));
        Assert.Equal("~other/x", Expand("~other/x", windows: false)); // another user's home isn't expanded
        Assert.Equal("/x/~", Expand("/x/~", windows: false));
    }

    [Fact]
    public void Variables_each_os_its_way()
    {
        Assert.Equal(@"C:\Users\Ann\source", Expand(@"%USERPROFILE%\source", windows: true));
        Assert.Equal("/work/repos/app", Expand("%REPOS%/app", windows: false)); // %VAR% everywhere, as .NET does
        Assert.Equal("/work/repos/app", Expand("$REPOS/app", windows: false));
        Assert.Equal("/work/repos-old", Expand("${REPOS}-old", windows: false));
        Assert.Equal("$REPOS/app", Expand("$REPOS/app", windows: true)); // not a Windows variable
        Assert.Equal("%MISSING%/a", Expand("%MISSING%/a", windows: false));
        Assert.Equal("$MISSING/a", Expand("$MISSING/a", windows: false));
        Assert.Equal("100% $5 $", Expand("100% $5 $", windows: false));
    }

    [Fact]
    public void Whitespace_is_trimmed_and_odd_text_comes_back()
    {
        Assert.Equal("/home/ann/x", Expand("  ~/x \t", windows: false));
        Assert.Equal(string.Empty, Expand("   ", windows: false));
        Assert.Equal("%%", Expand("%%", windows: false));
        Assert.Equal("${unclosed", Expand("${unclosed", windows: false));
        Assert.Equal("plain", UserPaths.Expand(" plain "));
    }
}
