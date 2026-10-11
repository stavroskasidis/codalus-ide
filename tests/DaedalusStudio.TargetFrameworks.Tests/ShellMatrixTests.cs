using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Debugging;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell;
using DaedalusStudio.Shell.Tests;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Testing;

namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// samples/TargetFrameworks in the headless shell (M33, ADR 0030): the editor's C# version per framework, tests and
/// coverage on every framework's test host, Hot Reload from .NET 6, and F5 on .NET Core 2.1.
/// </summary>
public sealed class ShellMatrixTests
{
    private static async Task<string> OpenAsync(ShellFixture f)
    {
        CompatRoot.Require();
        var folder = f.CopySample("TargetFrameworks");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "TargetFrameworks.sln"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 38 && f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.RestoreState is "succeeded" or "failed", seconds: 600);
        Assert.Equal("succeeded", f.Shell.Solutions.RestoreState);

        // The design-time builds after the restore: .NET Core 2.x and 3.0 get their framework from packages.
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Keys.All(p => MatrixFixture.HasReferences(f.Shell.Solutions, p)), seconds: 600);
        return folder;
    }

    private static string AppProgram(string folder, Framework framework) => Path.Combine(folder, framework.Id, framework.App, "Program.cs");

    [AvaloniaFact]
    public async Task The_editor_reports_what_each_frameworks_csharp_version_lacks_as_the_compiler_does()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: >= 38 }, seconds: 600);

        foreach (var framework in Matrix.All.Where(m => m.NextFeature is not null))
        {
            // The program uses its framework's newest C# feature and compiles; the next version's feature doesn't.
            var file = AppProgram(folder, framework);
            var editor = await f.Shell.OpenFileAsync(file, preview: false, null);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            var text = editor!.Model.Text.Text;
            var marker = text.IndexOf("// [next]", StringComparison.Ordinal);
            editor.Model.Text.Replace(marker, "// [next]".Length, framework.NextFeature!);
            var expected = $"not available in C# {framework.CSharp}";
            var line = Matrix.Line(file, "next") - 1;

            // The compiler's error on the edited line, and none elsewhere (the language service has the project's
            // references). Both settle as the host catches up, so they are waited for together.
            List<Diagnostic> errors = [];
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                errors = [.. f.Shell.Languages.Diagnostics.For(file).Where(d => d.Severity == DiagnosticSeverity.Error)];
                if (errors.Any(d => d.Message.Contains(expected, StringComparison.Ordinal)) && errors.All(e => e.Range.Start.Line == line))
                {
                    break;
                }

                Assert.True(started.Elapsed < TimeSpan.FromSeconds(240),
                    $"{framework.Tfm}: expected only '{expected}' on line {line + 1}, got: " + string.Join("; ", errors.Select(e => $"{e.Range.Start.Line + 1}: {e.Message}")));
                ShellFixture.Pump();
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            editor.Model.Text.Replace(marker, framework.NextFeature!.Length, "// [next]");
            ShellFixture.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Tests_run_and_cover_on_every_frameworks_test_host()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("DaedalusStudio.Plugins.DotNetTest"));
        await OpenAsync(f);
        var projects = Matrix.All.Select(m => m.Tests).ToHashSet(StringComparer.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Count(r => projects.Contains(r.Label) && r.Leaves().Any()) == projects.Count), seconds: 600);

        var covered = new List<string>();
        f.Shell.Tests.CoverageReported += (_, report) =>
        {
            lock (covered)
            {
                covered.AddRange(report.Files.Where(file => file.Lines.Any(l => l.Hits > 0)).Select(file => file.Uri.Replace('\\', '/')));
            }
        };
        var keys = f.Shell.Tests.Read(roots => roots.Where(r => projects.Contains(r.Label)).Select(r => r.Key).ToList());
        await WithOutput(f, f.Shell.RunTestsAsync(TestRunKind.Coverage, keys), TimeSpan.FromMinutes(15));
        await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning, seconds: 900);
        var outcomes = f.Shell.Tests.Read(roots => roots.Where(r => projects.Contains(r.Label))
            .ToDictionary(r => r.Label, r => r.Leaves().Select(l => l.Outcome).ToList()));
        Assert.All(projects, p => Assert.True(outcomes[p] is [TestOutcome.Passed, TestOutcome.Passed], $"{p}: {string.Join(", ", outcomes[p])}"));

        // Each framework's library was measured through its own test host (coverlet's netstandard2.0 collector before
        // .NET 8, the current one after).
        await ShellFixture.PumpUntil(() =>
        {
            lock (covered)
            {
                return Matrix.All.All(m => covered.Any(u => u.EndsWith($"/{m.Lib}/Greeter.cs", StringComparison.OrdinalIgnoreCase)));
            }
        }, seconds: 120);
    }

    [AvaloniaFact]
    public async Task Hot_reload_is_disabled_before_net6_with_its_reason_and_applies_an_edit_on_net6()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        var core31 = Path.Combine(folder, "Core31", "Core31.App", "Core31.App.csproj");
        var net60 = Path.Combine(folder, "Net60", "Net60.App", "Net60.App.csproj");

        f.Shell.Runs.SetStartupProject(core31);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("hotReloadSupported") is false);
        Assert.Equal("Hot Reload needs .NET 6 or later: Core31.App targets netcoreapp3.1.", f.Shell.HotReloadUnsupportedReason());
        await WithOutput(f, f.Shell.StartWithHotReloadAsync(), TimeSpan.FromMinutes(5));
        Assert.Null(f.Shell.HotReloadProgram);
        Assert.Contains(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("Hot Reload needs .NET 6 or later: Core31.App targets netcoreapp3.1.", StringComparison.Ordinal));

        f.Shell.Runs.SetStartupProject(net60);
        f.Shell.Runs.SetProfile(net60, "Watch");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("hotReloadSupported") is true);
        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 240);
        var program = f.Shell.HotReloadProgram!;
        try
        {
            await ShellFixture.PumpUntil(() => program.Channel?.GetText().Contains("message: before", StringComparison.Ordinal) == true, seconds: 300);
            Assert.Contains("runtime=6.0.", program.Channel!.GetText(), StringComparison.Ordinal);
            await ShellFixture.PumpUntil(() => program.HotReload!.State == Workbench.Build.HotReloadState.Watching, seconds: 120);

            var file = Path.Combine(folder, "Net60", "Net60.App", "Program.cs");
            var editor = await f.Shell.OpenFileAsync(file, preview: false, null);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            var offset = editor!.Model.Text.Text.IndexOf("message: before", StringComparison.Ordinal);
            editor.Model.Text.Replace(offset, "message: before".Length, "message: after");
            f.Shell.Run("run.applyCodeChanges");
            await ShellFixture.PumpUntil(() => program.Channel!.GetText().Contains("message: after", StringComparison.Ordinal), seconds: 180);
        }
        finally
        {
            f.Shell.StopRunning();
        }
    }

    [AvaloniaFact]
    public async Task The_solution_explorer_shows_the_framework_package_of_netcoreapp21()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        var lib = Path.Combine(folder, "Core21", "Core21.Lib", "Core21.Lib.csproj");
        var id = $"deps:{lib}|framework|Microsoft.NETCore.App";
        f.Shell.Explorer.RevealWhenAvailable(id);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Selected?.Id == id, seconds: 60);
        Assert.Equal("(2.1.0)", f.Shell.Explorer.Selected!.Node.Detail);

        // The Debug menu's item says why Hot Reload is off (looked at in the screenshots, D25).
        f.Shell.Runs.SetStartupProject(Path.Combine(folder, "Core31", "Core31.App", "Core31.App.csproj"));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("hotReloadSupported") is false);
        foreach (var theme in new[] { "blue", "dark" })
        {
            await f.Shell.Workbench.Settings.SetAsync(DaedalusStudio.Shell.Theming.ThemeService.SettingKey, theme, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
            ShellFixture.Pump();
            Screenshot(f, $"m33-dependencies-{theme}");
        }
    }

    /// <summary>
    /// Awaits <paramref name="work"/> while the UI runs, and fails with the Output window's channels when it takes longer
    /// than <paramref name="limit"/> (a run that never reports its end would otherwise hang the suite).
    /// </summary>
    private static async Task WithOutput(ShellFixture f, Task work, TimeSpan limit)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!work.IsCompleted)
        {
            if (started.Elapsed > limit)
            {
                Assert.Fail($"Not done after {limit.TotalMinutes} minutes. Output:\n" + string.Join("\n", f.Shell.Workbench.Output.Channels.Select(c => $"--- {c.Name}\n{Tail(c.GetText())}")));
            }

            ShellFixture.Pump();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await work;
    }

    private static string Tail(string text) => text.Length > 3000 ? text[^3000..] : text;

    /// <summary>Saves the window when <c>DAEDALUS_UI_SNAPSHOTS</c> names a folder (for looking at the rendering).</summary>
    private static void Screenshot(ShellFixture f, string name)
    {
        if (Environment.GetEnvironmentVariable("DAEDALUS_UI_SNAPSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(f.Window))
            {
                visual.InvalidateVisual();
            }

            ShellFixture.Pump();
            using var frame = f.Window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(folder, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public async Task F5_on_a_netcoreapp21_app_stops_at_its_breakpoint()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && !CompatRoot.Required, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        var framework = Matrix.Get("Core21");
        f.Shell.Runs.SetStartupProject(Path.Combine(folder, framework.Id, framework.App, framework.App + ".csproj"));
        var program = AppProgram(folder, framework);
        var stop = Matrix.Line(program, "break");
        f.Shell.Debugger.Breakpoints.Toggle(program, stop);
        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == stop);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "pair"));
        f.Shell.Debugger.Breakpoints.Clear();
        f.Shell.Run("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Contains("runtime=2.1.", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
    }
}
