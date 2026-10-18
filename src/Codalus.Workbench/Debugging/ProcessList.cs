using System.Diagnostics;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// A process that can be attached to. <see cref="IsDotnet"/>: it runs .NET (CoreCLR, or the desktop CLR when
/// <see cref="IsDesktopClr"/>, which the .NET Framework debugger attaches to, M43b); a desktop-CLR process is always .NET.
/// </summary>
public sealed record ProcessEntry(int Id, string Name, string? Title, bool IsDotnet, bool IsDesktopClr = false)
{
    /// <summary>Whether it runs .NET: CoreCLR, or the desktop CLR (<see cref="IsDesktopClr"/> implies it).</summary>
    public bool IsDotnet { get; init; } = IsDotnet || IsDesktopClr;

    /// <summary>The runtime as the Attach to Process list shows it.</summary>
    public string? Runtime => IsDesktopClr ? ".NET Framework" : IsDotnet ? ".NET" : null;
}

/// <summary>Lists processes for Attach to Process, with the ones that run .NET (or .NET Framework, on Windows) first (best effort, per OS).</summary>
public static class ProcessList
{
    public static IReadOnlyList<ProcessEntry> Get()
    {
        var result = new List<ProcessEntry>();
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self || process.Id == 0)
                {
                    continue;
                }

                string name;
                string? title = null;
                try
                {
                    name = process.ProcessName;
                    if (OperatingSystem.IsWindows())
                    {
                        title = process.MainWindowTitle is { Length: > 0 } t ? t : null;
                    }
                }
                catch (InvalidOperationException)
                {
                    continue; // exited meanwhile
                }

                // One snapshot of its modules decides both: .NET and .NET Framework can't disagree.
                var (dotnet, desktop) = Classify(name, Codalus.Platform.ProcessModules.Names(process.Id), OperatingSystem.IsWindows());
                result.Add(new ProcessEntry(process.Id, name, title, dotnet, desktop));
            }
        }

        return [.. result.OrderByDescending(p => p.IsDotnet).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }

    /// <summary>
    /// Whether a process runs .NET, and whether that's the desktop CLR (only looked for on Windows), from its loaded
    /// modules' file names (null: unknown, as on macOS or without access, when only a process named <c>dotnet</c>
    /// counts). CoreCLR is <c>coreclr.dll</c>, <c>libcoreclr.so</c> or <c>libcoreclr.dylib</c>; the desktop CLR is
    /// <see cref="Codalus.Debugging.DesktopClr.RuntimeOf"/>'s (CoreCLR wins), and is .NET too.
    /// </summary>
    internal static (bool IsDotnet, bool IsDesktopClr) Classify(string processName, IReadOnlyCollection<string>? modules, bool windows)
    {
        var desktop = windows && modules is not null && Codalus.Debugging.DesktopClr.RuntimeOf(modules) is not null;
        var core = modules is not null && modules.Any(m => s_coreClr.Contains(m, StringComparer.OrdinalIgnoreCase));
        return (desktop || core || string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase), desktop);
    }

    private static readonly string[] s_coreClr = [Codalus.Debugging.DesktopClr.CoreClr, "libcoreclr.so", "libcoreclr.dylib"];
}
