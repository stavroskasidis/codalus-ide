using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Codalus.Platform;

namespace Codalus.Debugging.Tests;

/// <summary>samples/Debugging, copied to a temp folder and built once for the test class.</summary>
public sealed class DebuggeeFixture : IAsyncLifetime
{
    // The real path: on macOS the temp folder is behind a symbolic link, and breakpoints bind by the path the PDB records.
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-debug-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "Debugging", "Debuggee");

    public string Dll => Path.Combine(Folder, "bin", "Debug", "net11.0", "Debuggee.dll");

    public string Program => Path.Combine(Folder, "Program.cs");

    public string Types => Path.Combine(Folder, "Types.cs");

    public static string RepoRoot =>
        typeof(DebuggeeFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    /// <summary>netcoredbg, or null (tests are skipped locally; CI fetches it with eng/get-netcoredbg.sh and fails without it).</summary>
    public static string? Adapter => DebugAdapterLocator.Resolve() ?? (Environment.GetEnvironmentVariable("CI") == "true" ? throw new InvalidOperationException("netcoredbg was not found (run eng/get-netcoredbg.sh).") : null);

    /// <summary>Codalus IDE's CoreCLR adapter (<c>codalus-debughost --clr-dap</c>, M54), the one built with the tests.</summary>
    public static DebugAdapterStartInfo CodalusAdapter() =>
        new(DotnetHost, [Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll"), CoreClrAdapter.AdapterArgument], AppContext.BaseDirectory);

    /// <summary>The <c>dotnet</c> that runs the tests, by its full path (a bare name is looked up by netcoredbg's process).</summary>
    public static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    /// <summary>
    /// Runs a freshly built program once without the debugger, and returns how long it took. The Visual Basic and F#
    /// programs are this suite's only net10.0 ones, among the first on a CI runner to start the .NET 10 runtime that
    /// setup installed minutes before. Under netcoredbg, which waits a fixed 5 seconds for the runtime of the program it
    /// started, those cold first starts on a loaded Windows runner ran out all three of the session's starts in 4 of
    /// the last 12 runs (in two of them the F# program, in the same test process, started fine), while of the ~95
    /// sessions of net11.0 programs, on the runtime the test processes themselves run on, one start timed out once.
    /// Started once before, the program's runtime is warm when the debugger starts it.
    /// </summary>
    public static async Task<TimeSpan> RunOnceAsync(string dll, string workingDirectory)
    {
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(new ProcessStartInfo(DotnetHost, ["exec", dll])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process);
            Assert.Fail($"{dll} didn't end within 2 minutes.");
        }

        Assert.True(process.ExitCode == 0, $"{dll} exited with {process.ExitCode}: {await output}{await error}");
        return watch.Elapsed;
    }

    /// <summary>netcoredbg on macOS reports every exit code as 0 (a known gap, ADR 0007); elsewhere it is exact.</summary>
    public static void AssertExitCode(int expected, int? actual)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>The 1-based line of the <c>// [name]</c> marker in <paramref name="file"/>.</summary>
    public static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1 is > 0 and var line
            ? line
            : throw new InvalidOperationException($"No [{marker}] marker in {file}.");

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(RepoRoot, "samples", "Debugging"), Path.Combine(_root, "Debugging"));
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        var start = new ProcessStartInfo("dotnet", ["build", Path.Combine(_root, "Debugging", "Debugging.slnx"), "-nologo", "-v", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output);
    }

    public ValueTask DisposeAsync()
    {
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

        return ValueTask.CompletedTask;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>Collects an adapter's events so tests can wait for them.</summary>
internal sealed class EventLog
{
    private readonly Channel<DapEvent> _events = Channel.CreateUnbounded<DapEvent>();
    private readonly List<DapEvent> _all = [];

    public EventLog(DebugAdapter adapter) => adapter.EventReceived += e =>
    {
        lock (_all)
        {
            _all.Add(e);
        }

        _events.Writer.TryWrite(e);
        return Task.CompletedTask;
    };

    public IReadOnlyList<DapEvent> All
    {
        get
        {
            lock (_all)
            {
                return [.. _all];
            }
        }
    }

    public string Output => string.Concat(All.Where(e => e.Name == DapEvents.Output).Select(e => DebugAdapter.Read(e.Body, DapJsonContext.Default.OutputEvent)?.Output));

    /// <summary>Waits for the next event named <paramref name="name"/> (skipping others).</summary>
    public async Task<DapEvent> NextAsync(string name, int seconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            DapEvent e;
            try
            {
                e = await _events.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No '{name}' event. Events so far: {string.Join(", ", All.Select(x => x.Name + " " + x.Body))}");
            }

            if (e.Name == name)
            {
                return e;
            }
        }
    }

    public async Task<StoppedEvent> StoppedAsync(int seconds = 60) =>
        DebugAdapter.Read((await NextAsync(DapEvents.Stopped, seconds)).Body, DapJsonContext.Default.StoppedEvent)!;
}
