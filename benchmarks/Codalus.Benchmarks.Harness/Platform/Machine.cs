using System.Diagnostics;
using System.Runtime.InteropServices;
using Codalus.Benchmarks.Harness.Model;

namespace Codalus.Benchmarks.Harness.Platform;

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
        IsCi: Environment.GetEnvironmentVariable("CI") is "true" or "1",
        L3CacheBytes: L3Cache());

    /// <summary>"32 MB" (or "512 KB"), or "unknown".</summary>
    public static string FormatCache(long? bytes) => bytes switch
    {
        null or <= 0 => "unknown",
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes.Value / 1048576.0:0.#} MB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes.Value / 1024} KB"),
    };

    /// <summary>
    /// The L3 cache of the first CPU: sysfs on Linux, <c>Win32_Processor.L3CacheSize</c> (KB) on Windows,
    /// <c>hw.l3cachesize</c> on macOS (Apple silicon has none; null then). A VM that reports 0 counts as unknown.
    /// </summary>
    private static long? L3Cache()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Run("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Select-Object -First 1).L3CacheSize\"") is { } kb
                       && long.TryParse(kb, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value * 1024
                    : null;
            }

            if (OperatingSystem.IsMacOS())
            {
                return Run("sysctl", "-n hw.l3cachesize") is { } text
                       && long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value
                    : null;
            }

            const string caches = "/sys/devices/system/cpu/cpu0/cache";
            if (!Directory.Exists(caches))
            {
                return null;
            }

            foreach (var index in Directory.EnumerateDirectories(caches, "index*"))
            {
                if (File.Exists(Path.Combine(index, "level")) && File.ReadAllText(Path.Combine(index, "level")).Trim() == "3"
                    && File.Exists(Path.Combine(index, "size")))
                {
                    return ParseCacheSize(File.ReadAllText(Path.Combine(index, "size")));
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>sysfs's cache size: "33792K", "32M" or a number of bytes.</summary>
    public static long? ParseCacheSize(string text)
    {
        text = text.Trim();
        var multiplier = text.EndsWith('K') ? 1024L : text.EndsWith('M') ? 1L << 20 : 1L;
        var digits = multiplier == 1 ? text : text[..^1];
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value * multiplier
            : null;
    }

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
