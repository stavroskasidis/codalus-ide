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

    /// <summary>
    /// An object that only a waiting thread's frame holds names that frame and where the thread waits, and a delegate on
    /// the way names its method: a disposed shell was held through an Action on some thread's stack, and the path said
    /// neither which thread nor whose handler (Linux CI, run 43200651803).
    /// </summary>
    [Fact]
    public void A_root_on_a_stack_names_its_frame_and_a_delegate_its_method()
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // no snapshot there (the first test checks what it says)
        }

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() => HoldOnStack(holding, release)) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(holding.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var paths = HeapRootPaths.Describe(typeof(Held).FullName!, o => o.ReadField<int>("<Mark>k__BackingField") == 11);
            Assert.True(paths.Contains(nameof(HoldOnStack), StringComparison.Ordinal), paths);
            Assert.True(paths.Contains($"({typeof(Held).FullName}.{nameof(Held.Touch)})", StringComparison.Ordinal), paths);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void HoldOnStack(ManualResetEventSlim holding, ManualResetEventSlim release)
    {
        Action handler = new Held(11).Touch;
        holding.Set();
        release.Wait();
        GC.KeepAlive(handler);
    }

    private sealed record Held(int Mark)
    {
        public void Touch()
        {
        }
    }
}
