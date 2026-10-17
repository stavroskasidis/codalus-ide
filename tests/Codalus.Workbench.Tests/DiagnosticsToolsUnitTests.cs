using System.Text.Json;
using Codalus.Diagnostics.Host.Cpu;
using Codalus.Diagnostics.Host.Memory;
using Codalus.Diagnostics.Host.Trace;
using Codalus.Diagnostics.Protocol;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The M18b models built by hand (ADR 0046): weighted profiles, async method names, span tags, the heap graph's
/// types, diffs and paths to roots, the Speedscope and Chromium exports, the benchmark finder and BenchmarkDotNet's
/// reports, sessions with snapshots, and the I/O counters.
/// </summary>
public sealed class DiagnosticsToolsUnitTests
{
    /// <summary>Allocations: Main → Make → [String] (3 ticks of 100 KB), Main → [Byte[]] (1 tick of 500 KB).</summary>
    private static (CpuProfile Profile, int Main, int Make, int String, int Bytes) Allocations()
    {
        var p = new CpuProfile { Unit = "KB" };
        var main = p.AddFunction("App.Main", "App");
        var make = p.AddFunction("App.Make", "App");
        var text = p.AddFunction("System.String", "[type]");
        var bytes = p.AddFunction("System.Byte[]", "[type]");
        var sMain = p.AddStack(-1, main, 1);
        var sMake = p.AddStack(sMain, make, 2);
        var sString = p.AddStack(sMake, text, -1);
        var sBytes = p.AddStack(sMain, bytes, -1);
        p.AddSample(1, 1, sString, false, 100);
        p.AddSample(2, 1, sString, false, 100);
        p.AddSample(3, 2, sString, false, 100);
        p.AddSample(4, 1, sBytes, false, 500);
        p.Duration = 10;
        p.Seal();
        return (p, main, make, text, bytes);
    }

    [Theory]
    [InlineData("System.Threading.Thread.<PollGC>g__PollGCWorker|70_0()", true)]
    [InlineData("System.Threading.Thread.PollGC()", true)]
    [InlineData("System.Threading.Thread.Sleep(int32)", false)]
    [InlineData("Primes.IsPrime(int32)", false)]
    public void The_runtimes_gc_poll_is_folded_into_its_caller(string name, bool folded) =>
        Assert.Equal(folded, NettraceReader.IsSuspensionMethod(name));

    [Fact]
    public void Weighted_samples_count_their_weight_in_every_view()
    {
        var (profile, main, _, text, bytes) = Allocations();
        var views = new CpuViews(profile);
        var filter = new CpuFilter(Profile: ProfileKinds.Allocations);
        var types = views.BottomUp(filter, [], 0, 10);
        Assert.Equal(800, types.Total);
        Assert.Equal([bytes, text], types.Nodes.Select(n => n.Function));
        Assert.Equal(500, types.Nodes[0].Inclusive);
        var roots = views.TopDown(filter, -1, 0, 10);
        Assert.Equal(800, Assert.Single(roots.Nodes).Inclusive);
        Assert.Equal(main, roots.Nodes[0].Function);
        Assert.Equal(800, views.Timeline(filter, 5).Samples.Sum());
        Assert.Equal(100, views.Timeline(filter with { Threads = [2] }, 5).Samples.Sum());
        var flame = views.Flame(filter, -1, 0, 100);
        Assert.Equal(800, flame.Total);
        Assert.Contains(flame.Frames, f => f.Function == text && f.Width == 300);
        Assert.NotEqual(CpuViews.Key(filter), CpuViews.Key(new CpuFilter()));
    }

