using System.Diagnostics;
using Avalonia.Threading;
using Codalus.Diagnostics.BenchEvents;

namespace Codalus.Shell.Infrastructure;

internal sealed partial class BenchDriver
{
    /// <summary>The folder of files <c>--bench grammars</c> opens, one per grammar.</summary>
    public const string GrammarsCorpusVariable = "CODALUS_BENCH_GRAMMARS";

    /// <summary>
    /// <c>--bench grammars</c> (with <c>CODALUS_BENCH_GRAMMARS</c> naming a folder): opens its files one after the other,
    /// in name order, and reports for each how long until its grammar's colors were drawn (<c>colored_us</c>) and until its
    /// last line was tokenized (<c>complete_us</c>), with the process's CPU time and allocations meanwhile and its memory
    /// after. The tabs stay open, as a session's would.
    /// </summary>
    private async Task GrammarsAsync()
    {
        var folder = Environment.GetEnvironmentVariable(GrammarsCorpusVariable);
        if (folder is null || !Directory.Exists(folder))
        {
            BenchEvents.Emit(BenchEventNames.ScenarioDone);
            Exit();
            return;
        }

        // Let startup's own work (the Welcome plugin's host, first layout) settle first.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        await RenderedAsync(window).ConfigureAwait(true);

        using var process = Process.GetCurrentProcess();
        var sequenceStarted = Stopwatch.GetTimestamp();
        var sequenceCpu = process.TotalProcessorTime;
        var sequenceAllocated = GC.GetTotalAllocatedBytes(precise: false);
        var gcCount = GC.CollectionCount(0);
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var started = Stopwatch.GetTimestamp();
            var document = await shell.OpenFileAsync(file).ConfigureAwait(true);
            if (document is null)
            {
                continue;
            }

            var controller = await WaitForControllerAsync(document).ConfigureAwait(true);
            var opened = Stopwatch.GetElapsedTime(started);
            long coloredUs = -1, completeUs = -1;
            if (controller.GrammarScope is not null)
            {
                var deadline = TimeSpan.FromSeconds(60);
                while (!controller.GrammarVisibleColored() && Stopwatch.GetElapsedTime(started) < deadline)
                {
                    await Task.Delay(2).ConfigureAwait(true);
                }

                await RenderedAsync(window).ConfigureAwait(true);
                coloredUs = (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                while (!controller.GrammarComplete() && Stopwatch.GetElapsedTime(started) < deadline)
                {
                    await Task.Delay(2).ConfigureAwait(true);
                }

                completeUs = (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            }
            else
            {
                await RenderedAsync(window).ConfigureAwait(true);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            process.Refresh();
            BenchEvents.Emit(
                "grammar_file",
                [
                    ("open_us", (long)opened.TotalMicroseconds),
                    ("colored_us", coloredUs),
                    ("complete_us", completeUs),
                    ("elapsed_us", (long)elapsed.TotalMicroseconds),
                    ("cpu_us", (long)(process.TotalProcessorTime - cpu).TotalMicroseconds),
                    ("allocated_bytes", GC.GetTotalAllocatedBytes(precise: false) - allocated),
                    ("working_set_bytes", process.WorkingSet64),
                    ("gc_heap_bytes", GC.GetTotalMemory(forceFullCollection: false)),
                    ("lines", controller.Editor.Document.LineCount),
                ],
                [("file", Path.GetFileName(file)), ("language", document.Model.LanguageId ?? string.Empty), ("scope", controller.GrammarScope ?? string.Empty)]);
        }

        process.Refresh();
        var total = Stopwatch.GetElapsedTime(sequenceStarted);
        var totalCpu = process.TotalProcessorTime - sequenceCpu;
        var totalAllocated = GC.GetTotalAllocatedBytes(precise: false) - sequenceAllocated;
        var workingSet = process.WorkingSet64;
        var heap = GC.GetTotalMemory(forceFullCollection: false);
        var gen0 = GC.CollectionCount(0) - gcCount;
        var retained = GC.GetTotalMemory(forceFullCollection: true);
        BenchEvents.Emit(
            "grammar_sequence",
            [
                ("elapsed_us", (long)total.TotalMicroseconds),
                ("cpu_us", (long)totalCpu.TotalMicroseconds),
                ("allocated_bytes", totalAllocated),
                ("working_set_bytes", workingSet),
                ("gc_heap_bytes", heap),
                ("gc_retained_bytes", retained),
                ("gen0_collections", gen0),
            ]);
        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        Dispatcher.UIThread.Post(Exit, DispatcherPriority.Background);
    }
}
