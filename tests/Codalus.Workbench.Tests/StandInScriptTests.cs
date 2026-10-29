using System.Collections.Concurrent;
using System.Diagnostics;
using Codalus.Tests;

namespace Codalus.Workbench.Tests;

/// <summary>
/// <see cref="StandInScript"/>: a stand-in written while other threads start processes starts every time. Written by
/// this process itself, 1 in 25 failed with "Text file busy" on Linux (#209: the Connect test's stand-in node, WSL).
/// </summary>
public sealed class StandInScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-stand-in-script-" + Guid.NewGuid().ToString("N"));

    public StandInScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_stand_in_written_while_processes_start_starts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows runs a script whatever has it open; its stand-ins are .cmd files.");
        using var stop = new CancellationTokenSource();
        var starting = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var process = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
                process.WaitForExit();
            }
        })).ToArray();

        var failures = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var script = Path.Combine(_root, $"stand-in-{writer}-{i}");
                StandInScript.Write(script, "#!/bin/sh\necho v22.11.0\n");
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(script) { UseShellExecute = false, RedirectStandardOutput = true })!;
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (output != "v22.11.0\n")
                    {
                        failures.Add($"{script}: {output}");
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }
        })));
        await stop.CancelAsync();
        await Task.WhenAll(starting);

        Assert.True(failures.IsEmpty, $"{failures.Count} of 300 didn't start: {failures.FirstOrDefault()}");
        Assert.Empty(Directory.GetFiles(_root, "*.text")); // the text cp copied is gone
    }
}
