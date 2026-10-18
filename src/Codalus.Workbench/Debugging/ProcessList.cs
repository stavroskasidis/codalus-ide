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
/// <remarks>
/// Each process costs one read of its modules (<see cref="Codalus.Platform.ProcessModules"/>: one walk of its loader
/// list on Windows) and the windows' titles are read once for all processes (<see cref="Codalus.Platform.ProcessWindows"/>).
/// Through <see cref="Process.Modules"/> and <see cref="Process.MainWindowTitle"/> the list took more than a minute on a
/// loaded CI runner. A process that ends while the list is read is still listed (from the processes' snapshot), not as .NET.
/// </remarks>
public static class ProcessList
{
    public static IReadOnlyList<ProcessEntry> Get()
    {
        var result = new List<ProcessEntry>();
        var self = Environment.ProcessId;
        var processes = Process.GetProcesses();
        var titles = Codalus.Platform.ProcessWindows.MainWindowTitles();
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.Id == self || process.Id == 0)
                {
                    continue;
                }

                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue; // exited meanwhile
                }

                // One snapshot of its modules decides both: .NET and .NET Framework can't disagree.
                var (dotnet, desktop) = Classify(name, Codalus.Platform.ProcessModules.Names(process.Id), OperatingSystem.IsWindows());
                var title = titles.TryGetValue(process.Id, out var t) && t.Length > 0 ? t : null;
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
