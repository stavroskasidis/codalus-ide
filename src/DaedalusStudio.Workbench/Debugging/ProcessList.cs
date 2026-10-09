using System.Diagnostics;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>A process that can be attached to.</summary>
public sealed record ProcessEntry(int Id, string Name, string? Title, bool IsDotnet);

/// <summary>Lists processes for Attach to Process, with the ones that run .NET first (best effort, per OS).</summary>
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

                result.Add(new ProcessEntry(process.Id, name, title, IsDotnet(process)));
            }
        }

        return [.. result.OrderByDescending(p => p.IsDotnet).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }

    /// <summary>Whether the process has loaded the .NET runtime (coreclr). Unknown (no access, macOS) counts as no.</summary>
    private static bool IsDotnet(Process process)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return File.ReadAllText($"/proc/{process.Id}/maps").Contains("libcoreclr.so", StringComparison.Ordinal);
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
