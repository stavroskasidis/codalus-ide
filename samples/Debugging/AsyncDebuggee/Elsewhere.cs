using System.Runtime.CompilerServices;

namespace AsyncDebuggee;

/// <summary>
/// An await that resumes on another thread at once and takes a while there before the method's next line (its
/// GetResult waits): stopped as the awaiting thread leaves the method, the method is already running elsewhere (M61b,
/// the race of Linux CI run 43568784192 made certain). Stepped into from its caller, as there: a step into a state
/// machine makes its kickoff code that isn't the user's, so the step back out of the method lands in the caller.
/// </summary>
public static class Elsewhere
{
    public static async Task<int> CallAsync(int value)
    {
        var raced = await RaceAsync(value); // [race-call]
        return raced;
    }

    private static async Task<int> RaceAsync(int value)
    { // [race-open]
        var start = value + 1; // [race-before]
        await new ResumeElsewhere(); // [race-await]
        var after = start * 2; // [race-after]
        return after;
    }
}

/// <summary>
/// Never complete when awaited: the continuation runs on a new thread at once, and OnCompleted returns only once the
/// resumed method is in GetResult there (its state says it runs again), which then waits a second.
/// </summary>
public readonly struct ResumeElsewhere
{
    private static readonly ManualResetEventSlim s_resumed = new();

    public Awaiter GetAwaiter() => default;

    public readonly struct Awaiter : INotifyCompletion
    {
        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            new Thread(() => continuation()) { IsBackground = true }.Start();
            s_resumed.Wait(TimeSpan.FromSeconds(10));
        }

        public void GetResult()
        {
            s_resumed.Set();
            Thread.Sleep(1000);
        }
    }
}

/// <summary>
/// A caller held before it has the task of the method it awaits (M61b): the method suspends, resumes on another thread
/// at once and stops there, while the thread that called it is still inside the method's await (the awaiter's
/// OnCompleted waits until the method releases it, and half a second more). The task names no awaiting call yet: the
/// caller is on the other thread's stack.
/// </summary>
public static class Held
{
    public static async Task<int> HoldAsync(int value)
    {
        var held = await HeldAsync(value); // [held-call]
        return held + 1; // [held-return]
    }

    private static async Task<int> HeldAsync(int value)
    {
        await new HoldCaller();
        var resumed = value * 10; // [held-resumed]
        HoldCaller.Release();
        return resumed;
    }
}

/// <summary>Never complete when awaited: the continuation runs on a new thread, and OnCompleted returns once released.</summary>
public readonly struct HoldCaller
{
    private static readonly ManualResetEventSlim s_released = new();

    public static void Release() => s_released.Set();

    public Awaiter GetAwaiter() => default;

    public readonly struct Awaiter : INotifyCompletion
    {
        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            new Thread(() => continuation()) { IsBackground = true }.Start();
            s_released.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(500);
        }

        public void GetResult()
        {
        }
    }
}
