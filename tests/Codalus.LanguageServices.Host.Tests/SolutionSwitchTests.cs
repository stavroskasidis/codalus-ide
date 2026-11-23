using System.Collections.Concurrent;
using System.Diagnostics;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Projects;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// A folder closes for a solution while the folder's last design-time build is on its way: the smoke test's EF step,
/// right after the folder step, on macOS CI, where the C# host stayed "ready" with the folder's Lib and Tool next to
/// Blogging (3 projects, or 2), and the step waited for 1 until it timed out. The language service's flushes are held
/// until the solution has opened, as a busy machine delays them; before the fix, the folder's projects came back then
/// and stayed.
/// </summary>
public sealed class SolutionSwitchTests
{
    /// <summary>
    /// Saves <paramref name="path"/> as an editor does, waiting while another process reads it. Lib's change has the build
    /// host evaluate the folder's projects again, and on Windows a write can't open Tool.csproj while that reads it:
    /// "The process cannot access the file '…\Tool.csproj' because it is being used by another process" (Windows CI,
    /// develop's dry run 44922234897, with the runner's CPU queue at 57). The wait is logged, so a run says when it waited.
    /// </summary>
    private static async Task SaveAsync(string path, string text, Action<string> log, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        var tries = 0;
        while (true)
        {
            try
            {
                await File.WriteAllTextAsync(path, text, ct);
                if (tries > 0)
                {
                    log($"saved {Path.GetFileName(path)} after {waited.ElapsedMilliseconds} ms open in another process");
                }

                return;
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another process has it open.
                if (waited.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new IOException($"{path} was open in another process for {waited.ElapsedMilliseconds} ms ({tries} tries).", ex);
                }

                tries++;
                await Task.Delay(100, ct);
            }
        }
    }