    [Fact]
    public void Comparing_profiles_matches_functions_by_name()
    {
        var (before, _, _, _, _) = Allocations();
        var after = new CpuProfile();
        var main = after.AddFunction("App.Main", "App");
        var make = after.AddFunction("App.Make", "App");
        var sMain = after.AddStack(-1, main, 1);
        var sMake = after.AddStack(sMain, make, 2);
        after.AddSample(1, 1, sMake, false);
        after.AddSample(2, 1, sMain, false);
        after.Seal();
        var result = CpuViews.Compare(new CpuViews(before), new CpuViews(after), new CpuFilter(), 10);
        var makeRow = result.Functions.Single(f => f.Name == "App.Make");
        Assert.Equal(300 / 800.0, makeRow.BaselineTotal, 3);
        Assert.Equal(0.5, makeRow.Total, 3);
        Assert.Equal(0.5, makeRow.Self, 3);
        var bytes = result.Functions.Single(f => f.Name == "System.Byte[]");
        Assert.Equal(0, bytes.Total);
        Assert.True(bytes.Delta < 0);
        Assert.Equal(result.Functions.Max(f => Math.Abs(f.Delta)), Math.Abs(result.Functions[0].Delta));
    }

    [Theory]
    [InlineData("Async: <FetchAsync>d__0", "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int32].Start(!!0&)|Worker+<FetchAsync>d__0.MoveNext()", "Worker.FetchAsync")]
    [InlineData("Async: <Run>d__3", "My.App.Jobs+<Run>d__3.MoveNext()", "My.App.Jobs.Run")]
    [InlineData("Async: <Run>d__3", "Other.Frame()", "Run")]
    [InlineData("Task.Delay", "Other.Frame()", null)]
    public void Async_operations_are_named_by_their_state_machines(string operation, string stack, string? method) =>
        Assert.Equal(method, TraceData.MethodOf(operation, stack.Split('|')));

    [Fact]
    public void Span_tags_are_read_from_their_rendering()
    {
        const string Tags = "db.system:sqlite, db.query.text:SELECT 1, server.port:5432";
        Assert.Equal("sqlite", TraceData.Tag(Tags, "db.system"));
        Assert.Equal("SELECT 1", TraceData.Tag(Tags, "db.query.text"));
        Assert.Equal("5432", TraceData.Tag(Tags, "server.port"));
        Assert.Null(TraceData.Tag(Tags, "http.method"));
        Assert.Null(TraceData.Tag(null, "db.system"));
    }

    /// <summary>
    /// A static Cache (a GC handle root) holds a Dictionary whose Entry[] holds two Orders, each with a string; a stack
    /// root holds a third Order; one string is unreachable.
    /// </summary>
    private static HeapGraph Heap(int orders)
    {
        var builder = new HeapGraph.Builder();
        builder.Type(1, "Cache", []);
        builder.Type(2, "System.Collections.Generic.Dictionary`2", [10, 3]);
        builder.Type(10, "System.Int32", []);
        builder.Type(3, "Order", []);
        builder.Type(4, "System.String", []);
        builder.Type(5, "Entry[]", []);
        builder.Node(0x100, 24, 1, 1);
        builder.Node(0x200, 80, 2, 1);
        builder.Node(0x300, 32 + (8 * orders), 5, orders);
        for (var i = 0; i < orders; i++)
        {
            builder.Node(0x1000 + ((ulong)i * 0x10), 40, 3, 1);
        }

        for (var i = 0; i < orders; i++)
        {
            builder.Node(0x5000 + ((ulong)i * 0x10), 64, 4, 0);
        }

        builder.Node(0x9000, 64, 4, 0); // unreachable
        builder.Edge(0x200); // Cache → Dictionary
        builder.Edge(0x300); // Dictionary → Entry[]
        for (var i = 0; i < orders; i++)
        {
            builder.Edge(0x1000 + ((ulong)i * 0x10)); // Entry[] → Order
        }

        for (var i = 0; i < orders; i++)
        {
            builder.Edge(0x5000 + ((ulong)i * 0x10)); // Order → String
        }

        builder.Root(0x100, 2);
        return builder.Build();
    }

