using System.Diagnostics;
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
internal sealed class UiStallMonitor : IDisposable
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

    public UiStallMonitor()
    {
        Ui.Watch();
        _thread = new Thread(Loop) { IsBackground = true, Name = "UI stall monitor" };
        _thread.Start();
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
            return new UiStallReport(_probes, _longest, _longestGc, _longestJob, _stalls, _stalled, _longestStep, _longestCollections);
        }
    }

    /// <summary>
    /// When the first probe since the last <see cref="Reset"/> was posted (a <see cref="Stopwatch"/> timestamp; 0 before
    /// one). A wait the UI thread spent blocked that no probe measured says whether the monitor's own thread ran during
    /// it (macOS CI, run 41968010181: a 600 ms block and one probe of 0 ms).
    /// </summary>
    internal long FirstSent => Interlocked.Read(ref _firstSent);

    public void Reset()
    {
        Interlocked.Exchange(ref _firstSent, 0);
        lock (_gate)
        {
            _longest = 0;
            _longestGc = 0;
            _longestJob = null;
            _longestStep = null;
            _longestCollections = null;
            _probes = 0;
            _stalls = 0;
            _stalled = 0;
        }
    }

    private void Loop()
    {
        var jobs = new Dictionary<string, int>(StringComparer.Ordinal);
        var steps = new Dictionary<string, int>(StringComparer.Ordinal);
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

            jobs.Clear();
            steps.Clear();
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
            }

            var gc = (GC.GetTotalPauseDuration() - gcMark.Paused).TotalMilliseconds;
            var waited = Stopwatch.GetElapsedTime(sent, Volatile.Read(ref ran)).TotalMilliseconds - gc;
            lock (_gate)
            {
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
                }
            }

            Thread.Sleep(10);
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
/// thread held by a background collection, which the pauses don't count (docs/ci-flakes.md).
/// </summary>
internal readonly record struct UiStallReport(int Probes, double LongestMilliseconds, double LongestGcMilliseconds, string? LongestJob, int Stalls, double StalledMilliseconds, string? LongestStep = null, string? LongestCollections = null)
{
    /// <summary>What the longest wait was: its length, its GC pauses, the posted job the UI thread ran most during it, the collections then.</summary>
    public string Describe() =>
        $"{LongestMilliseconds:0} ms (and {LongestGcMilliseconds:0} ms of GC pauses) in {LongestJob ?? "no posted job (layout, rendering, input or a handler)"}{(LongestStep is null ? string.Empty : $" at {LongestStep}")}, over {Probes} probes"
        + (LongestCollections is null ? string.Empty : $"; during it, {LongestCollections}");
}
