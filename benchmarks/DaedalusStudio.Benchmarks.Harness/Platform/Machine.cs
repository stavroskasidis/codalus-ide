using System.Diagnostics;
using System.Runtime.InteropServices;
using DaedalusStudio.Benchmarks.Harness.Model;

namespace DaedalusStudio.Benchmarks.Harness.Platform;

internal static class Machine
{
    public static MachineInfo Describe() => new(
        Os: OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        OsVersion: RuntimeInformation.OSDescription,
        Architecture: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        Cpu: Cpu(),
        LogicalCores: Environment.ProcessorCount,
        MemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Gpu: Gpu(),
        DisplayRefreshHz: OperatingSystem.IsWindows() ? Win32.PrimaryRefreshRate() : null,
        IsCi: Environment.GetEnvironmentVariable("CI") is "true" or "1");

    private static string Cpu()
    {
        if (OperatingSystem.IsWindows())
        {
            return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string
                   ?? "unknown";
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
        }

        return Run("sysctl", "-n machdep.cpu.brand_string") ?? "unknown";
    }

    private static string? Gpu()
    {
        if (OperatingSystem.IsWindows())
        {
            return Run("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_VideoController | Select-Object -First 1).Name\"");
        }

        return OperatingSystem.IsMacOS()
            ? Run("sh", "-c \"system_profiler SPDisplaysDataType | grep 'Chipset Model' | head -1 | cut -d: -f2\"")
            : Run("sh", "-c \"lspci 2>/dev/null | grep -i -m1 'vga' | cut -d: -f3\"");
    }

    public static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
