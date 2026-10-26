using System.Runtime.CompilerServices;
using Avalonia;

namespace Codalus.Shell.Tests;

/// <summary>
/// Keeps what one headless test leaves running from reaching the later ones through Avalonia's root locator (task #80).
/// </summary>
/// <remarks>
/// Each test runs in a locator scope of its own over the root, which stays empty: Avalonia binds the services it makes on
/// demand, MediaContext among them (it runs the layout passes and places popups), in the test's scope. Between two tests
/// no scope is active and Dispatcher.UIThread is unset, so work an earlier test left running (a shell's background job)
/// that reaches Avalonia from a pool thread then makes that thread the UI thread and binds such a service at the root, on
/// that thread's dispatcher. Every later test's scope falls back to it, and their layout passes and popup placements are
/// queued on a dispatcher nothing runs, for the rest of the run: the completion list stayed at the window's top ("the
/// list's top 0 is above the line's bottom 194") and the shell tests timed out waiting for a document's view. So each
/// test starts from an empty root, and says what it found there.
/// </remarks>
internal static class HeadlessIsolation
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Current")]
    private static extern IAvaloniaDependencyResolver Current(AvaloniaLocator? locator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parentScope")]
    private static extern ref IAvaloniaDependencyResolver? Parent(AvaloniaLocator locator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_registry")]
    private static extern ref Dictionary<Type, Func<object?>> Registry(AvaloniaLocator locator);

    /// <summary>The headless test set up before this one: the likely one to have left the work running (one line in 40 CI runs).</summary>
    private static string? s_previous;

    /// <summary>Empties the root locator, from the scope the headless session made for the test it sets up.</summary>
    public static void ClearRootLocator()
    {
        var current = Xunit.TestContext.Current.Test?.TestDisplayName;
        var previous = s_previous;
        s_previous = current;
        var root = (AvaloniaLocator)Current(null);
        while (Parent(root) is AvaloniaLocator parent)
        {
            root = parent;
        }

        var registry = Registry(root);
        if (registry.Count > 0)
        {
            Console.WriteLine($"Headless isolation: the root locator held {string.Join(", ", registry.Keys.Select(t => t.Name))}, bound by work an earlier test left running (the headless test before this one: {previous ?? "none"}; this one: {current ?? "unknown"}); cleared.");
            registry.Clear();
        }
    }
}
