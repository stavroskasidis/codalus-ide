using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Says when the UI thread stops answering, from a thread of its own. The step limit and the watchdog both run on the UI
/// thread, so a step that blocks it reports nothing until the CI step's own limit ends the run, and a runner that goes
/// silent before that keeps no log: Windows smoke's runner in <c>profiler.memory</c> printed nothing for 45 minutes and
/// left none (run 45434861768, docs/ci-flakes.md). These lines go to the console, which the smoke's live output uploads
/// as it grows, so the next one says which step and posted job held the UI thread, and which processes held the memory.
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>How long the UI thread may not answer before a line says so (and again each such while it still doesn't).</summary>
    private static readonly TimeSpan s_hangReport = TimeSpan.FromSeconds(60);

    private long _uiAnswered = Stopwatch.GetTimestamp();

    private void StartHangReporter()
    {
        Ui.Watch(); // Ui.RunningJob names the posted job the UI thread runs
        new Thread(ReportHangs) { IsBackground = true, Name = "Smoke hang reporter" }.Start();
    }

    private void ReportHangs()
    {
        var said = 0L;
        var hung = false;
        while (true)
        {
            Thread.Sleep(5000);
            Dispatcher.UIThread.Post(() => Volatile.Write(ref _uiAnswered, Stopwatch.GetTimestamp()), DispatcherPriority.Send);
            var silent = Stopwatch.GetElapsedTime(Volatile.Read(ref _uiAnswered));
            if (silent >= s_hangReport)
            {
                if (said == 0 || Stopwatch.GetElapsedTime(said) >= s_hangReport)
                {
                    said = Stopwatch.GetTimestamp();
                    hung = true;
                    Say(string.Create(CultureInfo.InvariantCulture,
                        $"  [smoke] the UI thread hasn't answered for {silent.TotalSeconds:0} s (step '{_runningStep ?? "none"}', running {Ui.RunningJob ?? "no posted job"}); {Memory()}"));
                }
            }
            else if (hung)
            {
                hung = false;
                said = 0;
                Say("  [smoke] the UI thread answers again");
            }
        }
    }

    /// <summary>This process's memory and the machine's three largest processes.</summary>
    internal static string Memory()
    {
        var largest = new List<(long Bytes, string Name)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    largest.Add((process.WorkingSet64, $"{process.ProcessName} ({process.Id})"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // gone, or not ours to read
                }
            }
        }

        static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB";
        return $"this process {Mb(Environment.WorkingSet)} (GC heap {Mb(GC.GetTotalMemory(forceFullCollection: false))}); largest: "
            + string.Join(", ", largest.OrderByDescending(p => p.Bytes).Take(3).Select(p => $"{p.Name} {Mb(p.Bytes)}"));
    }

    private static void Say(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // the console is gone: nothing to tell
        }
    }
}
