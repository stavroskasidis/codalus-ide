using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Workbench.Tests;

/// <summary>samples/Diagnostics copied to a temp folder and built once (every fixture project) for the M18b tool tests.</summary>
public sealed class DiagnosticsSamplesFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-diagtools-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "Diagnostics");

    public string Dll(string project) => Path.Combine(Folder, project, "bin", "Debug", "net10.0", project + ".dll");

    public async ValueTask InitializeAsync()
    {
        var repo = DiagnosticsFixture.Metadata("CodalusRepoRoot");
        Copy(Path.Combine(repo, "samples", "Diagnostics"), Folder);
        File.Copy(Path.Combine(repo, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        File.Copy(Path.Combine(repo, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        foreach (var project in new[] { "CpuBound", "Allocations", "Leaky", "AsyncHeavy", "Activities" })
        {
            var start = new ProcessStartInfo("dotnet", ["build", Path.Combine(Folder, project, project + ".csproj"), "-nologo", "-v", "quiet"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            Assert.True(process.ExitCode == 0, project + ": " + await output);
        }
    }

    /// <summary>Starts a fixture program for <paramref name="seconds"/> (0: until killed); held before Main when <paramref name="suspended"/>.</summary>
    public async Task<Process> StartAsync(string project, double seconds, bool suspended = false)
    {
        var start = new ProcessStartInfo("dotnet", [Dll(project), seconds.ToString(CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (suspended)
        {
            start.Environment[ProfilingLaunch.SuspendVariable] = "1";
        }

        var process = Process.Start(start)!;
        if (!suspended)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.StartsWith(project + " ", line, StringComparison.Ordinal);
        }

        return process;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "BenchmarkDotNet.Artifacts")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>
/// The M18b tools in the diagnostics host (ADR 0046) through <see cref="DiagnosticsService"/>, with real EventPipe
/// sessions on samples/Diagnostics: allocations, .NET Async, threads and contention, exceptions, activities, file I/O,
/// the events viewer and its exports, heap snapshots with their diffs and paths to roots, and comparing traces.
/// </summary>
public sealed class DiagnosticsToolsTests(DiagnosticsSamplesFixture fixture) : IClassFixture<DiagnosticsSamplesFixture>, IAsyncLifetime
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "codalus-diagtoolswork-" + Guid.NewGuid().ToString("N"));
    private DiagnosticsService _diagnostics = null!;

    private static CancellationToken Timeout(int seconds = 180) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(DiagnosticsFixture.HostPath), $"Diagnostics host not built: {DiagnosticsFixture.HostPath}");
        Directory.CreateDirectory(_work);
        _diagnostics = new DiagnosticsService(() => ProcessLaunchInfo.FromPath(DiagnosticsFixture.HostPath));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _diagnostics.DisposeAsync();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    /// <summary>Traces a fixture from its first instruction to its end with <paramref name="tools"/> and opens the trace.</summary>
    private async Task<CpuTrace> TraceFromLaunchAsync(string project, double seconds, params string[] tools)
    {
        var process = await fixture.StartAsync(project, seconds, suspended: true);
        try
        {
            var path = Path.Combine(_work, project + "-" + Guid.NewGuid().ToString("N")[..6] + ".nettrace");
            var recording = await _diagnostics.StartTraceAsync(process.Id, path, tools, null, Timeout());
            await _diagnostics.ResumeAsync(process.Id, Timeout());
            await process.WaitForExitAsync(Timeout());
            var stopped = await recording.StopAsync(Timeout());
            Assert.True(stopped.Bytes > 10_000, $"{stopped.Bytes} bytes");
            return await _diagnostics.OpenTraceAsync(stopped.Path, Timeout());
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Allocations_show_the_allocated_types_and_their_stacks()
    {
        await using var trace = await TraceFromLaunchAsync("Allocations", 3, TraceTools.Allocations);
        var allocations = Assert.Single(trace.Summary.Profiles!, p => p.Kind == ProfileKinds.Allocations);
        Assert.Equal("KB", allocations.Unit);
        Assert.True(allocations.Events > 50, $"{allocations.Events} allocation ticks");
        Assert.DoesNotContain(trace.Summary.Profiles!, p => p.Kind == ProfileKinds.Cpu); // CPU wasn't asked for

        // Bottom-up: the types (the leaf frames) by KB, then the stacks that allocate them.
        var filter = new CpuFilter(Profile: ProfileKinds.Allocations);
        var types = await trace.BottomUpAsync(filter, [], 0, 20, Timeout());
        Assert.Contains(types.Nodes, n => n.Name is "System.String" or "System.Char[]");
        var bytes = types.Nodes.First(n => n.Name == "System.Byte[]");
        Assert.Equal("[type]", bytes.Module);
        var allocators = await trace.BottomUpAsync(filter, [bytes.Function], 0, 10, Timeout());
        Assert.Contains(allocators.Nodes, n => n.Name.Contains("<Main>$", StringComparison.Ordinal));

        // The other views take the same profile: top-down, flame graph and the timeline.
        var flame = await trace.FlameAsync(filter, -1, 0, 1000, Timeout());
        Assert.Equal(types.Total, flame.Total);
        Assert.Contains(flame.Frames, f => f.Name == "System.Byte[]");
        var timeline = await trace.TimelineAsync(filter, 10, Timeout());
        Assert.Equal(types.Total, timeline.Samples.Sum());
    }

    [Fact]
    public async Task Async_threads_contention_and_exceptions_of_an_async_program()
    {
        await using var trace = await TraceFromLaunchAsync("AsyncHeavy", 3, TraceTools.Cpu, TraceTools.Async, TraceTools.Threads, TraceTools.Exceptions);
        Assert.True(trace.Summary.Tasks > 100, $"{trace.Summary.Tasks} tasks");
        var all = new CpuFilter();

        // .NET Async: the longest tasks first, async methods named by their state machines' frames.
        var tasks = await trace.AsyncTasksAsync(all, null, 0, 200, Timeout());
        Assert.True(tasks.Total > 100);
        Assert.True(tasks.Tasks.Zip(tasks.Tasks.Skip(1)).All(p => p.First.Duration >= p.Second.Duration), "longest first");
        var chain = await trace.AsyncTasksAsync(all, "SlowChainAsync", 0, 50, Timeout());
        Assert.NotEmpty(chain.Tasks);
        Assert.Contains(chain.Tasks, t => t.Method == "Worker.SlowChainAsync" && t.Duration >= 40 && t.Status == "Completed");
        Assert.NotEmpty(chain.Tasks[0].Stack);
        var fetch = await trace.AsyncTasksAsync(all, "FetchAsync", 0, 5, Timeout());
        Assert.Equal("Worker.FetchAsync", fetch.Tasks[0].Method);

        // Threads: states over time, contention, and the thread pool's injections.
        var threads = await trace.ThreadsAsync(100, Timeout());
        Assert.All(threads.Threads, t => Assert.Equal(100, t.States.Length));
        Assert.Contains(threads.Threads, t => t.Running > 0);
        Assert.Contains(threads.Threads, t => t.Contention > 0);
        Assert.Contains(threads.Threads, t => t.States.Contains('W'));
        // A bucket shows C when a third of it is spent blocked: only a thread blocked for a third of the whole trace is
        // sure to have one (more contention spread thinly, as on a busy Windows runner, may never fill a third of a bucket).
        if (threads.Threads.Max(t => t.Contention) >= threads.BucketMilliseconds * 100 / 3)
        {
            Assert.Contains(threads.Threads, t => t.States.Contains('C'));
        }
        Assert.Contains(threads.Pool, p => p.Kind == "adjustment" && p.Reason is "Starvation" or "CooperativeBlocking" or "ThreadTimedOut" or "Warmup" or "Initializing" or "ClimbingMove");
        Assert.Contains(threads.Pool, p => p.Kind == "start");

        // Lock contention as a profile: the contending stacks under the "Lock contention" frame.
        var contention = new CpuFilter(Profile: ProfileKinds.Contention);
        var locks = await trace.BottomUpAsync(contention, [], 0, 5, Timeout());
        var lockFrame = Assert.Single(locks.Nodes);
        Assert.Equal("Lock contention", lockFrame.Name);
        var contenders = await trace.FlameAsync(contention, -1, 0, 2000, Timeout());
        Assert.Equal(locks.Total, contenders.Total);
        Assert.Contains(contenders.Frames, f => f.Name.Contains("FetchAsync", StringComparison.Ordinal));

        // Exceptions: grouped by type and throw site, with the line once symbols are read; over time.
        var exceptions = await trace.ExceptionsAsync(all, Timeout());
        var group = exceptions.Groups[0];
        Assert.Equal("System.InvalidOperationException", group.Type);
        Assert.True(group.ThrowSite.Contains("Worker.Validate", StringComparison.Ordinal), group.ThrowSite);
        Assert.True(group.Count >= 8, $"{group.Count} exceptions");
        Assert.StartsWith("Item ", group.Message, StringComparison.Ordinal);
        Assert.Equal("Program.cs", Path.GetFileName(group.File));
        var program = Path.Combine(fixture.Folder, "AsyncHeavy", "Program.cs");
        var throwLine = Array.FindIndex(File.ReadAllLines(program), l => l.Contains("// [throw]", StringComparison.Ordinal)) + 1;
        Assert.InRange(group.Line ?? 0, throwLine - 2, throwLine + 2);
        Assert.Equal(exceptions.Total, exceptions.Recent.Count);
        var exceptionTimeline = await trace.TimelineAsync(new CpuFilter(Profile: ProfileKinds.Exceptions), 10, Timeout());
        Assert.Equal(group.Count, exceptionTimeline.Samples.Sum());
    }

    [Fact]
    public async Task Activities_file_io_the_events_viewer_and_exports()
    {
        await using var trace = await TraceFromLaunchAsync("Activities", 3, TraceTools.Cpu, TraceTools.Activities, TraceTools.FileIo);
        var all = new CpuFilter();

        // HTTP requests from System.Net.Http's events: the slow one takes its 30 ms.
        var http = await trace.ActivitiesAsync(ActivityKinds.Http, all, byTime: false, 0, 100, Timeout());
        Assert.True(http.Total >= 4, $"{http.Total} requests");
        var slow = http.Items.First(i => i.Name.Contains("/slow", StringComparison.Ordinal));
        Assert.True(slow.Duration >= 25, $"{slow.Duration} ms");
        Assert.Equal("200", slow.Status);
        Assert.StartsWith("http://127.0.0.1:", slow.Name, StringComparison.Ordinal);

        // Database commands from EF Core's DiagnosticSource events.
        var commands = await trace.ActivitiesAsync(ActivityKinds.Database, all, byTime: true, 0, 200, Timeout());
        Assert.Contains(commands.Items, c => c.Name.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && c.Source == "Microsoft.EntityFrameworkCore");
        Assert.True(commands.Items.Zip(commands.Items.Skip(1)).All(p => p.First.Start <= p.Second.Start), "by time");

        // Spans: the fixture's round with its children one level down.
        var spans = await trace.ActivitiesAsync(ActivityKinds.Span, all, byTime: true, 0, 2000, Timeout());
        var round = spans.Items.First(s => s.Name == "round" && s.Source == "Codalus.Samples.Activities");
        Assert.Equal(0, round.Depth);
        Assert.Contains(spans.Items, s => s.Name == "fetch" && s.Parent == round.Id && s.Depth == 1);
        Assert.Contains(spans.Items, s => s.Name == "query" && s.Depth == 1);
        Assert.Contains("round:", round.Detail, StringComparison.Ordinal);

        // File I/O: the counters the OS gives (the fixture writes and reads 1 MB a round).
        var io = await trace.FileIoAsync(Timeout());
        Assert.True(io.Available);
        Assert.NotEmpty(io.Samples);
        if (!OperatingSystem.IsMacOS())
        {
            // macOS counts bytes when they reach the disk, which the page cache may delay past the trace.
            Assert.True(io.WriteBytes >= 1024 * 1024, $"{io.WriteBytes} bytes written ({io.Source})");
            Assert.True(io.ReadBytes >= 1024 * 1024, $"{io.ReadBytes} bytes read");
            Assert.True(io.WriteOperations > 0);
        }

        // The events viewer: counts by provider and event, a page filtered by provider, by name and by text.
        var stats = await trace.EventStatsAsync(Timeout());
        Assert.Equal(trace.Summary.Events, stats.Total);
        Assert.Contains(stats.Events, e => e is { Provider: "System.Net.Http", Name: "Request/Start" } && e.Count >= 4);
        var starts = await trace.EventsAsync(new EventFilter(Providers: ["System.Net.Http"], Names: ["Request/Start"]), 0, 3, Timeout());
        Assert.True(starts.Total >= 4);
        Assert.Equal(3, starts.Events.Count);
        Assert.Equal(starts.Total - 3, starts.More);
        Assert.All(starts.Events, e => Assert.Contains("pathAndQuery=", e.Payload, StringComparison.Ordinal));
        var text = await trace.EventsAsync(new EventFilter(Providers: ["System.Net.Http"], Text: "/slow"), 0, 50, Timeout());
        Assert.NotEmpty(text.Events);
        Assert.All(text.Events, e => Assert.Contains("/slow", e.Payload, StringComparison.Ordinal));
        var window = await trace.EventsAsync(new EventFilter(StartMilliseconds: 1000, EndMilliseconds: 1500), 0, 100, Timeout());
        Assert.All(window.Events, e => Assert.InRange(e.At, 1000, 1500));

        // Exports: Speedscope and Chromium trace JSON.
        var speedscope = await trace.ExportAsync(all, ExportFormats.Speedscope, Path.Combine(_work, "cpu.speedscope.json"), Timeout());
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(speedscope.Path, TestContext.Current.CancellationToken)))
        {
            Assert.Equal("https://www.speedscope.app/file-format-schema.json", document.RootElement.GetProperty("$schema").GetString());
            Assert.True(document.RootElement.GetProperty("shared").GetProperty("frames").GetArrayLength() > 10);
            var profile = document.RootElement.GetProperty("profiles")[0];
            Assert.Equal("sampled", profile.GetProperty("type").GetString());
            Assert.Equal(profile.GetProperty("samples").GetArrayLength(), profile.GetProperty("weights").GetArrayLength());
        }

        var chromium = await trace.ExportAsync(all, ExportFormats.Chromium, Path.Combine(_work, "cpu.trace.json"), Timeout());
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(chromium.Path, TestContext.Current.CancellationToken)))
        {
            var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToList();
            static string? Category(JsonElement e) => e.TryGetProperty("cat", out var cat) ? cat.GetString() : null;
            Assert.Contains(events, e => e.GetProperty("ph").GetString() == "X" && Category(e) == "cpu" && e.GetProperty("dur").GetDouble() >= 0);
            Assert.Contains(events, e => e.GetProperty("ph").GetString() == "M");
            Assert.Contains(events, e => Category(e) == "Spans" && e.GetProperty("name").GetString() == "round");
        }
    }

    [Fact]
    public async Task Heap_snapshots_their_diff_and_paths_to_roots()
    {
        var process = await fixture.StartAsync("Leaky", 0);
        HeapSnapshot? first = null;
        HeapSnapshot? second = null;
        try
        {
            var taken = await _diagnostics.TakeHeapSnapshotAsync(process.Id, Path.Combine(_work, "heap-1.nettrace"), Timeout());
            Assert.True(taken.Bytes > 10_000);
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            var clock = Stopwatch.StartNew();
            var later = await _diagnostics.TakeHeapSnapshotAsync(process.Id, Path.Combine(_work, "heap-2.nettrace"), Timeout());
            TestContext.Current.TestOutputHelper?.WriteLine($"Heap snapshot: {later.Bytes:N0} bytes in {clock.ElapsedMilliseconds} ms");
            first = await _diagnostics.OpenHeapSnapshotAsync(taken.Path, Timeout());
            second = await _diagnostics.OpenHeapSnapshotAsync(later.Path, Timeout());
            Assert.True(first.Summary.Objects > 1000 && first.Summary.Roots > 0, $"{first.Summary.Objects} objects, {first.Summary.Roots} roots");
            Assert.True(second.Summary.Objects > first.Summary.Objects);

            // Types by size; Order among them.
            var types = await second.TypesAsync(null, null, 0, 50, Timeout());
            Assert.True(types.Types.Zip(types.Types.Skip(1)).All(p => p.First.Bytes >= p.Second.Bytes), "largest first");
            var order = (await second.TypesAsync(null, "Order", 0, 10, Timeout())).Types.Single(t => t.Name == "Order");
            Assert.True(order.Count > 1000, $"{order.Count} orders");

            // The diff: Order grows (the leak), with its strings.
            var diff = await second.TypesAsync(first, null, 0, 20, Timeout());
            var grown = diff.Types.First(t => t.Name == "Order");
            Assert.True(grown.CountDelta > 1000, $"{grown.CountDelta} more orders");
            Assert.True(grown.BytesDelta > 0);
            Assert.True(diff.Types.Zip(diff.Types.Skip(1)).All(p => p.First.BytesDelta >= p.Second.BytesDelta), "the most growth first");

            // Paths to roots: up from Order through its referrers to a root.
            IReadOnlyList<int> path = [order.Id];
            var reachedRoot = false;
            for (var depth = 0; depth < 12 && !reachedRoot; depth++)
            {
                var referrers = await second.ReferrersAsync(path, 0, 10, Timeout());
                Assert.NotEmpty(referrers.Referrers);
                var top = referrers.Referrers[0];
                Assert.True(top.Count > 0 && top.Bytes > 0);
                reachedRoot = top.Id < 0 && top.Id != int.MinValue;
                path = [.. path, top.Id];
                if (!reachedRoot && !top.HasChildren)
                {
                    break;
                }
            }

            Assert.True(reachedRoot, "a path from Order reaches a root: " + string.Join(" ← ", path));
        }
        finally
        {
            Kill(process);
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Two_traces_compare_by_function()
    {
        await using var cpu = await TraceFromLaunchAsync("CpuBound", 2, TraceTools.Cpu);
        await using var async = await TraceFromLaunchAsync("AsyncHeavy", 2, TraceTools.Cpu);
        var result = await async.CompareAsync(cpu, new CpuFilter(), 500, Timeout());
        // AsyncHeavy mostly waits, and only running managed code is sampled: on a loaded runner its 2 s can hold no
        // samples at all (the Windows runner), which still compares.
        Assert.True(result.BaselineTotal > 0, $"the CpuBound baseline has samples ({result.BaselineTotal}, AsyncHeavy {result.Total})");
        var isPrime = result.Functions.First(f => f.Name.Contains("Primes.IsPrime", StringComparison.Ordinal));
        // The baseline's hottest among CpuBound's own functions (a loaded runner can rank runtime frames above it).
        var own = result.Functions.Where(f => f.Module == "CpuBound").ToList();
        Assert.True(isPrime.BaselineSelf > 0 && own.All(f => f.BaselineSelf <= isPrime.BaselineSelf),
            string.Join(", ", own.OrderByDescending(f => f.BaselineSelf).Take(5).Select(f => $"{f.Name} {f.BaselineSelf:P0}")));
        Assert.Equal(0, isPrime.Self);
        Assert.True(isPrime.Delta < 0);
        Assert.True(result.Functions.Zip(result.Functions.Skip(1)).All(p => Math.Abs(p.First.Delta) >= Math.Abs(p.Second.Delta)), "the largest changes first");
    }

    [Fact]
    public async Task Extra_providers_land_in_the_events_viewer()
    {
        var process = await fixture.StartAsync("CpuBound", 0);
        try
        {
            var path = Path.Combine(_work, "custom.nettrace");
            var recording = await _diagnostics.StartTraceAsync(process.Id, path, [TraceTools.Cpu], [new TraceProvider("Codalus-Samples-CpuBound", -1, 5, new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" })], Timeout()); // a whole number: the program parses it in its own culture
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            var stopped = await recording.StopAsync(Timeout());
            await using var trace = await _diagnostics.OpenTraceAsync(stopped.Path, Timeout());
            var stats = await trace.EventStatsAsync(Timeout());
            Assert.Contains(stats.Events, e => e.Provider == "Codalus-Samples-CpuBound");
            var events = await trace.EventsAsync(new EventFilter(Providers: ["Codalus-Samples-CpuBound"]), 0, 20, Timeout());
            Assert.NotEmpty(events.Events);
        }
        finally
        {
            Kill(process);
        }
    }
}
