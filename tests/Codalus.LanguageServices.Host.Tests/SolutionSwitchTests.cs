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
                await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project, ct)).Replace("</Project>", $"<!-- {Guid.NewGuid():N} -->\n</Project>", StringComparison.Ordinal), ct);
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
}
