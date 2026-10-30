using System.Diagnostics;
using Avalonia.Threading;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// The UI thread's longest wait at input priority while a flow runs (M56: the UI must never hang, under any load). A thread
/// of its own posts a probe at <see cref="DispatcherPriority.Input"/> every 10 ms and times how long each waits to run: a job
/// that blocks the UI thread, and a queue of jobs above input (Avalonia's queued scrolls ran at Default), delay a key press
/// by as long. The smoke test's sweep monitor probes at Default, under such a queue. While a probe waits, the monitor
/// samples the posted job the UI thread runs (<see cref="Ui.RunningJob"/>) to name the longest wait, and counts the
/// garbage collector's pauses in it, which aren't the flow's code.
/// </summary>
internal sealed class UiStallMonitor : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private volatile bool _stopped;
    private double _longest;
    private double _longestGc;
    private string? _longestJob;
    private int _probes;

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

    /// <summary>What the longest wait was: its length, its GC pauses, the posted job the UI thread ran most during it.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return $"{_longest:0} ms (and {_longestGc:0} ms of GC pauses) in {_longestJob ?? "no posted job (layout, rendering, input or a handler)"}, over {_probes} probes";
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _longest = 0;
            _longestGc = 0;
            _longestJob = null;
            _probes = 0;
        }
    }

    /// <summary>Fails when the UI thread held a probe longer than <paramref name="budgetMilliseconds"/> since the last reset.</summary>
    public void AssertResponsive(string flow, double budgetMilliseconds)
    {
        lock (_gate)
        {
            Assert.True(_probes > 0, $"{flow}: the stall monitor saw no probe answered");
            Assert.True(_longest <= budgetMilliseconds, $"{flow}: the UI thread didn't answer input for {Describe()}; the budget is {budgetMilliseconds:0} ms");
        }
    }

    private void Loop()
    {
        var jobs = new Dictionary<string, int>(StringComparer.Ordinal);
        while (!_stopped)
        {
            using var answered = new ManualResetEventSlim();
            var sent = Stopwatch.GetTimestamp();
            var gcBefore = GC.GetTotalPauseDuration();
            long ran = 0;
            Dispatcher.UIThread.Post(() =>
            {
                ran = Stopwatch.GetTimestamp();
                answered.Set();
            }, DispatcherPriority.Input);

            jobs.Clear();
            while (!answered.Wait(5))
            {
                if (_stopped)
                {
                    return;
                }

                var job = Ui.RunningJob ?? string.Empty;
                jobs[job] = jobs.GetValueOrDefault(job) + 1;
            }

            var gc = (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
            var waited = Stopwatch.GetElapsedTime(sent, Volatile.Read(ref ran)).TotalMilliseconds - gc;
            lock (_gate)
            {
                _probes++;
                if (waited > _longest)
                {
                    _longest = waited;
                    _longestGc = gc;
                    _longestJob = jobs.Where(p => p.Key.Length > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
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
