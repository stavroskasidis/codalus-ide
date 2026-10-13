using System.Diagnostics;
using Codalus.Debugging.Host;

namespace Codalus.Debugging.Tests;

[DebuggerDisplay("{Name}, {Age} years", Name = "Person {Name}")]
public sealed class DisplayedPerson
{
    public string Name { get; init; } = "Ada";

    public int Age { get; init; } = 36;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Secret { get; init; } = "hidden";

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public int[] Scores { get; init; } = [1, 2];

#pragma warning disable CS0414, CA1823, IDE0051, IDE0052 // read by the debugger attributes test through metadata only
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _cache = 1;
#pragma warning restore CS0414, CA1823, IDE0051, IDE0052
}

/// <summary>Inherits its base type's attributes.</summary>
public sealed class Employee : PersonBase;

[DebuggerDisplay("Base {Id}")]
[DebuggerTypeProxy(typeof(PersonBaseProxy))]
public class PersonBase
{
    public int Id { get; init; }
}

public sealed class PersonBaseProxy(PersonBase person)
{
    public int Id => person.Id;
}

public sealed class Outer
{
    [DebuggerDisplay("Inner!")]
    public sealed class Inner;
}

[DebuggerDisplay("Count = {Count}")]
public sealed class Bag<T>
{
    public int Count => 0;
}

