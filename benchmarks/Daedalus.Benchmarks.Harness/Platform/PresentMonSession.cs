using System.Diagnostics;
using System.Globalization;

namespace Daedalus.Benchmarks.Harness.Platform;

/// <summary>One present (frame handed to the compositor) as recorded by PresentMon, in QPC milliseconds.</summary>
public sealed record PresentEvent(int ProcessId, double CpuStartMs, double CpuBusyMs, double GpuBusyMs, double? DisplayLatencyMs)
{
    /// <summary>When the app called Present for this frame.</summary>
    public double PresentMs => CpuStartMs + CpuBusyMs;
}

/// <summary>
/// Wraps Intel PresentMon (MIT; downloaded to benchmarks/tools, never committed). Captures every
/// process with the target's executable name, which covers Electron GPU helper processes.
/// Requires admin or membership of "Performance Log Users".
/// </summary>
internal sealed class PresentMonSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _csv;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _output;

    private PresentMonSession(Process process, string csv, System.Collections.Concurrent.ConcurrentQueue<string> output)
    {
        _process = process;
        _csv = csv;
        _output = output;
    }

    /// <summary>PresentMon's console output (for diagnostics).</summary>
    public string Output => string.Join(Environment.NewLine, _output);

    public bool Exited => _process.HasExited;

    public static string? Locate(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "benchmarks", "tools", "presentmon", "PresentMon.exe");
        return File.Exists(path) ? path : null;
    }

    public static async Task<PresentMonSession> StartAsync(string presentMon, string processName, string workDir)
    {
        var csv = Path.Combine(workDir, $"presentmon-{Guid.NewGuid():N}.csv");
        var psi = new ProcessStartInfo(presentMon)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "--process_name", processName, "--output_file", csv, "--v2_metrics", "--qpc_time_ms",
                     "--no_console_stats", "--stop_existing_session", "--session_name", "DaedalusBench",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("PresentMon failed to start.");
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.Enqueue(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.Enqueue(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.Delay(1500).ConfigureAwait(false); // ETW session start-up.
        return new PresentMonSession(process, csv, output);
    }

    public async Task<IReadOnlyList<PresentEvent>> StopAsync()
    {
        // PresentMon stops cleanly on its own session being terminated.
        Machine.Run(_process.StartInfo.FileName, "--terminate_existing_session --session_name DaedalusBench");
        if (!_process.WaitForExit(10_000))
        {
            _process.Kill();
        }

        await Task.Delay(200).ConfigureAwait(false);
        return File.Exists(_csv) ? Parse(await File.ReadAllLinesAsync(_csv).ConfigureAwait(false)) : [];
    }

    internal static List<PresentEvent> Parse(IReadOnlyList<string> lines)
    {
        var result = new List<PresentEvent>();
        if (lines.Count < 2)
        {
            return result;
        }

        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name);
        // CPUStartTime + CPUBusy is when Present was called; CPUWait is time blocked inside Present.
        int pid = Col("ProcessID"), start = Col("CPUStartTime"), busy = Col("CPUBusy"), gpu = Col("GPUBusy"), display = Col("DisplayLatency");
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length != header.Length || !double.TryParse(cells[start], CultureInfo.InvariantCulture, out var cpuStart))
            {
                continue;
            }

            result.Add(new PresentEvent(
                int.Parse(cells[pid], CultureInfo.InvariantCulture),
                cpuStart,
                Number(cells[busy]),
                Number(cells[gpu]),
                double.TryParse(cells[display], CultureInfo.InvariantCulture, out var d) ? d : null));
        }

        return result;
    }

    private static double Number(string cell) => double.TryParse(cell, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// For each input (QPC ms), the latency to the first present whose frame started at or after the input:
    /// earlier frames cannot contain it. Inputs with no such present within <paramref name="maxMs"/> are dropped.
    /// </summary>
    public static List<double> InputToPresent(IReadOnlyList<double> inputsMs, IReadOnlyList<PresentEvent> presents, double maxMs = 500)
    {
        var ordered = presents.OrderBy(p => p.CpuStartMs).ToArray();
        var latencies = new List<double>();
        foreach (var input in inputsMs)
        {
            var frame = ordered.FirstOrDefault(p => p.CpuStartMs >= input);
            if (frame is not null && frame.PresentMs - input <= maxMs)
            {
                latencies.Add(frame.PresentMs - input);
            }
        }

        return latencies;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            await StopAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }
}
