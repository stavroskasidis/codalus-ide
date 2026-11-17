using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The UI thread's longest wait at input priority while a flow runs (M56: the UI must never hang, under any load), shared by
/// the stall-budget tests (EverydayResponsivenessTests) and the benchmarks' everyday flows (<c>--bench everyday</c>). A thread
/// of its own posts a probe at <see cref="DispatcherPriority.Input"/> every 10 ms and times how long each waits to run: a job
/// that blocks the UI thread, and a queue of jobs above input (Avalonia's queued scrolls ran at Default), delay a key press
/// by as long. The smoke test's sweep monitor probes at Default, under such a queue. While a probe waits, the monitor
/// samples the flow's <see cref="Step"/> and the job posted from another thread that the UI thread runs
/// (<see cref="Ui.RunningJob"/>, often none: the flow's own work, layout and timers aren't posted jobs) to name the longest
/// wait, and counts the garbage collector's pauses in it, which aren't the flow's code.
/// </summary>
internal sealed partial class UiStallMonitor : IDisposable
{
    /// <summary>A wait longer than this is a stall: a key press that waited so long shows (<see cref="UiStallReport.StalledMilliseconds"/>).</summary>
    public const double StallMilliseconds = 50;

    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private volatile bool _stopped;
    private double _longest;
    private double _longestGc;
    private string? _longestJob;
    private string? _longestStep;
    private string? _longestCollections;
    private volatile string? _step;
    private int _probes;
    private int _stalls;
    private double _stalled;
    private long _firstSent;
    private long _waitingSince;
    private long _resetAt;
    private string? _longestKernel;

    /// <summary>On Linux, the UI thread's id in the kernel (0 until known): a stall samples its state there.</summary>
    private volatile int _uiThreadId;

