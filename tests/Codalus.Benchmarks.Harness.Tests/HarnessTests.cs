using Codalus.Benchmarks.FixtureGen;
using Codalus.Benchmarks.Harness;
using Codalus.Benchmarks.Harness.Model;
using Codalus.Benchmarks.Harness.Platform;
using Codalus.Diagnostics.BenchEvents;

namespace Codalus.Benchmarks.Harness.Tests;

public class StatsTests
{
    [Fact]
    public void Percentiles_use_nearest_rank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(5, Distribution.Percentile(sorted, 0.5));
        Assert.Equal(10, Distribution.Percentile(sorted, 0.99));
        Assert.Equal(1, Distribution.Percentile(sorted, 0.0));
    }

    [Fact]
    public void Distribution_ignores_non_finite_values()
    {
        var d = Distribution.Of([3, double.NaN, 1, 2, double.PositiveInfinity])!;
        Assert.Equal(3, d.N);
        Assert.Equal(1, d.Min);
        Assert.Equal(3, d.Max);
        Assert.Equal(2, d.Mean);
    }

    [Fact]
    public void Distribution_of_nothing_is_null() => Assert.Null(Distribution.Of([]));
}

public class PresentMonTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency";

    [Fact]
    public void Parse_reads_qpc_times_and_busy_durations()
    {
        var presents = PresentMonSession.Parse(
        [
            Header,
            "app.exe,42,0x1,DXGI,0,0,0,Composed: Flip,1000.5,16.6,4.0,12.6,1.0,1.0,0.5,0.5,NA,NA,NA,NA,NA,NA,NA",
            "app.exe,42,0x1,DXGI,0,0,0,Composed: Flip,1017.1,16.6,3.0,13.6,1.0,1.0,0.7,0.3,25.0,NA,NA,NA,NA,NA,NA",
        ]);

        Assert.Equal(2, presents.Count);
        Assert.Equal(1004.5, presents[0].PresentMs, 3);
        Assert.Null(presents[0].DisplayLatencyMs);
        Assert.Equal(25.0, presents[1].DisplayLatencyMs);
        Assert.Equal(0.7, presents[1].GpuBusyMs, 3);
    }

    [Fact]
    public void InputToPresent_pairs_each_input_with_the_first_frame_started_after_it()
    {
        PresentEvent[] presents =
        [
            new(1, CpuStartMs: 100, CpuBusyMs: 5, GpuBusyMs: 1, DisplayLatencyMs: null), // started before the input
            new(1, CpuStartMs: 112, CpuBusyMs: 4, GpuBusyMs: 1, DisplayLatencyMs: null),
            new(1, CpuStartMs: 200, CpuBusyMs: 2, GpuBusyMs: 1, DisplayLatencyMs: null),
        ];

        var latencies = PresentMonSession.InputToPresent([110, 150, 2000], presents);

        Assert.Equal([6, 52], latencies); // 2000 has no frame within the window and is dropped.
    }
}

public class BenchEventLogTests
{
    [Fact]
    public void Read_parses_names_clocks_and_fields_and_skips_torn_lines()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            """{"ev":"interactive","wall_us":1000,"mono_us":10}""",
            """{"ev":"key_latency","wall_us":2000,"mono_us":20,"latency_us":7000,"synthetic":1}""",
            """{"ev":"scroll_fr""",
        ]);

        var events = BenchEventLog.Read(path);

        Assert.Equal(2, events.Count);
        Assert.Equal("interactive", events[0].Name);
        Assert.Equal(1000, events[0].WallMicros);
        Assert.Equal(7000, events[1].Field("latency_us"));
        Assert.Equal(0, events[1].Field("missing"));
    }
}

public class ReportTests
{
    [Fact]
    public void Markdown_has_one_column_per_target_and_one_row_per_metric()
    {
        var machine = new MachineInfo("windows", "Windows 11", "x64", "CPU", 8, 16L << 30, "GPU", 60, IsCi: false);
        ScenarioResult Result(string target, double value)
        {
            var r = new ScenarioResult { Target = target, Scenario = "cold-start", Machine = machine, StartedAt = DateTimeOffset.UnixEpoch, Method = "black-box" };
            r.AddRange("startup_ready_ms", [value, value + 10, value + 20]);
            return r.Summarize();
        }

        var markdown = Report.Markdown([Result("vscode", 900), Result("avalonia", 300)]);

        Assert.Contains("| metric | avalonia | vscode |", markdown);
        Assert.Contains("| startup_ready_ms | 310 / 320 / 320 (3) | 910 / 920 / 920 (3) |", markdown);
    }
}

public class EverydayScenarioTests
{
    private static Target Target(string name) => new(name, IsPrototype: name == "shell", name, name, static (_, _) => new System.Diagnostics.ProcessStartInfo(), InstallDirectory: null);

    [Fact]
    public void The_everyday_flows_are_the_shells_scenario()
    {
        Assert.Contains("everyday", ScenarioRunner.Scenarios);
        Assert.True(ScenarioRunner.Applies(Target("shell"), "everyday"));
        Assert.False(ScenarioRunner.Applies(Target("vscode"), "everyday")); // in-app events only
    }

