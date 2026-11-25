using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>CodeLens resolves a few at a time (<see cref="BoundedResolve"/>), not one round trip after another.</summary>
public sealed class BoundedResolveTests
{
    private static CodeLens Lens(int line) => new(new LanguageServices.Protocol.Range(new Position(line, 0), new Position(line, 1)));

    [Fact]
    public async Task Several_resolves_are_in_flight_at_once_and_all_are_applied()
    {
        var gates = Enumerable.Range(0, 10).Select(_ => new TaskCompletionSource<CodeLens>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = new List<int>();
        var applied = new List<int>();
        var run = BoundedResolve.RunAsync(
            Enumerable.Range(0, 10).ToList(),
            i =>
            {
                lock (started)
                {
                    started.Add(i);
                }

                return gates[i].Task;
            },
            () => true,
            (i, _) => applied.Add(i));

        // A window's worth asked before any answer; then one more for each answer, the last first.
        Assert.Equal([0, 1, 2, 3], started);
        while (!run.IsCompleted)
        {
            int[] waiting;
            lock (started)
            {
                waiting = [.. started.Where(i => !gates[i].Task.IsCompleted)];
            }

            if (waiting.Length > 0)
            {
                Assert.InRange(waiting.Length, 1, BoundedResolve.Window);
                gates[waiting[^1]].SetResult(Lens(waiting[^1]));
            }

            await Task.Yield();
        }

        Assert.Null(await run);
        Assert.Equal(Enumerable.Range(0, 10), applied.Order());
    }

    [Fact]
    public async Task Answers_after_the_text_changed_are_dropped_and_a_failure_is_returned_once()
    {
        var current = true;
        var applied = 0;
        var stale = await BoundedResolve.RunAsync([1, 2, 3], _ => { current = false; return Task.FromResult(Lens(0)); }, () => current, (_, _) => applied++);
        Assert.IsAssignableFrom<OperationCanceledException>(stale);
        Assert.Equal(0, applied);

        var calls = 0;
        var failure = await BoundedResolve.RunAsync(
            Enumerable.Range(0, 8).ToList(),
            i =>
            {
                calls++;
                return i == 1 ? throw new InvalidOperationException("busy") : Task.FromResult(Lens(i));
            },
            () => true,
            (_, _) => applied++);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.True(calls < 8); // none started after the failure
    }
}