/// <summary>The debug companion host (M14b): debugger attributes from metadata, and dumps through ClrMD.</summary>
public sealed class CompanionTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    private static readonly string[] s_modules = [typeof(CompanionTests).Assembly.Location];

    [Fact]
    public void DebuggerDisplay_and_DebuggerBrowsable_are_read_from_the_assembly()
    {
        using var attributes = new TypeAttributes();

        var person = attributes.Find("Codalus.Debugging.Tests.DisplayedPerson", s_modules);

        Assert.Equal("{Name}, {Age} years", person.Display);
        Assert.Equal("Person {Name}", person.Name);
        Assert.Contains("_cache", person.Hidden);
        Assert.Contains("Secret", person.Hidden);
        // An auto-property's backing field is hidden when the compiler marked it Never, which it does in Debug builds only.
        var backing = typeof(DisplayedPerson).GetField("<Age>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var marked = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DebuggerBrowsableAttribute>(backing)?.State == DebuggerBrowsableState.Never;
        Assert.Equal(marked, person.Hidden.Contains("<Age>k__BackingField"));
        Assert.Equal(["Scores"], person.RootHidden);
        Assert.Equal(typeof(CompanionTests).Assembly.Location, person.Module);
    }

    [Theory]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so", "10.0.12")]
    [InlineData("/usr/local/share/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128/libcoreclr.dylib", "11.0.0-rc.1.26425.128")]
    [InlineData("C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.12\\coreclr.dll", "10.0.12")]
    [InlineData("/opt/app/libcoreclr.so", null)]
    [InlineData(null, null)]
    public void A_runtime_without_a_version_resource_is_versioned_by_its_folder(string? path, string? expected) =>
        Assert.Equal(expected, Dumps.FolderVersion(path));

    [Fact]
    public void Base_types_nested_types_and_generic_types_are_found()
    {
        using var attributes = new TypeAttributes();

        var employee = attributes.Find("Codalus.Debugging.Tests.Employee", s_modules);
        Assert.Equal("Base {Id}", employee.Display);
        Assert.Equal("Codalus.Debugging.Tests.PersonBaseProxy", employee.TypeProxy);

        Assert.Equal("Inner!", attributes.Find("Codalus.Debugging.Tests.Outer.Inner", s_modules).Display);
        Assert.Equal("Count = {Count}", attributes.Find("Codalus.Debugging.Tests.Bag<System.Collections.Generic.List<int>>", s_modules).Display);
        Assert.Null(attributes.Find("Codalus.Debugging.Tests.CompanionTests", s_modules).Display);
        Assert.Null(attributes.Find("No.Such.Type", s_modules).Display);
        Assert.Null(attributes.Find("int[]", s_modules).Display);
    }

    [Theory]
    [InlineData("A.B", "A.B")]
    [InlineData("A.List<int>", "A.List`1")]
    [InlineData("A.Map<string, A.List<int>>", "A.Map`2")]
    [InlineData("A.Outer+Inner", "A.Outer.Inner")]
    public void Debugger_type_names_map_to_metadata_names(string typeName, string first)
    {
        Assert.Equal(first, TypeAttributes.MetadataNames(typeName).First());
    }

    [Fact]
    public async Task A_dump_of_a_program_paused_at_a_breakpoint_maps_the_paused_frame_to_its_line()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var dump = Path.Combine(Path.GetTempPath(), "codalus-test-" + Guid.NewGuid().ToString("N") + ".dmp");
        await using var debug = new Codalus.Workbench.Debugging.DebugService(() => DebuggeeFixture.Adapter);
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var start = DebuggeeFixture.Line(depth, "depth-start");
        debug.Breakpoints.Set(new Codalus.Workbench.Debugging.BreakpointSpec(depth, start));
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        await debug.LaunchAsync(host, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        for (var i = 0; i < 2400 && debug.State != Codalus.Workbench.Debugging.DebugState.Stopped; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(Codalus.Workbench.Debugging.DebugState.Stopped, debug.State);
        var dumps = new Dumps();
        try
        {
            await Dumps.CreateAsync(debug.ProcessId!.Value, dump, "full", Dumps.DumpTimeout, TestContext.Current.CancellationToken);
            var summary = dumps.Open(dump);
            var main = summary.Threads.Single(t => t.Frames.Any(f => f.Method.Contains("Depth.Run", StringComparison.Ordinal)));
            var run = main.Frames.First(f => f.Method.Contains("Depth.Run", StringComparison.Ordinal));
            Assert.True(
                (Path.GetFileName(run.File), run.Line) == ("Depth.cs", start),
                $"{run}; all frames: {string.Join(" | ", main.Frames)}");

            // Its caller is mapped from the return address, to the line of the call.
            var caller = main.Frames[main.Frames.ToList().IndexOf(run) + 1];
            Assert.Equal(("Program.cs", DebuggeeFixture.Line(debuggee.Program, "depth-call")), (Path.GetFileName(caller.File), caller.Line));
        }
        finally
        {
            dumps.Dispose();
            await debug.StopAsync();
            File.Delete(dump);
        }
    }

    [Fact]
    public async Task A_dump_of_a_running_program_shows_its_threads_stacks_heap_and_objects()
    {
        var wait = Path.Combine(Path.GetTempPath(), "codalus-dump-wait-" + Guid.NewGuid().ToString("N"));
        var dump = Path.Combine(Path.GetTempPath(), "codalus-test-" + Guid.NewGuid().ToString("N") + ".dmp");
        using var process = Process.Start(new ProcessStartInfo("dotnet", [debuggee.Dll, "--wait-for", wait]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var dumps = new Dumps();
        try
        {
            await Task.Delay(1500, TestContext.Current.CancellationToken); // in the wait loop by now

            var created = await Dumps.CreateAsync(process.Id, dump, "full", Dumps.DumpTimeout, TestContext.Current.CancellationToken); // full: the JIT data that maps code to lines
            Assert.True(created.Size > 0);
            var summary = dumps.Open(dump);

            // macOS core dumps don't record the process id.
            Assert.Equal(OperatingSystem.IsMacOS() ? null : process.Id, summary.ProcessId);
            Assert.StartsWith(".NET 10.", summary.Runtime, StringComparison.Ordinal);
            var main = Assert.Single(summary.Threads, t => t.Frames.Any(f => f.Method.Contains("<Main>$", StringComparison.Ordinal)));
            var frame = main.Frames.First(f => f.Method.Contains("<Main>$", StringComparison.Ordinal));
            Assert.True(
                (Path.GetFileName(frame.File), frame.Line) == ("Program.cs", DebuggeeFixture.Line(debuggee.Program, "wait")),
                $"{frame}; all frames: {string.Join(" | ", main.Frames)}");

            var heap = dumps.Heap(summary.Id, 50);
            Assert.True(heap.TotalObjects > 100);
            var strings = Assert.Single(heap.Types, t => t.Type == "System.String");
            var address = dumps.Instances(summary.Id, strings.MethodTable, 5)[0];
            var text = dumps.Object(summary.Id, address);
            Assert.Equal("System.String", text.Type);
            Assert.NotNull(text.Value);

            Assert.True(dumps.Close(summary.Id));
        }
        finally
        {
            dumps.Dispose(); // closes the dump file
            process.Kill(entireProcessTree: true);
            File.Delete(dump);
        }
    }
}