    [Fact]
    public void Each_flow_gives_its_longest_wait_and_its_stalls_and_a_note_says_what_they_were()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                """{"ev":"everyday_flow","wall_us":1,"mono_us":1,"stall_max_us":120400,"stall_total_us":340250,"stalls":3,"probes":250,"gc_pause_us":12500,"elapsed_us":2500000,"flow":"typing","step":"typing","job":"ShellHost.Editor.cs:612"}""",
                """{"ev":"everyday_note","wall_us":2,"mono_us":2,"note":"the rebuild failed or didn't run"}""",
                """{"ev":"everyday_flow","wall_us":3,"mono_us":3,"stall_max_us":31000,"stall_total_us":0,"stalls":0,"probes":90,"gc_pause_us":0,"elapsed_us":900000,"flow":"build"}""",
                """{"ev":"everyday_flow","wall_us":4,"mono_us":4,"stall_max_us":5000,"stall_total_us":0}""",
                """{"ev":"scenario_done","wall_us":5,"mono_us":5}""",
            ]);
            var machine = new MachineInfo("linux", "Ubuntu", "x64", "CPU", 4, 16L << 30, null, null, IsCi: true);
            var result = new ScenarioResult { Target = "shell", Scenario = "everyday", Machine = machine, StartedAt = DateTimeOffset.UnixEpoch, Method = "instrumented" };

            var summary = ScenarioRunner.AddEverydayFlows(result, BenchEventLog.Read(path), run: 1);

            Assert.Equal([120.4], result.Samples["everyday_typing_stall_max_ms"]);
            Assert.Equal([340.25], result.Samples["everyday_typing_stall_total_ms"]);
            Assert.Equal([31.0], result.Samples["everyday_build_stall_max_ms"]);
            Assert.Equal([0.0], result.Samples["everyday_build_stall_total_ms"]);
            Assert.Equal([12.5], result.Samples["everyday_typing_gc_pause_ms"]);
            Assert.Equal([0.0], result.Samples["everyday_build_gc_pause_ms"]);
            Assert.Equal(6, result.Samples.Count); // a flow without a name isn't one
            Assert.Equal("the longest waits: typing 120 ms at typing in ShellHost.Editor.cs:612 (2.5 s), build 31 ms (0.9 s)", summary);
            Assert.Equal(["run 1: the rebuild failed or didn't run", "Run 1: " + summary], result.Notes);
            Assert.Contains("| everyday_typing_stall_max_ms | 120 |", Report.Markdown([result.Summarize()]), StringComparison.Ordinal);

            Assert.Equal("no flow measured", ScenarioRunner.AddEverydayFlows(result, [], run: 2));

            // A run that stopped early, or whose flows didn't do their work, is a warning on CI, not only a note.
            Assert.Equal("6 of 8 flows not measured (open_tracked, next_document, ctrl_tab, sync_collapsed, explorer_search, close_reopen); 1 note(s) from the driver, in the report",
                ScenarioRunner.EverydayProblem(BenchEventLog.Read(path), timedOut: false));
            Assert.Equal("it didn't finish within 12 minutes; 8 of 8 flows not measured (" + string.Join(", ", BenchEventNames.EverydayFlows) + ")", ScenarioRunner.EverydayProblem([], timedOut: true));
            BenchEvent Flow(string name) => new(BenchEventNames.EverydayFlow, 0, 0, new Dictionary<string, long>()) { Text = new Dictionary<string, string> { ["flow"] = name } };
            Assert.Null(ScenarioRunner.EverydayProblem([.. BenchEventNames.EverydayFlows.Select(Flow)], timedOut: false));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MachineTests
{
    [Fact]
    public void The_l3_cache_size_is_parsed_and_shown()
    {
        Assert.Equal(33792L * 1024, Machine.ParseCacheSize("33792K\n"));
        Assert.Equal(32L << 20, Machine.ParseCacheSize("32M"));
        Assert.Equal(4096, Machine.ParseCacheSize("4096"));
        Assert.Null(Machine.ParseCacheSize("unknown"));
        Assert.Equal("33 MB", Machine.FormatCache(33792L * 1024));
        Assert.Equal("unknown", Machine.FormatCache(null));
    }

    [Fact]
    public void Results_from_before_the_l3_cache_was_recorded_still_parse()
    {
        const string json = """
            { "target": "shell", "scenario": "cold-start", "startedAt": "2026-10-14T20:45:46Z", "method": "instrumented",
              "machine": { "os": "linux", "osVersion": "Ubuntu", "architecture": "x64", "cpu": "AMD EPYC 9V45 96-Core Processor",
                           "logicalCores": 4, "memoryBytes": 16766410752, "isCi": true } }
            """;
        var result = System.Text.Json.JsonSerializer.Deserialize(json, ResultsJsonContext.Default.ScenarioResult)!;
        Assert.Null(result.Machine.L3CacheBytes);

        var recorded = result with { Machine = result.Machine with { L3CacheBytes = 32L << 20 } };
        Assert.Contains("\"l3CacheBytes\": 33554432", System.Text.Json.JsonSerializer.Serialize(recorded, ResultsJsonContext.Default.ScenarioResult), StringComparison.Ordinal);
        Assert.Contains("AMD EPYC 9V45 96-Core Processor (L3 32 MB)", Report.Markdown([recorded]), StringComparison.Ordinal);
    }
}

public class FixtureTests
{
    [Fact]
    public void Large_file_is_deterministic_and_has_the_exact_line_count()
    {
        var a = LargeFileGenerator.Generate(5_000);
        var b = LargeFileGenerator.Generate(5_000);

        Assert.Equal(a, b);
        Assert.Equal(5_000, a.Count(c => c == '\n'));
    }
}