    [Fact]
    public void The_heap_graph_gives_types_diffs_and_paths_to_roots()
    {
        var before = Heap(2);
        var after = Heap(5);
        Assert.Equal(1, after.RootCount);
        Assert.Equal(5, after.TypeCount); // the types with objects (Int32 is only a type parameter)

        var types = after.Types(null, null, 0, 10);
        Assert.Equal(after.Bytes, types.Bytes);
        Assert.Equal("System.String", types.Types[0].Name); // 6 strings of 64 bytes
        Assert.Equal(6, types.Types[0].Count);
        Assert.Contains(types.Types, t => t.Name == "System.Collections.Generic.Dictionary<System.Int32, Order>");
        Assert.Single(after.Types(null, "order", 0, 10).Types, t => t.Name == "Order");

        var diff = after.Types(before, null, 0, 10);
        var orders = diff.Types.Single(t => t.Name == "Order");
        Assert.Equal(3, orders.CountDelta);
        Assert.Equal(120, orders.BytesDelta);
        Assert.DoesNotContain(diff.Types, t => t.Name == "Cache"); // unchanged

        // Order ← Entry[] ← Dictionary ← Cache ← a GC handle.
        var order = types.Types.Single(t => t.Name == "Order").Id;
        var entries = Assert.Single(after.Referrers([order], 0, 10).Referrers);
        Assert.Equal("Entry[]", entries.Name);
        Assert.Equal(5, entries.Count);
        Assert.True(entries.HasChildren);
        var dictionary = Assert.Single(after.Referrers([order, entries.Id], 0, 10).Referrers);
        var cache = Assert.Single(after.Referrers([order, entries.Id, dictionary.Id], 0, 10).Referrers);
        Assert.Equal("Cache", cache.Name);
        var root = Assert.Single(after.Referrers([order, entries.Id, dictionary.Id, cache.Id], 0, 10).Referrers);
        Assert.Equal("[GC handle]", root.Name);
        Assert.False(root.HasChildren);

        // The unreachable string.
        var strings = after.Referrers([types.Types[0].Id], 0, 10).Referrers;
        Assert.Contains(strings, r => r.Id == HeapGraph.Unreached && r.Count == 1);
        Assert.Throws<ArgumentException>(() => after.Referrers([], 0, 10));
    }

    [Fact]
    public void Profiles_export_to_speedscope_and_chromium()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cpu = new CpuProfile();
            var main = cpu.AddFunction("App.Main", "App");
            var work = cpu.AddFunction("App.Work", "App");
            var sMain = cpu.AddStack(-1, main, 1);
            var sWork = cpu.AddStack(sMain, work, 2);
            cpu.AddSample(0, 1, sWork, false);
            cpu.AddSample(1, 1, sWork, false);
            cpu.AddSample(2, 1, sMain, false);
            cpu.Duration = 3;
            cpu.Seal();
            var data = new TraceData(null) { Duration = 3 };
            data.Profiles[ProfileKinds.Cpu] = cpu;
            data.Spans.Add(new ActivityRecord(0.5, 2.5, "round", null, null, "App", "a", null));

            var speedscope = Path.Combine(folder, "cpu.speedscope.json");
            Assert.True(TraceExport.Write(data, new CpuFilter(), ExportFormats.Speedscope, speedscope) > 0);
            using (var document = JsonDocument.Parse(File.ReadAllText(speedscope)))
            {
                var frames = document.RootElement.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
                Assert.Equal(["App.Main", "App.Work"], frames);
                var profile = document.RootElement.GetProperty("profiles")[0];
                Assert.Equal("milliseconds", profile.GetProperty("unit").GetString());
                Assert.Equal(3, profile.GetProperty("endValue").GetDouble());
                Assert.Equal([0, 1], profile.GetProperty("samples")[0].EnumerateArray().Select(e => e.GetInt32()));
                Assert.Equal([0], profile.GetProperty("samples")[2].EnumerateArray().Select(e => e.GetInt32()));
            }

            var chromium = Path.Combine(folder, "cpu.trace.json");
            TraceExport.Write(data, new CpuFilter(), ExportFormats.Chromium, chromium);
            using (var document = JsonDocument.Parse(File.ReadAllText(chromium)))
            {
                var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().Where(e => e.GetProperty("ph").GetString() == "X").ToList();
                var workEvent = events.Single(e => e.GetProperty("name").GetString() == "App.Work");
                Assert.Equal(0, workEvent.GetProperty("ts").GetDouble());
                Assert.Equal(2000, workEvent.GetProperty("dur").GetDouble()); // until the sample without it, in µs
                var mainEvent = events.Single(e => e.GetProperty("name").GetString() == "App.Main");
                Assert.Equal(3000, mainEvent.GetProperty("dur").GetDouble());
                Assert.Contains(events, e => e.GetProperty("name").GetString() == "round" && e.GetProperty("dur").GetDouble() == 2000);
            }