    [Theory]
    [InlineData(false)] // the results arrive while the folder closes: its build host finishes the build before it stops
    [InlineData(true)] // they arrived just before the close, and wait for the next flush
    public async Task A_closed_folders_last_design_time_build_stays_out_of_the_next_solution(bool beforeTheClose)
    {
        var root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-switch-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(RepoPaths.CopyFixture("ProjectSystem", Path.Combine(root, "folder")), "FolderOnly");
        var data = RepoPaths.CopyFixture("Data", Path.Combine(root, "data"));
        var lib = Path.Combine(folder, "src", "Lib", "Lib.csproj");
        var tool = Path.Combine(folder, "src", "Tool", "Tool.csproj");
        var blogging = Path.Combine(data, "Blogging", "Blogging.csproj");
        var log = new ConcurrentQueue<string>();
        var clock = Stopwatch.StartNew();
        void Log(string line) => log.Enqueue($"{clock.ElapsedMilliseconds,6} {line}");

        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        await using var solutions = new SolutionService(() => RepoPaths.BuildHostAssembly, policy);
        var items = new ProjectItemService(solutions);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly)) { Languages = ["csharp", "vb"], MoreProjectLanguages = ["VB"] });
        await using var languages = new LanguageService(registry, solutions, policy);
        solutions.SolutionChanged += (_, _) => Log($"solution {Path.GetFileName(solutions.Solution?.Path)}");
        languages.StatusChanged += (_, e) => Log($"C# {e.Status}");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await solutions.OpenAsync(folder, trusted: true, restore: true, ct);
            await items.SetFolderProjectsAsync([lib, tool], ct);
            await LanguageFixture.WaitUntilAsync(() => solutions.RestoreState == "succeeded" && solutions.DesignTimeProgress is { Completed: true }
                                                       && languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 2 });
            await Task.Delay(1000, ct); // whatever the restore started has finished

            // Both projects change: the build host evaluates them again and builds them, and Data opens meanwhile.
            var gate = languages.FlushGate;
            var held = false;
            Task? opening = null;
            var arrived = new ConcurrentDictionary<string, bool>(SolutionService.PathComparer);
            void Open() => opening ??= Task.Run(async () =>
            {
                Log("opening Data");
                await solutions.OpenAsync(Path.Combine(data, "Data.slnx"), trusted: true, restore: false, ct);
            }, ct);

            solutions.DesignTimeProgressChanged += (_, progress) =>
            {
                if (progress.Completed || held)
                {
                    return;
                }

                // The flushes wait from here until Data has opened (raised on the build host connection's dispatch
                // loop, ahead of the build's results).
                gate.Wait(ct);
                held = true;
                Log("flushes held");
                if (!beforeTheClose)
                {
                    // The results stay behind this notification until the folder has started to close.
                    Open();
                    SpinWait.SpinUntil(() => solutions.Solution is null, TimeSpan.FromSeconds(30));
                }
            };
            solutions.CompilerInputsChanged += (_, inputs) =>
            {
                Log($"design-time build of {Path.GetFileName(inputs.Path)} (solution {Path.GetFileName(solutions.Solution?.Path)})");
                if (held && beforeTheClose && arrived.TryAdd(inputs.Path, true) && arrived.ContainsKey(lib) && arrived.ContainsKey(tool))
                {
                    Open();
                }
            };

            Log("changing Lib and Tool");
            foreach (var project in new[] { lib, tool })
            {
                await SaveAsync(project, (await File.ReadAllTextAsync(project, ct)).Replace("</Project>", $"<!-- {Guid.NewGuid():N} -->\n</Project>", StringComparison.Ordinal), Log, ct);
            }

            string Describe() => $"C# {languages.Status("csharp")}; design-time builds: {string.Join(", ", solutions.CompilerInputs.Keys.Select(Path.GetFileName))}\n" + string.Join('\n', log);
            try
            {
                await LanguageFixture.WaitUntilAsync(() => opening is not null, TimeSpan.FromSeconds(60));
                await opening!;
                await LanguageFixture.WaitUntilAsync(() => solutions.Solution?.Projects.Length == 1, TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                    Log("flushes released");
                }
            }

            try
            {
                await LanguageFixture.WaitUntilAsync(() => solutions.DesignTimeProgress is { Completed: true } && languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 1 }, TimeSpan.FromSeconds(120));
                await Task.Delay(1000, ct);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("The C# host has another solution's projects: " + Describe());
            }

            Assert.True(languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 1 }, Describe());
            Assert.Equal(new[] { blogging }, solutions.CompilerInputs.Keys);
            Assert.Null(languages.Project(lib));
            Assert.Null(languages.Project(tool));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
    /// <summary>
    /// A solution's first design-time builds reach the shell before it says the solution opened: the build host answers
    /// the open as soon as evaluation starts, and the answer's continuation, which announces the solution, ran after the
    /// results on a busy machine. The language service took those results for the solution before and dropped them, so
    /// the C# host never had the project: the smoke test's lambda compiled in Miscellaneous Files (CS1061, Linux CI, run
    /// 44574066272). Here the announcement waits until the design-time builds are done, and the flushes until it was made.
    /// </summary>
    [Fact]
    public async Task A_solutions_results_that_come_before_its_open_is_announced_reach_the_language_host()
    {
        var root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-announce-" + Guid.NewGuid().ToString("N"));
        var data = RepoPaths.CopyFixture("Data", Path.Combine(root, "data"));
        var blogging = Path.Combine(data, "Blogging", "Blogging.csproj");
        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        await using var solutions = new SolutionService(() => RepoPaths.BuildHostAssembly, policy);
        var ct = TestContext.Current.CancellationToken;

        // Ahead of the language service's handler: the announcement is held until the design-time builds are done.
        var held = false;
        solutions.SolutionChanged += (_, _) =>
        {
            if (solutions.Solution is not null && !held)
            {
                held = true;
                SpinWait.SpinUntil(() => solutions.DesignTimeProgress is { Completed: true }, TimeSpan.FromSeconds(120));
            }
        };
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly)) { Languages = ["csharp", "vb"], MoreProjectLanguages = ["VB"] });
        await using var languages = new LanguageService(registry, solutions, policy);
        try
        {
            // The flushes wait until the announcement was made, as the batch's delay let them on the busy machine.
            var gate = languages.FlushGate;
            await gate.WaitAsync(ct);
            try
            {
                await solutions.OpenAsync(Path.Combine(data, "Data.slnx"), trusted: true, restore: false, ct);
                Assert.True(held);
                Assert.True(solutions.CompilerInputs.ContainsKey(blogging), "The design-time build was meant to come before the announcement.");
            }
            finally
            {
                gate.Release();
            }

            await LanguageFixture.WaitUntilAsync(() => languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 1 }, TimeSpan.FromSeconds(120),
                () => $"C# {languages.Status("csharp")}; the projects' way there: {languages.ProjectHistory()}");
            Assert.NotNull(languages.Project(blogging));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
