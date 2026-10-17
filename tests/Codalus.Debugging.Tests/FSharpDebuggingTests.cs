using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M36b (ADR 0068): an F# program on the real netcoredbg, through its portable PDB. A breakpoint in an F# library's
/// <c>.fs</c> file binds, the function's parameter shows with its F# list, the call stack names the F# console, and
/// expressions are evaluated as C# (the documented gap).
/// </summary>
public sealed class FSharpDebuggingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-fsdebug-" + Guid.NewGuid().ToString("N"));
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly System.Text.StringBuilder _output = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DebugService _debug = null!;

    private string Console => Path.Combine(_root, "FSharp", "FsConsole");

    private string Geometry => Path.Combine(_root, "FSharp", "FsLibrary", "Geometry.fs");

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var samples = Path.Combine(DebuggeeFixture.RepoRoot, "samples");
        CopyTree(Path.Combine(samples, "FSharp"), Path.Combine(_root, "FSharp"));
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        var start = new ProcessStartInfo("dotnet", ["build", Path.Combine(Console, "FsConsole.fsproj"), "-nologo", "-v", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output);

        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Output += (_, o) =>
        {
            lock (_output)
            {
                _output.Append(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds:0.0}s] {o.Text}");
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task An_fsharp_program_stops_in_an_fsharp_library_and_shows_its_parameter()
    {
        var ct = TestContext.Current.CancellationToken;
        var line = Array.FindIndex(File.ReadAllLines(Geometry), l => l.StartsWith("let totalArea", StringComparison.Ordinal)) + 1;
        _debug.Breakpoints.Set(new BreakpointSpec(Geometry, line));
        var dll = Path.Combine(Console, "bin", "Debug", "net10.0", "FsConsole.dll");
        lock (_output)
        {
            _output.Append(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds:0.0}s] (the test launches the program)\n");
        }

        try
        {
            await _debug.LaunchAsync("dotnet", ["exec", dll], Console, new Dictionary<string, string>(), ct);
        }
        catch (InvalidOperationException ex)
        {
            // netcoredbg gives the runtime 5 seconds per start (DebugService.StartupAttempts): the Debug Console says
            // when each start began and what the debugger wrote.
            Assert.Fail($"{ex.Message}\nDebug Console:\n{Output()}");
        }

        await NextStopAsync();
        Assert.Equal(line, _debug.SelectedFrame?.Line);
        Assert.Equal(Geometry, _debug.SelectedFrame?.Source?.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Contains("totalArea", _debug.SelectedFrame!.Name, StringComparison.Ordinal);
        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        Assert.Contains(locals, v => v.Name == "shapes");

        // Expressions are C#: a member path into the F# list and its union case. (Members F# adds by augmentation,
        // such as a list's Length, aren't found.)
        Assert.Equal("{FsLibrary.Shapes.Shape.Circle}", (await _debug.EvaluateAsync("shapes.Head", "watch", ct)).Result);
        Assert.Equal("3", (await _debug.EvaluateAsync("1 + 2", "watch", ct)).Result);

        // The caller is the console's top-level code, in Program.fs.
        Assert.Contains(_debug.Frames, frame => frame.Source?.Path?.EndsWith("Program.fs", StringComparison.Ordinal) == true);
        await _debug.StopAsync();
    }

    private async Task NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"No stop within 60 seconds ({_debug.State}).\nDebug Console:\n{Output()}");
        }
    }

    private string Output()
    {
        lock (_output)
        {
            return _output.ToString();
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is not ("bin" or "obj"))
            {
                CopyTree(directory, Path.Combine(target, name));
            }
        }
    }
}