            Assert.Throws<ArgumentException>(() => TraceExport.Write(data, new CpuFilter(), "svg", Path.Combine(folder, "x")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private const string BenchmarkSource = """
        using BenchmarkDotNet.Attributes;

        namespace Perf.Text
        {
            // [Benchmark] in a comment is no benchmark
            [MemoryDiagnoser]
            public class Strings
            {
                [Params(16)]
                public int Count { get; set; }

                private const string Text = "[Benchmark] public void NotOne() { }";

                [Benchmark(Baseline = true)]
                public string Builder() => "x";

                [Benchmark, BenchmarkCategory("Join")]
                public string Join(
                    int a = 1) => "y";

                [BenchmarkCategory("Other")]
                public string Helper() => "z";

                /* [Benchmark] */
                public void AlsoNot() { }

                public class Nested
                {
                    [BenchmarkDotNet.Attributes.Benchmark]
                    public async Task<int> Deep() => await Task.FromResult(1);
                }
            }
        }
        """;

    [Fact]
    public void The_benchmark_finder_reads_attributes_classes_and_namespaces()
    {
        var found = BenchmarkFinder.Find(BenchmarkSource);
        var lines = BenchmarkSource.Split('\n');
        var methods = found.Where(f => !f.IsType).ToList();
        Assert.Equal(["Builder", "Join", "Deep"], methods.Select(m => m.Method));
        Assert.All(methods, m => Assert.Contains(m.Method + "(", lines[m.Line - 1], StringComparison.Ordinal));
        Assert.Equal("Perf.Text.Strings.Builder", methods[0].Filter);
        Assert.Equal("Strings.Nested", methods[2].Type);
        Assert.Equal("Perf.Text.Strings.Nested.Deep", methods[2].Filter);
        var classes = found.Where(f => f.IsType).ToList();
        Assert.Equal(["Strings", "Strings.Nested"], classes.Select(c => c.Type));
        Assert.Equal("Perf.Text.Strings.*", classes[0].Filter);
        Assert.Contains("class Strings", lines[classes[0].Line - 1], StringComparison.Ordinal);
        Assert.True(BenchmarkFinder.NamesBenchmark("method: Benchmark"));
        Assert.False(BenchmarkFinder.NamesBenchmark("BenchmarkCategory(\"x\")"));
        Assert.Empty(BenchmarkFinder.Find("class A { [Fact] public void Test() { } }"));
    }

    [Fact]
    public void BenchmarkDotNet_reports_and_arguments()
    {
        const string Report = """
            {"Title":"x","Benchmarks":[
              {"Namespace":"Benchmarks","Type":"StringBenchmarks","Method":"Concat","MethodTitle":"Concat","Parameters":"Count=16","FullName":"Benchmarks.StringBenchmarks.Concat(Count: 16)",
               "Statistics":{"N":1,"Mean":261500,"Median":261400,"StandardError":0,"StandardDeviation":12.5,"ConfidenceInterval":{"N":1,"Mean":261500,"Margin":""}},
               "Memory":{"Gen0Collections":2,"Gen1Collections":0,"Gen2Collections":0,"TotalOperations":1000,"BytesAllocatedPerOperation":1000}},
              {"Namespace":"Benchmarks","Type":"StringBenchmarks","Method":"Failed","FullName":"Benchmarks.StringBenchmarks.Failed"},
              {"Namespace":"Benchmarks","Type":"HashBenchmarks","Method":"Sum","MethodTitle":"Sum","Parameters":"","FullName":"Benchmarks.HashBenchmarks.Sum",
               "Statistics":{"Mean":12.5,"Median":12,"StandardDeviation":0.1,"ConfidenceInterval":{"Margin":0.4}}}
            ]}
            """;
        var results = BenchmarkService.ParseReport(Report);
        Assert.Equal(2, results.Count); // the failed one has no statistics
        var concat = results[0];
        Assert.Equal("Concat", concat.Method);
        Assert.Equal("Count=16", concat.Parameters);
        Assert.Equal(261500, concat.Mean);
        Assert.Null(concat.Error);
        Assert.Equal(1000, concat.Allocated);
        Assert.Equal(2, concat.Gen0);
        var sum = results[1];
        Assert.Null(sum.Parameters);
        Assert.Equal(0.4, sum.Error);
        Assert.Null(sum.Allocated);

        var arguments = BenchmarkService.Arguments(new BenchmarkRequest("/src/Bench.csproj", ["A.B.C", "A.D.*"], "Dry", InProcess: true), "/tmp/artifacts");
        Assert.Equal(["run", "-c", "Release", "--project", "/src/Bench.csproj", "--", "--filter", "A.B.C", "A.D.*", "--exporters", "json", "--artifacts", "/tmp/artifacts", "--job", "Dry", "--inProcess"], arguments);
        Assert.DoesNotContain("--job", BenchmarkService.Arguments(new BenchmarkRequest("/p.csproj", ["*"], "Default"), "/a"));
        Assert.DoesNotContain("--no-build", arguments);
        Assert.Equal(["run", "-c", "Release", "--project", "/p.csproj", "--no-build", "--", "--filter", "*", "--exporters", "json", "--artifacts", "/a"],
            BenchmarkService.Arguments(new BenchmarkRequest("/p.csproj", ["*"], "Default"), "/a", noBuild: true));
        Assert.Equal("261.50 μs", BenchmarkService.FormatTime(261500).Replace(',', '.'));
        Assert.Equal("1000 B", BenchmarkService.FormatBytes(1000));
    }

    [Fact]
    public void Benchmarks_are_found_in_projects_that_reference_BenchmarkDotNet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Bench", "obj"));
            Directory.CreateDirectory(Path.Combine(folder, "App"));
            File.WriteAllText(Path.Combine(folder, "Bench", "Bench.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.8" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(folder, "Bench", "Strings.cs"), BenchmarkSource);
            File.WriteAllText(Path.Combine(folder, "Bench", "obj", "Generated.cs"), BenchmarkSource);
            File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
            File.WriteAllText(Path.Combine(folder, "App", "Program.cs"), BenchmarkSource);
            var service = new BenchmarkService(() => folder, Path.Combine(folder, "state"));
            var changed = 0;
            service.Changed += (_, _) => changed++;
            service.Refresh([Path.Combine(folder, "Bench", "Bench.csproj"), Path.Combine(folder, "App", "App.csproj")]);
            Assert.Equal(1, changed);
            Assert.Equal(3, service.All.Count); // the bench project's file only, not obj/ nor the app
            Assert.Equal([Path.Combine(folder, "Bench", "Bench.csproj")], service.Projects);
            Assert.Equal(Path.Combine(folder, "Bench", "Bench.csproj"), service.ProjectOf(Path.Combine(folder, "Bench", "Strings.cs")));
            Assert.Null(service.ProjectOf(Path.Combine(folder, "App", "Program.cs")));
            Assert.Equal(Path.Combine(folder, ".codalus", "benchmarks", "history.json"), service.HistoryPath);
            Assert.Empty(service.History().Runs);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_benchmark_run_waits_for_the_IDE_build_and_runs_nothing_when_it_fails()
    {
        // The IDE's build waits for the build host's restore (the one after opening the solution): dotnet run, which
        // would restore the project at the same time, starts only after it, with --no-build (Linux CI: obj/*.nuget.g.props
        // "already exists" when both restored).
        var folder = Path.Combine(Path.GetTempPath(), "codalus-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(folder, "Bench", "Bench.csproj");
            var building = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new BenchmarkService(() => folder, Path.Combine(folder, "state"), (path, _) =>
            {
                asked.TrySetResult(path);
                return building.Task;
            });
            var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
            service.Output += (_, line) => output.Enqueue(line);

            var run = service.RunAsync(new BenchmarkRequest(project, ["A.B.C"], "Dry", InProcess: true), CancellationToken.None);
            Assert.Equal(project, await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.True(service.IsRunning);
            Assert.DoesNotContain(output, l => l.StartsWith("> dotnet", StringComparison.Ordinal)); // waits for the build
            Assert.False(Directory.Exists(Path.Combine(service.Root, "runs")));

            building.SetResult(false);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Contains("Bench", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(output, l => l.StartsWith("> dotnet", StringComparison.Ordinal)); // nothing ran
            Assert.False(service.IsRunning);
            Assert.Empty(service.History().Runs);

            // Stopping while the build runs ends the run as cancelled, without running anything either.
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service = new BenchmarkService(() => folder, Path.Combine(folder, "state"), async (_, token) =>
            {
                await using (token.Register(() => stopped.TrySetResult(false)))
                {
                    return await stopped.Task;
                }
            });
            run = service.RunAsync(new BenchmarkRequest(project, ["A.B.C"]), CancellationToken.None);
            Assert.True(service.IsRunning);
            service.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(service.Root, "runs")));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Sessions_keep_their_snapshots_and_dump()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new ProfilerSession("s", DateTimeOffset.Now, 3, ProfilerSession.LaunchTarget, null, "Leaky", 42, [ProfilerTools.Memory, ProfilerTools.Allocations], null, null, ["heap-1.nettrace", "heap-2.nettrace"], "full.dmp") { Folder = folder };
            ProfilerSessionStore.Save(session);
            var loaded = ProfilerSessionStore.Load(folder)!;
            Assert.Equal([Path.Combine(folder, "heap-1.nettrace"), Path.Combine(folder, "heap-2.nettrace")], loaded.SnapshotPaths);
            Assert.Equal(Path.Combine(folder, "full.dmp"), loaded.DumpPath);
            Assert.Contains("Memory Usage, .NET Object Allocation", loaded.Description, StringComparison.Ordinal);

            // A session from before M18b (no snapshots, no dump) still reads.
            File.WriteAllText(Path.Combine(folder, ProfilerSession.FileName), """{"name":"old","startedAt":"2026-10-11T10:00:00+00:00","durationSeconds":3,"target":"launch","processName":"CpuBound","processId":1,"tools":["cpu"],"trace":"cpu.nettrace"}""");
            var old = ProfilerSessionStore.Load(folder)!;
            Assert.Empty(old.SnapshotPaths);
            Assert.Null(old.DumpPath);
            Assert.Equal(Path.Combine(folder, "cpu.nettrace"), old.TracePath);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_io_counters_of_a_process()
    {
        var counters = IoSampler.Read(Environment.ProcessId);
        Assert.NotNull(counters);
        Assert.True(counters.Value.ReadBytes >= 0 && counters.Value.WriteBytes >= 0);
        Assert.Equal(OperatingSystem.IsMacOS(), counters.Value.ReadOperations is null);
        Assert.Null(IoSampler.Read(int.MaxValue - 3));
        Assert.Equal(Path.Combine("x", "trace.io.json"), IoSampler.SidecarPath(Path.Combine("x", "trace.nettrace")));
        Assert.NotEmpty(IoSampler.SourceDescription);
    }

    /// <summary>
    /// The fallback when the client library's process list throws (a process ended while it read its
    /// /proc/&lt;pid&gt;/environ): the diagnostics ports themselves, sockets on Unix and pipes on Windows.
    /// </summary>
    [Fact]
    public void Diagnostics_ports_are_listed_by_their_process_ids()
    {
        var folder = Directory.CreateTempSubdirectory("codalus-ports-").FullName;
        try
        {
            foreach (var name in new[] { "dotnet-diagnostic-4321-1234567-socket", "dotnet-diagnostic-17", "dotnet-diagnostic-17-99-socket", "dotnet-diagnostic-x-1-socket", "other-5-socket" })
            {
                File.WriteAllText(Path.Combine(folder, name), string.Empty);
            }

            Assert.Equal([17, 4321], Codalus.Diagnostics.Host.Sessions.DiagnosticsPort.ListedPorts(folder));
            Assert.Empty(Codalus.Diagnostics.Host.Sessions.DiagnosticsPort.ListedPorts(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