    public UiStallMonitor()
    {
        Ui.Watch();
        if (OperatingSystem.IsLinux())
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _uiThreadId = GetTid();
            }
            else
            {
                Dispatcher.UIThread.Post(() => _uiThreadId = GetTid(), DispatcherPriority.Send);
            }
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "UI stall monitor" };
        _thread.Start();
    }

    [LibraryImport("libc", EntryPoint = "gettid")]
    private static partial int GetTid();

    /// <summary>
    /// The UI thread's state in the kernel (Linux): <c>R</c> running or runnable, <c>S</c> asleep (a wait), <c>D</c> in an
    /// uninterruptible wait (I/O, a page fault on a busy disk), <c>t</c> stopped by a tracer, with the kernel function it
    /// waits in when the kernel says. A wait in no posted job could be the UI thread runnable and not run, waiting in the
    /// kernel, or stopped (Linux CI, run 44073138183: 2428 ms while createdump wrote a core of the test host): this tells
    /// which. Null off Linux or when it can't be read.
    /// </summary>
    private string? KernelState()
    {
        if (_uiThreadId is not (> 0 and var id))
        {
            return null;
        }

        try
        {
            var task = $"/proc/self/task/{id}/";
            var stat = File.ReadAllText(task + "stat");
            var state = stat.LastIndexOf(')') is var end and >= 0 && end + 2 < stat.Length ? stat[end + 2].ToString() : "?";
            var wchan = File.ReadAllText(task + "wchan").Trim();
            return state != "R" && wchan is { Length: > 0 } and not "0" ? $"{state} in {wchan}" : state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The longest wait since the last <see cref="Reset"/>, without the garbage collector's pauses in it.</summary>
    public double LongestMilliseconds
    {
        get
        {
            lock (_gate)
            {
                return _longest;
            }
        }
    }

    /// <summary>What the flow being measured is doing, set by its driver: the longest wait names the step it was spent in.</summary>
    public string? Step
    {
        get => _step;
        set => _step = value;
    }

    /// <summary>What the monitor measured since the last <see cref="Reset"/>.</summary>
    public UiStallReport Report()
    {
        lock (_gate)
        {
            return new UiStallReport(_probes, _longest, _longestGc, _longestJob, _stalls, _stalled, _longestStep, _longestCollections, _longestKernel);
        }
    }

    /// <summary>
    /// When the first probe since the last <see cref="Reset"/> was posted (a <see cref="Stopwatch"/> timestamp; 0 before
    /// one). A wait the UI thread spent blocked that no probe measured says whether the monitor's own thread ran during
    /// it (macOS CI, run 41968010181: a 600 ms block and one probe of 0 ms).
    /// </summary>
    internal long FirstSent => Interlocked.Read(ref _firstSent);

    /// <summary>
    /// When the probe now waiting for the UI thread was posted (a <see cref="Stopwatch"/> timestamp; 0 while none waits):
    /// a test that holds the UI thread holds it until a probe has waited as long as it means to measure, whenever the
    /// monitor's thread got to post it (Windows CI, run 43178619011: 154 ms measured of a 300 ms block).
    /// </summary>
    internal long WaitingSince => Interlocked.Read(ref _waitingSince);

    /// <summary>Runs on the monitor's thread after a probe is answered, before it is recorded (tests force interleavings with it).</summary>
    internal Action? BeforeRecord { get; set; }

    /// <summary>
    /// Forgets what was measured. A probe answered before it isn't counted, though it may be recorded after it: it read as a
    /// 0 ms measurement of what followed, before the probe that waited through that was recorded (macOS CI, run
    /// 42894614787). The probe on its way at the reset is counted: it is the one that measures a stall right after it, as
    /// no other is posted while it waits (dropping it failed two stall tests on Linux CI, run 42903894635).
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _firstSent, 0);
        lock (_gate)
        {
            _resetAt = Stopwatch.GetTimestamp();
            _longest = 0;
            _longestGc = 0;
            _longestJob = null;
            _longestStep = null;
            _longestCollections = null;
            _longestKernel = null;
            _probes = 0;
            _stalls = 0;
            _stalled = 0;
        }
    }

    private void Loop()
    {
        var jobs = new Dictionary<string, int>(StringComparer.Ordinal);
        var steps = new Dictionary<string, int>(StringComparer.Ordinal);
        var kernel = new Dictionary<string, int>(StringComparer.Ordinal);
        while (!_stopped)
        {
            using var answered = new ManualResetEventSlim();
            var sent = Stopwatch.GetTimestamp();
            var gcMark = GcReport.Mark();
            long ran = 0;
            Dispatcher.UIThread.Post(() =>
            {
                ran = Stopwatch.GetTimestamp();
                answered.Set();
            }, DispatcherPriority.Input);
            Interlocked.CompareExchange(ref _firstSent, sent, 0);
            Interlocked.Exchange(ref _waitingSince, sent);

            jobs.Clear();
            steps.Clear();
            kernel.Clear();
            var stepSent = _step;
            while (!answered.Wait(5))
            {
                if (_stopped)
                {
                    return;
                }

                var job = Ui.RunningJob ?? string.Empty;
                jobs[job] = jobs.GetValueOrDefault(job) + 1;
                var step = _step ?? string.Empty;
                steps[step] = steps.GetValueOrDefault(step) + 1;
                if (Stopwatch.GetElapsedTime(sent).TotalMilliseconds > StallMilliseconds && KernelState() is { } state)
                {
                    kernel[state] = kernel.GetValueOrDefault(state) + 1;
                }
            }

            Interlocked.Exchange(ref _waitingSince, 0);
            var gc = (GC.GetTotalPauseDuration() - gcMark.Paused).TotalMilliseconds;
            var waited = Stopwatch.GetElapsedTime(sent, Volatile.Read(ref ran)).TotalMilliseconds - gc;
            BeforeRecord?.Invoke();
            lock (_gate)
            {
                Record(Volatile.Read(ref ran), waited, gc, gcMark, jobs, steps, stepSent, kernel);
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Counts an answered probe (under the gate), unless it was answered before the last <see cref="Reset"/>.</summary>
    private void Record(long answeredAt, double waited, double gc, GcMark gcMark, Dictionary<string, int> jobs, Dictionary<string, int> steps, string? stepSent, Dictionary<string, int> kernel)
    {
        if (answeredAt < _resetAt)
        {
            return; // it measured only time before the reset (see Reset)
        }

        _probes++;
        if (waited > StallMilliseconds)
        {
            _stalls++;
            _stalled += waited;
        }

        if (waited > _longest)
        {
            _longest = waited;
            _longestGc = gc;
            _longestJob = jobs.Where(p => p.Key.Length > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
            _longestStep = steps.Where(p => p.Key.Length > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? stepSent;
            _longestCollections = GcReport.Since(gcMark);
            _longestKernel = kernel.Count == 0 ? null : string.Join(", ", kernel.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}×"));
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join();
        Ui.Unwatch();
    }
}

/// <summary>
/// What a <see cref="UiStallMonitor"/> measured: the probes answered, the longest wait (without the GC's pauses, which are
/// <see cref="LongestGcMilliseconds"/>), the posted job the UI thread ran most during it and the flow's step then, and the
/// waits over <see cref="UiStallMonitor.StallMilliseconds"/>: how many, and their sum (each without its GC pauses). The
/// collections during the longest wait are named (<see cref="GcReport"/>): a wait of 1011 ms in no posted job with only 135 ms
/// of pauses, under a whole local run of the shell tests (the Git Repository's filter flow, 0 ms alone), could be the UI
/// thread held by a background collection, which the pauses don't count (docs/ci-flakes.md). On Linux, the UI thread's
/// states in the kernel during it, sampled every 5 ms past the first 50 (<see cref="LongestKernel"/>).
/// </summary>
internal readonly record struct UiStallReport(int Probes, double LongestMilliseconds, double LongestGcMilliseconds, string? LongestJob, int Stalls, double StalledMilliseconds, string? LongestStep = null, string? LongestCollections = null, string? LongestKernel = null)
{
    /// <summary>What the longest wait was: its length, its GC pauses, the posted job the UI thread ran most during it, the collections and the UI thread's kernel states then.</summary>
    public string Describe() =>
        $"{LongestMilliseconds:0} ms (and {LongestGcMilliseconds:0} ms of GC pauses) in {LongestJob ?? "no posted job (layout, rendering, input or a handler)"}{(LongestStep is null ? string.Empty : $" at {LongestStep}")}, over {Probes} probes"
        + (LongestCollections is null ? string.Empty : $"; during it, {LongestCollections}")
        + (LongestKernel is null ? string.Empty : $"; the UI thread in the kernel then: {LongestKernel}");
}
