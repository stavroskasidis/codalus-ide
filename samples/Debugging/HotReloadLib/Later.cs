namespace HotReloadLib;

/// <summary>Loaded late: HotReload calls it only once a file named "later" is next to it (M55's tests).</summary>
public static class Later
{
    public static string Text() => "later";
}
