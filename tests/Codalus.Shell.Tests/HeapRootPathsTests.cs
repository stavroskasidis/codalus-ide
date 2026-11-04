namespace Codalus.Shell.Tests;

/// <summary>The diagnostic of a shell that stays reachable names what holds it.</summary>
public class HeapRootPathsTests
{
    private static List<object>? s_holder;

    [Fact]
    public void A_root_path_names_what_holds_the_object()
    {
        s_holder = [new Held(7)];
        try
        {
            var paths = HeapRootPaths.Describe(typeof(Held).FullName!, o => o.ReadField<int>("<Mark>k__BackingField") == 7);
            var expected = OperatingSystem.IsMacOS()
                ? "no snapshot of the process on macOS"
                : "System.Collections.Generic.List<System.Object> -> System.Object[] -> " + typeof(Held).FullName;
            Assert.True(paths.Contains(expected, StringComparison.Ordinal), paths);
        }
        finally
        {
            s_holder = null;
        }
    }

    private sealed record Held(int Mark);
}
