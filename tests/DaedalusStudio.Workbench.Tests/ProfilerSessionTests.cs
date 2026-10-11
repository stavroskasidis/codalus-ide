using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Workbench.Diagnostics;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Profiling sessions on disk, counter exports and the hot-line store (M18a, ADR 0045).</summary>
public sealed class ProfilerSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-sessions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Sessions_are_saved_listed_newest_first_reopened_and_deleted()
    {
        var root = ProfilerSessionStore.Root(_root, Path.Combine(_root, "state"));
        Assert.Equal(Path.Combine(_root, ".daedalus", "profiler"), root);
        Assert.Equal(Path.Combine(_root, "state", "profiler"), ProfilerSessionStore.Root(null, Path.Combine(_root, "state")));
        Assert.Empty(ProfilerSessionStore.List(root));

        var older = DateTimeOffset.Now.AddMinutes(-5);
        var first = ProfilerSessionStore.CreateFolder(root, "My App: test", older);
        Assert.Equal("My-App--test", Path.GetFileName(first)[16..]);
        var second = ProfilerSessionStore.CreateFolder(root, "My App: test", older);
        Assert.EndsWith("-2", second, StringComparison.Ordinal);
        ProfilerSessionStore.Save(new ProfilerSession("old", older, 3, ProfilerSession.ProcessTarget, null, "App", 42, [ProfilerTools.Counters], null, "counters.json") { Folder = first });
        ProfilerSessionStore.Save(new ProfilerSession("new", DateTimeOffset.Now, 4.5, ProfilerSession.LaunchTarget, "/src/App/App.csproj", "App", 43, [ProfilerTools.Cpu, ProfilerTools.Counters], "cpu.nettrace", "counters.json") { Folder = second });

        var sessions = ProfilerSessionStore.List(root);
        Assert.Equal(["new", "old"], sessions.Select(s => s.Name));
        Assert.Equal(Path.Combine(second, "cpu.nettrace"), sessions[0].TracePath);
        Assert.Null(sessions[1].TracePath);
        Assert.Contains("CPU Usage, .NET Counters", sessions[0].Description, StringComparison.Ordinal);
        Assert.Equal("new", ProfilerSessionStore.Load(Path.Combine(second, ProfilerSession.FileName))!.Name);
        Assert.Null(ProfilerSessionStore.Load(Path.Combine(_root, "nothing")));

        ProfilerSessionStore.Delete(sessions[1]);
        Assert.False(Directory.Exists(first));
        Assert.Single(ProfilerSessionStore.List(root));
    }

    [Fact]
    public void Counter_exports_quote_csv_and_read_back_json()
    {
        var series = new CounterSeries(new CounterKey("My.Meter", "requests", "route=/a,b"), "requests", "{request}", CounterValueKinds.Rate);
        series.Add(new CounterPoint(1, 2.5), 10);
        series.Add(new CounterPoint(2, 3), 10);
        var csv = CounterExport.ToCsv([series]);
        Assert.Equal("seconds,provider,counter,tags,unit,kind,value\n1,My.Meter,requests,\"route=/a,b\",{request},rate,2.5\n2,My.Meter,requests,\"route=/a,b\",{request},rate,3\n", csv);

        var json = CounterExport.ToJson(new CounterFile(7, "App", DateTimeOffset.UnixEpoch, 1, [new CounterFileSeries("My.Meter", "requests", "requests", null, CounterValueKinds.Rate, null, [new CounterFilePoint(1, 2)])], [new RuntimeEvent(0.5, RuntimeEventKinds.GarbageCollection, "GC #1", 0, 1.5)]));
        var file = CounterExport.ReadJson(json)!;
        Assert.Equal(7, file.ProcessId);
        Assert.Equal(2, file.Counters[0].Values[0].Value);
        Assert.Equal(1.5, file.Events![0].DurationMilliseconds);
        Assert.Null(CounterExport.ReadJson("{ not json"));

        // Series keep their last points only.
        for (var i = 0; i < 20; i++)
        {
            series.Add(new CounterPoint(10 + i, i), 10);
        }

        Assert.Equal(10, series.Points.Count);
        Assert.Equal(19, series.Last!.Value.Value);
    }

    [Fact]
    public void Hot_lines_are_found_by_path_hidden_and_cleared_by_their_source()
    {
        var store = new HotLineStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Set("Report A", 100, new Dictionary<string, IReadOnlyList<CpuLine>> { ["/src/App/Program.cs"] = [new CpuLine(10, 80, 60)] });
        Assert.True(store.HasLines);
        Assert.Single(store.LinesOf("/src/App/Program.cs"));
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, store.LinesOf("/SRC/App/Program.cs").Count);
        Assert.Single(store.LinesOf("\\src\\App\\Program.cs"));
        store.IsVisible = false;
        Assert.Empty(store.LinesOf("/src/App/Program.cs"));
        store.IsVisible = true;
        store.Clear("Report B");
        Assert.True(store.HasLines);
        store.Clear("Report A");
        Assert.False(store.HasLines);
        Assert.Equal(4, changes); // not for the other source's Clear
    }
}
