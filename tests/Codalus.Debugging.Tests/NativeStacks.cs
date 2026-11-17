using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Where a stalled session waits, for a failure message: <see cref="Codalus.Workbench.Debugging.DebugService.Describe"/>
/// and, on macOS, a native sample of the program and of its parent (the debug adapter that started it). On Linux
/// <c>Describe</c> already names each thread's kernel wait (<c>/proc/*/wchan</c>); macOS has no such file, so two
/// netcoredbg sessions that stalled after the runtime's first module (macOS CI, run 41620973752) said nothing of where
/// the program's runtime or netcoredbg waited.
/// </summary>
internal static class NativeStacks
{
    /// <summary>How much of each sample a failure message keeps.</summary>
    private const int SampleLimit = 12_000;

    /// <summary>The session's state, what its adapter waits for and, on macOS, both processes' stacks.</summary>
    public static string Describe(Codalus.Workbench.Debugging.DebugService debug)
    {
        var text = new StringBuilder("The session: ").Append(debug.Describe());
        if (OperatingSystem.IsMacOS() && debug.ProcessId is { } program)
        {
            text.Append("\nThe program's stacks:\n").Append(Sample(program));
            if (ParentOf(program) is { } parent)
            {
                text.Append("\nIts parent's (").Append(parent.ToString(CultureInfo.InvariantCulture)).Append(") stacks:\n").Append(Sample(parent));
            }
        }

        return text.ToString();
    }

    /// <summary>A one-second native sample of a process (macOS <c>sample</c>), its call graph cut to <see cref="SampleLimit"/>; why not, when it can't be had.</summary>
    internal static string Sample(int processId)
    {
        var file = Path.Combine(Path.GetTempPath(), $"codalus-sample-{processId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.txt");
        try
        {
            var (code, output) = Run("/usr/bin/sample", [processId.ToString(CultureInfo.InvariantCulture), "1", "-mayDie", "-file", file], TimeSpan.FromSeconds(30));
            if (!File.Exists(file))
            {
                return $"(no sample: exit code {code?.ToString(CultureInfo.InvariantCulture) ?? "none"}: {output.Trim()})";
            }

            var sample = File.ReadAllText(file);
            var start = sample.IndexOf("Call graph:", StringComparison.Ordinal);
            var graph = start < 0 ? sample : sample[start..];
            return graph.Length <= SampleLimit ? graph : graph[..SampleLimit] + "\n(cut)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"(no sample: {ex.Message})";
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static int? ParentOf(int processId)
    {
        try
        {
            var (code, output) = Run("/bin/ps", ["-o", "ppid=", "-p", processId.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(10));
            return code == 0 && int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parent) && parent > 1 ? parent : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static (int? Code, string Output) Run(string program, string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            Codalus.Platform.ProcessTree.Kill(process);
            return (null, "timed out");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}

public sealed class NativeStacksTests
{
    [Fact]
    public void A_sample_names_where_a_macOS_process_waits()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The sample is macOS's (Linux's waits are in Describe).");
        using var sleeping = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            var sample = NativeStacks.Sample(sleeping.Id);
            Assert.StartsWith("Call graph:", sample, StringComparison.Ordinal);
            Assert.Contains("nanosleep", sample, StringComparison.Ordinal);
        }
        finally
        {
            Codalus.Platform.ProcessTree.Kill(sleeping);
        }
    }
}
