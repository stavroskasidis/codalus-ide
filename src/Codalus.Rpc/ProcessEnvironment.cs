namespace Codalus.Rpc;

/// <summary>
/// Whether this process's environment, which every process it starts inherits, is complete (M56). Started from the macOS
/// Finder or a desktop launcher, the shell takes the login shell's <c>PATH</c> and network settings while its window
/// shows: a host or tool started meanwhile waits for them, at most as long as taking them may last (5 s), instead of the
/// window waiting. <see cref="LocalProcessLauncher"/> and the shell's command-line runners wait here before they start.
/// </summary>
public static class ProcessEnvironment
{
    private static Task s_ready = Task.CompletedTask;

    /// <summary>Completes once the environment is complete: at once, unless something is still being added to it.</summary>
    public static Task Ready => Volatile.Read(ref s_ready);

    /// <summary>Processes started from now on wait for <paramref name="ready"/> to end, however it ends.</summary>
    public static void WaitFor(Task ready) => Volatile.Write(ref s_ready, ready);

    /// <summary>Waits until the environment is complete; at once when it is.</summary>
    public static Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        var ready = Ready;
        return ready.IsCompleted ? Task.CompletedTask : WaitAsync(ready, cancellationToken);

        static async Task WaitAsync(Task ready, CancellationToken cancellationToken)
        {
            await ready.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
