using System.Diagnostics;
using System.Reflection;
using Codalus.Platform;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// <see cref="ProcessModules"/> and <see cref="ProcessWindows"/>, which Attach to Process reads every process through
/// (M43b): a running .NET process's modules name its runtime, one that has ended has none (it isn't listed as .NET),
/// Windows module names come from the mapped files' NT paths (a WOW64 process's 32-bit modules alike), and the main
/// windows' titles are read once for all processes (none off Windows).
/// </summary>
public sealed class ProcessModulesTests
{
    private static string Metadata(string key) =>
        typeof(ProcessModulesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>The scripted adapter's program (<c>--program</c>): it runs until it is killed.</summary>
    private static string Program => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    [Fact]
    public void A_running_dotnet_process_is_listed_with_its_runtime_and_an_ended_one_has_no_modules()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add(Program);
        start.ArgumentList.Add("--program");
        using var child = Process.Start(start)!;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Assert.Null(ProcessModules.Names(child.Id));
            }
            else
            {
                // Its runtime loads before its Main runs: wait for it rather than for output.
                var runtime = OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so";
                IReadOnlyList<string>? names = null;
                for (var i = 0; i < 300 && !(names?.Contains(runtime, StringComparer.OrdinalIgnoreCase) ?? false); i++)
                {
                    Thread.Sleep(100);
                    names = ProcessModules.Names(child.Id);
                }

                Assert.NotNull(names);
                Assert.Contains(names, n => string.Equals(n, runtime, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.DoesNotContain(names, n => n.Length == 0 || n.Contains('\\', StringComparison.Ordinal) || n.Contains('/', StringComparison.Ordinal));

                var entry = ProcessList.Get().Single(p => p.Id == child.Id);
                Assert.True(entry.IsDotnet);
                Assert.False(entry.IsDesktopClr);
            }
        }
        finally
        {
            ProcessTree.Kill(child);
            child.WaitForExit();
        }

        // Ended (this test still holds its handle on Windows, so its id names the ended process): no modules, so the
        // list wouldn't call it .NET. That's what the Attach to Process test saw when the list took longer than its
        // program waited.
        Assert.Null(ProcessModules.Names(child.Id));
    }

    [Fact]
    public void A_windows_module_is_named_by_the_file_mapped_at_its_address()
    {
        Assert.Equal("ntdll.dll", ProcessModules.FileName(@"\Device\HarddiskVolume3\Windows\System32\ntdll.dll"));
        Assert.Equal("clr.dll", ProcessModules.FileName(@"\Device\HarddiskVolume3\Windows\Microsoft.NET\Framework64\v4.0.30319\clr.dll"));

        // A WOW64 process's 32-bit runtime is mapped from Framework (not Framework64), under the same NT path scheme.
        Assert.Equal("clr.dll", ProcessModules.FileName(@"\Device\HarddiskVolume3\Windows\Microsoft.NET\Framework\v4.0.30319\clr.dll"));
        Assert.Equal("App.exe", ProcessModules.FileName(@"\Device\Mup\server\share\App.exe"));
        Assert.Equal("coreclr.dll", ProcessModules.FileName("coreclr.dll"));

        // Nothing mapped there any more (the module was unloaded meanwhile): left out.
        Assert.Null(ProcessModules.FileName(string.Empty));
        Assert.Null(ProcessModules.FileName(@"\Device\HarddiskVolume3\"));
    }

    [Fact]
    public void Linux_module_names_are_the_mapped_files_names_each_once()
    {
        string[] maps =
        [
            "5581c2a4b000-5581c2a4c000 r--p 00000000 08:01 1234  /usr/share/dotnet/dotnet",
            "7f0e3c000000-7f0e3c021000 rw-p 00000000 00:00 0 ",
            "7f0e3d200000-7f0e3d400000 r-xp 00000000 08:01 5678  /usr/share/dotnet/shared/Microsoft.NETCore.App/11.0.0/libcoreclr.so",
            "7f0e3d400000-7f0e3d500000 r--p 00200000 08:01 5678  /usr/share/dotnet/shared/Microsoft.NETCore.App/11.0.0/libcoreclr.so",
            "7f0e3e000000-7f0e3e001000 r--p 00000000 08:01 9012  /tmp/doublemapped (deleted)",
            "7ffd5a3f0000-7ffd5a411000 rw-p 00000000 00:00 0  [stack]",
        ];

        Assert.Equal(["dotnet", "libcoreclr.so", "doublemapped"], ProcessModules.LinuxNames(maps));
    }

    [Fact]
    public void Main_window_titles_are_read_once_for_every_process_and_only_on_windows()
    {
        var titles = ProcessWindows.MainWindowTitles();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(titles);
            return;
        }

        Assert.All(titles, pair =>
        {
            Assert.True(pair.Key > 0);
            Assert.NotNull(pair.Value);
        });
    }
}
