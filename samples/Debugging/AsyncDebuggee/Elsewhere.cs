using System.Diagnostics;
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

/// <summary>
/// A caller that has the task of the method it awaits only as the call's return value (Linux CI, runs 43828283396 and
/// 43865623534): a step's completion held the caller's thread right after the call, before it had an awaiter of the
/// task, while the method resumed elsewhere. The test holds it there (a Step Out of the method before its await, then
/// the thread frozen) and lets the method resume (<see cref="ResumeOnGo.Go"/>). Back, the caller waits for the task
/// to complete before it awaits it, so the method's completion resumes nothing and the caller goes on on its own thread.
/// </summary>
public static class Handed
{
    public static async Task<int> HandAsync(int value)
    {
        var handed = await HandedAsync(value).OnceCompleted(); // [handed-call]
        return handed + 1; // [handed-return]
    }

    private static async Task<int> HandedAsync(int value)
    {
        var start = value + 1; // [handed-start]
        await new ResumeOnGo();
        var resumed = start * 10; // [handed-resumed]
        return resumed;
    }

    [DebuggerNonUserCode]
    private static Task<int> OnceCompleted(this Task<int> task)
    {
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        return task;
    }
}

/// <summary>Never complete when awaited: the continuation runs on a new thread once the debugger sets <see cref="Go"/>.</summary>
[DebuggerNonUserCode]
public readonly struct ResumeOnGo
{
    public static volatile bool Go;

    public Awaiter GetAwaiter() => default;

    [DebuggerNonUserCode]
    public readonly struct Awaiter : INotifyCompletion
    {
        public bool IsCompleted => false;

        public void OnCompleted(Action continuation) =>
            new Thread(() =>
            {
                while (!Go)
                {
                    Thread.Sleep(10);
                }

                continuation();
            }) { IsBackground = true }.Start();

        public void GetResult()
        {
        }
    }
}
