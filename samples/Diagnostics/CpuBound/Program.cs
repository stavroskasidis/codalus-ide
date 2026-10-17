#nullable enable
// CpuBound: busy work for the profiler and the counters (M18). Runs for the seconds given (default 5; 0: until
// killed). The hot path is Primes.IsPrime's inner loop, marked [hot]; a second thread hashes. It publishes an
// EventCounter (Codalus-Samples-CpuBound/work-items) and a meter (Codalus.Samples.CpuBound/items.processed,
// batch.duration).
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using CpuBound;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 5;
var until = seconds <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"CpuBound {Environment.ProcessId} started for {(seconds <= 0 ? "ever" : seconds + " s")}");

using var meter = new Meter("Codalus.Samples.CpuBound");
var processed = meter.CreateCounter<long>("items.processed", "{item}");
var batches = meter.CreateHistogram<double>("batch.duration", "ms");

var hashing = new Thread(() => Hashing.Run(until)) { IsBackground = true, Name = "Hashing" };
hashing.Start();

// A batch counts the primes below a million (0.1 to 0.2 s), long on purpose. The sampler stops each thread for every
// sample; where the interruption that stops a running thread comes late (by milliseconds on some Windows runners), the
// thread stops at its next safe point instead, the GC poll after Stopwatch's clock read at the batch's end. With short
// batches (the primes below 20,000: 0.7 ms) that took nearly every sample, and Stopwatch.get_Elapsed was the hottest
// function; with long ones a late stop still lands in Primes.IsPrime.
const int BatchLimit = 1_000_000;

long total = 0;
var watch = new Stopwatch();
while (DateTime.UtcNow < until)
{
    watch.Restart();
    var found = Primes.Count(BatchLimit);
    total += found;
    processed.Add(found);
    WorkEvents.Log.WorkDone(found);
    batches.Record(watch.Elapsed.TotalMilliseconds);
}

hashing.Join();
Console.WriteLine($"CpuBound finished: {total} primes, {Hashing.Rounds} hash rounds");

namespace CpuBound
{
    internal static class Primes
    {
        public static int Count(int limit)
        {
            var count = 0;
            for (var n = 2; n < limit; n++)
            {
                if (IsPrime(n))
                {
                    count++;
                }
            }

            return count;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsPrime(int n)
        {
            for (var d = 2; d * d <= n; d++)
            {
                if (n % d == 0) // [hot]
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static class Hashing
    {
        public static long Rounds;

        // A round is one call of Mix that works about 50 ms, then a sleep of 350: the thread works an eighth of the
        // time, well behind the main thread, so IsPrime keeps the most self time and its [hot] line the most of the
        // file's.
        //
        // The work has no safe point but Mix's loop itself, as the main thread's long batches (see BatchLimit): the
        // sampler stops each thread for every sample, and a thread it can't interrupt at once stops at its next GC poll
        // (or at Thread.Sleep). Where the interruption comes milliseconds late (some Windows runners), every sample of
        // a thread that polls more often than that lands at the poll. Run once read Stopwatch's clock every 50 calls of
        // Mix (0.35 ms) to end a round: that read is a call without the GC transition, with a poll after it, so the
        // hashing thread's samples all went to Stopwatch.get_ElapsedMilliseconds and Thread.Sleep, and Mix had none (PR
        // #23's and #25's Windows runs, while IsPrime had plenty). Even on Linux, where a thread is interrupted within
        // microseconds, the poll took a sixth to a quarter of the thread's samples; with the runtime's interruption off
        // (DOTNET_INTERNAL_ThreadSuspendInjection=0) it took all of them. Now the clock is read only around the call,
        // to size the next one, so a stop that comes up to tens of milliseconds late still lands in Mix.
        public static void Run(DateTime until)
        {
            var value = 17UL;
            var rounds = 1_000_000;
            var call = new Stopwatch();
            while (DateTime.UtcNow < until)
            {
                call.Restart();
                value = Mix(value, rounds);
                rounds = (int)Math.Clamp(rounds * 50.0 / Math.Max(call.Elapsed.TotalMilliseconds, 0.1), 10_000, 1_000_000_000);
                Interlocked.Increment(ref Rounds);
                Thread.Sleep(350);
            }

            GC.KeepAlive(value);
        }

        // A loop without calls, as in Primes.IsPrime: no GC poll, and the JIT makes such a method fully interruptible, so
        // a sample stops the thread in it. NoInlining keeps it a frame of its own in an optimized build too. A round
        // takes about 7 ns in the Debug build the tests profile.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong Mix(ulong value, int rounds)
        {
            for (var round = 0; round < rounds; round++)
            {
                value ^= value >> 33;
                value *= 0xff51afd7ed558ccdUL;
                value ^= value >> 33;
            }

            return value;
        }
    }

    /// <summary>A custom EventCounter for the Counters monitor.</summary>
    [System.Diagnostics.Tracing.EventSource(Name = "Codalus-Samples-CpuBound")]
    internal sealed class WorkEvents : System.Diagnostics.Tracing.EventSource
    {
        public static readonly WorkEvents Log = new();

        private System.Diagnostics.Tracing.IncrementingEventCounter? _work;

        public void WorkDone(int items) => _work?.Increment(items);

        protected override void OnEventCommand(System.Diagnostics.Tracing.EventCommandEventArgs command)
        {
            if (command.Command == System.Diagnostics.Tracing.EventCommand.Enable)
            {
                _work ??= new System.Diagnostics.Tracing.IncrementingEventCounter("work-items", this)
                {
                    DisplayName = "Work Items",
                    DisplayRateTimeScale = TimeSpan.FromSeconds(1),
                };
            }
        }
    }
}
