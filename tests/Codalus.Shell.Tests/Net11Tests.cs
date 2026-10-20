using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>
/// .NET 11 projects in the shell (M30, ADR 0025) on samples/Net11: the language service, debugging, tests and Hot
/// Reload. The fixture needs the .NET 11 SDK: skipped without it locally, failing in CI.
/// </summary>
public sealed class Net11Tests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static void RequireNet11Sdk()
    {
        var sdks = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = sdks.StandardOutput.ReadToEnd();
        sdks.WaitForExit();
        Assert.SkipWhen(!output.Contains("\n11.0.", StringComparison.Ordinal) && !output.StartsWith("11.0.", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("CI") != "true",
            "The .NET 11 SDK is not installed (samples/Net11/global.json).");
    }

    private static async Task<string> OpenAsync(ShellFixture f)
    {
        RequireNet11Sdk();
        var folder = f.CopySample("Net11");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Net11.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 5 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        Assert.StartsWith("11.0.", Path.GetFileName(Path.TrimEndingDirectorySeparator(f.Shell.Solutions.SdkPath!)), StringComparison.Ordinal);
        return folder;
    }

    [AvaloniaFact]
    public async Task Net11_projects_get_the_language_service_and_the_debugger()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: >= 5 }, seconds: 180);

        // C# on .NET 11: no false errors, and the library's types resolve across projects.
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        var editor = await f.Shell.OpenFileAsync(program, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var text = editor!.Model.Text.Text;
        var position = editor.Model.PositionAt(text.IndexOf("new Circle", StringComparison.Ordinal) + "new ".Length + 1);
        var client = f.Shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
        await ShellFixture.PumpUntil(() => client.HoverAsync(editor.Model.Uri, position, ShellFixture.RequestTimeout()).GetAwaiter().GetResult()?.Contents.Value.Contains("Circle", StringComparison.Ordinal) == true, seconds: 120);
        Assert.DoesNotContain(f.Shell.Languages.Diagnostics.For(program), d => d.Severity == LanguageServices.Protocol.DiagnosticSeverity.Error);
        Assert.DoesNotContain("CS9057", f.Shell.LanguageLog.GetText(), StringComparison.Ordinal); // an analyzer built for a newer compiler

        // Razor in a net11.0 project: until the host's Roslyn catches up with the SDK's, the Razor generator of an
        // older installed SDK stands in (ADR 0025).
        var view = Path.Combine(folder, "src", "Components", "CircleView.razor");
        var razor = await f.Shell.OpenFileAsync(view, preview: false, null);
        await ShellFixture.PumpUntil(() => razor?.Controller is not null);
        var area = razor!.Model.PositionAt(razor.Model.Text.Text.IndexOf("@Circle.Area", StringComparison.Ordinal) + "@Circle.".Length + 1);
        await ShellFixture.PumpUntil(() => client.HoverAsync(razor.Model.Uri, area, ShellFixture.RequestTimeout()).GetAwaiter().GetResult()?.Contents.Value.Contains("Area", StringComparison.Ordinal) == true,
            seconds: 120);

        // F5 on the .NET 11 runtime: a breakpoint, a local, the program's output.
        var stop = Line(program, "break");
        f.Shell.Debugger.Breakpoints.Toggle(program, stop);
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 240);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == stop);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "circle"));
        f.Shell.Debugger.Breakpoints.Clear();
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Contains("runtime=11 library=net11.0", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Tests_run_on_net10_and_net11_with_vstest_and_mtp_and_coverage()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest"));
        var folder = await OpenAsync(f);
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Count(r => r.Label is "Shapes.Tests" or "Shapes.MtpTests" && r.Leaves().Any()) == 2), seconds: 180);

        // VSTest on both frameworks of the multi-targeted project.
        var vstest = f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "Shapes.Tests"));
        await f.Shell.RunTestsAsync(TestRunKind.Run, [vstest.Key]);
        await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning && f.Shell.Tests.Read(roots =>
            roots.Single(r => r.Label == "Shapes.Tests").Leaves().Count(l => l.Outcome == TestOutcome.Passed) == 4), seconds: 300);
        var frameworks = f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "Shapes.Tests").Leaves().Select(l => l.Label).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["net10.0", "net11.0"], frameworks);

        // Microsoft.Testing.Platform on .NET 11, with coverage of the library.
        var covered = new List<string>();
        f.Shell.Tests.CoverageReported += (_, report) =>
        {
            lock (covered)
            {
                covered.AddRange(report.Files.Where(file => file.Lines.Any(l => l.Hits > 0)).Select(file => file.Uri));
            }
        };
        var mtp = f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "Shapes.MtpTests"));
        await f.Shell.RunTestsAsync(TestRunKind.Coverage, [mtp.Key]);
        await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning && f.Shell.Tests.Read(roots =>
            roots.Single(r => r.Label == "Shapes.MtpTests").Leaves().Any(l => l.Outcome == TestOutcome.Passed)), seconds: 300);
        await ShellFixture.PumpUntil(() =>
        {
            lock (covered)
            {
                return covered.Any(u => u.EndsWith("/Circle.cs", StringComparison.OrdinalIgnoreCase));
            }
        }, seconds: 60);
    }

    [AvaloniaFact]
    public async Task Hot_reload_applies_an_edit_with_the_net11_sdk()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenAsync(f);
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        f.Shell.Runs.SetStartupProject(app);
        f.Shell.Runs.SetProfile(app, "Watch");

        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 180);
        var program = f.Shell.HotReloadProgram!;
        try
        {
            await ShellFixture.PumpUntil(() => program.Channel?.GetText().Contains("message: before", StringComparison.Ordinal) == true, seconds: 240);
            await ShellFixture.PumpUntil(() => program.HotReload!.State == Workbench.Build.HotReloadState.Watching, seconds: 120);

            var file = Path.Combine(folder, "src", "App", "Program.cs");
            var editor = await f.Shell.OpenFileAsync(file, preview: false, null);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            var offset = editor!.Model.Text.Text.IndexOf("message: before", StringComparison.Ordinal);
            editor.Model.Text.Replace(offset, "message: before".Length, "message: after");
            await f.RunAsync("run.applyCodeChanges");
            await ShellFixture.PumpUntil(() => program.Channel!.GetText().Contains("message: after", StringComparison.Ordinal), seconds: 180);
            // dotnet watch reports the change after the app already runs the new code.
            await ShellFixture.PumpUntil(() => program.HotReload!.Applied >= 1 || program.HotReload.Restarts >= 1, seconds: 60);
        }
        finally
        {
            f.Shell.StopRunning();
        }
    }
}
