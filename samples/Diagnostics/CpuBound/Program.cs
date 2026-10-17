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

        // A round works 25 ms by the clock, then sleeps 125: the thread works a sixth of the time, well behind the main
        // thread, so IsPrime keeps the most self time and its [hot] line the most of the file's (Mix had 6 to 9% of a
        // trace's samples here, IsPrime 85 to 90%). The work outlasts a timer tick (15.6 ms on Windows), so it spans one
        // wherever it starts. The sampler sleeps a millisecond between samples, but a process that doesn't get the 1 ms
        // timer period it asks for sleeps to the next tick: a round of 2 ms between sleeps of 15 ms then woke on the same
        // tick as the sampler, which found it still leaving Thread.Sleep and didn't sample again before it slept once
        // more, so Mix could get no samples at all (twice on PR #25's Windows runs, while IsPrime had plenty). The clock
        // is read every 50 calls (about 0.3 ms), so the samples a late stop moves to the GC poll after the read (see
        // da35619d) stay few.
        public static void Run(DateTime until)
        {
            var value = 17UL;
            var round = new Stopwatch();
            while (DateTime.UtcNow < until)
            {
                round.Restart();
                while (round.ElapsedMilliseconds < 25)
                {
                    for (var i = 0; i < 50; i++)
                    {
                        value = Mix(value);
                    }
                }

                Interlocked.Increment(ref Rounds);
                Thread.Sleep(125);
            }

            GC.KeepAlive(value);
        }

        // The rounds loop inside, without calls, as in Primes.IsPrime: the JIT makes such a method fully interruptible,
        // so a sample stops the thread in it. A one-round Mix had no safe point of its own (a leaf called from Run's
        // loop): where the interruption comes late (some Windows runners), the thread stopped at the call's return or at
        // Thread.Sleep, Run's frames, and Mix got no samples at all. A call takes about 7 µs in the Debug build the
        // tests profile.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong Mix(ulong value)
        {
            for (var round = 0; round < 1_000; round++)
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
