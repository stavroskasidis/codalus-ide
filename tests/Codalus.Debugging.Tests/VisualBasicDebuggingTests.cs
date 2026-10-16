using System.Diagnostics;
using System.Threading.Channels;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M35 (ADR 0066): a Visual Basic program on the real netcoredbg, through its portable PDB. Breakpoints in a
/// <c>.vb</c> file bind, locals show, stepping goes into the C# library, and expressions are evaluated as C# (the
/// documented gap).
/// </summary>
public sealed class VisualBasicDebuggingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-vbdebug-" + Guid.NewGuid().ToString("N"));
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private DebugService _debug = null!;

    private string Console => Path.Combine(_root, "VisualBasic", "VbConsole");

    private string Program => Path.Combine(Console, "Program.vb");

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var samples = Path.Combine(DebuggeeFixture.RepoRoot, "samples");
        CopyTree(Path.Combine(samples, "VisualBasic"), Path.Combine(_root, "VisualBasic"));
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        var start = new ProcessStartInfo("dotnet", ["build", Path.Combine(Console, "VbConsole.vbproj"), "-nologo", "-v", "quiet"])
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
    public async Task A_vb_program_stops_at_breakpoints_shows_locals_and_steps_into_csharp()
    {
        var ct = TestContext.Current.CancellationToken;
        var line = Array.FindIndex(File.ReadAllLines(Program), l => l.Contains("' [vb-loop]", StringComparison.Ordinal)) + 1;
        _debug.Breakpoints.Set(new BreakpointSpec(Program, line));
        var dll = Path.Combine(Console, "bin", "Debug", "net10.0", "VbConsole.dll");
        await _debug.LaunchAsync("dotnet", ["exec", dll], Console, new Dictionary<string, string>(), ct);

        await NextStopAsync();
        Assert.Equal(line, _debug.SelectedFrame?.Line);
        Assert.Equal(Program, _debug.SelectedFrame?.Source?.Path, ignoreCase: OperatingSystem.IsWindows());
        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        Assert.Contains(locals, v => v.Name == "inventory");
        var product = Assert.Single(locals, v => v.Name == "product");
        Assert.Contains(await _debug.VariablesAsync(product.VariablesReference), v => v is { Name: "Name", Value: "\"Tea\"" });

        // Expressions are C#: a member path works, and C#'s operators do.
        Assert.Equal("\"Tea\"", (await _debug.EvaluateAsync("product.Name", "watch", ct)).Result);
        Assert.Equal("true", (await _debug.EvaluateAsync("product.Price == 3", "watch", ct)).Result);

        // Into the C# library's Format.
        await _debug.StepIntoAsync();
        await NextStopAsync();
        Assert.Contains("Format", _debug.SelectedFrame!.Name, StringComparison.Ordinal);
        Assert.EndsWith("PriceFormatter.cs", _debug.SelectedFrame.Source?.Path, StringComparison.Ordinal);
        await _debug.StopAsync();
    }

    private async Task NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _stops.Reader.ReadAsync(timeout.Token);
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
