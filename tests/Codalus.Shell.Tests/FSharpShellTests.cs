using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// M36 (ADR 0067) in the headless shell on samples/FSharp, with the real FsAutoComplete and Fantomas (installed once
/// into the shared test cache with <c>dotnet tool install</c>): F# files get errors as you type, Quick Info and
/// formatting; Solution Explorer shows the compile order with signature files nested, and Move Up and Move Down edit it;
/// an F# project gets F#'s property pages.
/// </summary>
public sealed class FSharpShellTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    /// <summary>Quick Info at the end of <paramref name="marker"/> in an open file, asked again until it contains <paramref name="expected"/>.</summary>
    private static async Task<string?> QuickInfoAsync(ShellFixture f, string path, string marker, string expected, int seconds = 120)
    {
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var offset = document!.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var line = document.Model.Text.GetLineByOffset(offset);
        string? info = null;
        for (var attempt = 0; attempt < seconds && info?.Contains(expected, StringComparison.Ordinal) != true; attempt++)
        {
            try
            {
                var client = await f.Shell.Languages.ClientAsync(ShellHost.FSharpLanguageId);
                info = (await client!.HoverAsync(DocumentUris.FromPath(path), new Position(line.LineNumber - 1, offset - line.Offset), TestContext.Current.CancellationToken))?.Contents.Value;
            }
            catch (Rpc.JsonRpcRemoteException)
            {
                // not checked yet
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcConnectionLostException or ObjectDisposedException)
            {
                // FsAutoComplete starts again once a project it loaded is restored
            }

            if (info?.Contains(expected, StringComparison.Ordinal) != true)
            {
                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }
        }

        return info;
    }

    private static string Tail(string text) => text.Length > 6000 ? text[^6000..] : text;

    private static IReadOnlyList<string> Names(ShellFixture f, string project) =>
        [.. SolutionTree.Contents(project, f.Shell.Solutions.Projects[project]).Select(n => n.Name)];

    [AvaloniaFact]
    public async Task Fsharp_files_are_served_by_fsautocomplete_and_the_compile_order_is_edited_in_solution_explorer()
    {
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("FSharp");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        // FsAutoComplete reads a C# project it references from its built assembly (it compiles only F#).
        using (var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["build", Path.Combine(folder, "CsLibrary", "CsLibrary.csproj"), "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true }))
        {
            await build!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, build.ExitCode);
        }

        await f.Shell.InstallFSharpAsync();
        Assert.True(f.Shell.FSharp.IsInstalled, f.Shell.LanguageLog.GetText());
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "FSharp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 5 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

        // Program.fs: FsAutoComplete loads the solution itself; an error typed comes back, then goes.
        var path = Path.Combine(folder, "FsConsole", "Program.fs");
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var model = document!.Model;
        Assert.Equal(ShellHost.FSharpLanguageId, model.LanguageId);
        var call = model.Text.Text.IndexOf("(totalArea shapes)", StringComparison.Ordinal);
        model.Text.Replace(call, "(totalArea shapes)".Length, "(totalArea \"shapes\")");
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.Get(path, ShellHost.FSharpLanguageId) is { } d && d.Items.Any(x => x.Severity == DiagnosticSeverity.Error), seconds: 180);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("No F# error:\n" + f.Shell.LanguageLog.GetText());
        }

        model.Text.Replace(call, "(totalArea \"shapes\")".Length, "(totalArea shapes)");
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.Get(path, ShellHost.FSharpLanguageId) is { } d && d.Items.All(x => x.Severity != DiagnosticSeverity.Error), seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The error stayed: " + string.Join("; ", f.Shell.Languages.Diagnostics.Get(path, ShellHost.FSharpLanguageId)!.Items.Select(d => $"{d.Range.Start.Line}:{d.Range.Start.Character} {d.Code} {d.Message}")) + "\n" + model.Text.Text);
        }

        // Quick Info from another F# project, with its documentation and without FsAutoComplete's command links.
        var hover = await QuickInfoAsync(f, path, "(totalA", "The total area of the shapes.");
        Assert.Contains("val totalArea", hover, StringComparison.Ordinal);
        Assert.Contains("The total area of the shapes.", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("command:", hover, StringComparison.Ordinal);
        var uri = DocumentUris.FromPath(path);

        // Formatting is Fantomas's.
        var greeter = model.Text.Text.IndexOf("let greeter = Greeter(\"Hello\")", StringComparison.Ordinal);
        model.Text.Replace(greeter, "let greeter = Greeter(\"Hello\")".Length, "let greeter   =   Greeter(\"Hello\")");
        await Task.Delay(500, TestContext.Current.CancellationToken); // the change reaches the server
        var edits = await ShellFixture.RetryUntil(
            "Fantomas (FsAutoComplete) to format \"let greeter   =   Greeter(...)\" back to \"let greeter = Greeter(\"Hello\")\"",
            async token => await f.Shell.Languages.ClientAsync(ShellHost.FSharpLanguageId) is { } client ? await client.FormatAsync(uri, null, new FormattingOptions(4, true), token) : null,
            reply => reply?.Any(e => e.NewText.Contains("let greeter = Greeter(\"Hello\")", StringComparison.Ordinal)) == true,
            reply => reply is null ? "no edits (no F# client yet, or the server returned none)" : $"{reply.Length} edits: {string.Join(" | ", reply.Select(e => e.NewText.Length > 80 ? e.NewText[..80] + "…" : e.NewText))}",
            delayMilliseconds: 1000,
            // FsAutoComplete starts again once a project it loaded is restored.
            retryOn: ex => ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or ObjectDisposedException);
        Assert.Contains(edits ?? [], e => e.NewText.Contains("let greeter = Greeter(\"Hello\")", StringComparison.Ordinal));
        model.Text.Text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        // A script: what it #loads is known (Helpers.fsx's describe).
        var scriptInfo = await QuickInfoAsync(f, Path.Combine(folder, "Scripts", "Explore.fsx"), "(descr", "val describe");
        Assert.Contains("val describe", scriptInfo, StringComparison.Ordinal);
        Assert.Contains("Describes a list of numbers.", scriptInfo, StringComparison.Ordinal);

        // The navigation bar and the CodeLens rows from an editor's first frame, where FsAutoComplete will list types and
        // lenses: Shapes.fs declares a union and has a lens on area (line 7); Geometry.fs, a module, has a lens on
        // totalArea (line 7) and no bar. Nothing moves when the answers come (NavigationBarFirstFrameTests).
        foreach (var (file, types) in new[] { ("Shapes.fs", true), ("Geometry.fs", false) })
        {
            var (frames, bar, editor, rows) = await NavigationBarFirstFrameTests.OpenRecordingBarAsync(f, Path.Combine(folder, "FsLibrary", file), seconds: 240,
                rows: e => e.Insights.LensRowAbove(7) is not null);
            Assert.True(bar.HasTypes == types, $"{file}: {bar.Types.Count} types listed");
            Assert.True(frames.Count > 0 && frames.TrueForAll(shown => shown == types), $"{file}: the bar showed in {frames.Count(s => s)} of {frames.Count} frames");
            Assert.True(rows.TrueForAll(r => r), $"{file}: the lens row was there in {rows.Count(r => r)} of {rows.Count} frames; {editor.Insights.DescribeLenses()}");
            Assert.NotNull(editor.Insights.LensRowAbove(7));
        }

        // A type provider: JsonProvider's provided property (once the package is restored, and FsAutoComplete has
        // reloaded the project and run the provider: minutes on a hosted macOS runner).
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[Path.Combine(folder, "FsData", "FsData.fsproj")].Restored, seconds: 300);
        var provided = await QuickInfoAsync(f, Path.Combine(folder, "FsData", "Weather.fs"), "sample.Days[0].Hi", "property High", seconds: 360);
        Assert.True(provided?.Contains("property High: decimal", StringComparison.Ordinal) == true, $"Quick Info on the provided property: {provided ?? "none"}\n{Tail(f.Shell.LanguageLog.GetText())}");

        // Solution Explorer: the compile order, the signature file under its implementation; Move Up and Move Down.
        var library = Path.Combine(folder, "FsLibrary", "FsLibrary.fsproj");
        var geometry = Path.Combine(folder, "FsLibrary", "Geometry.fs");
        Assert.Equal(["Shapes.fs", "Geometry.fs"], Names(f, library));
        Assert.Equal(["Shapes.fsi"], SolutionTree.Contents(library, f.Shell.Solutions.Projects[library])[0].Children.Select(n => n.Name));
        f.Shell.Explorer.Reveal("f:" + geometry);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("explorerItemOrdered") is true);
        await f.Shell.MoveInCompileOrderAsync(down: false);
        await ShellFixture.PumpUntil(() => Names(f, library) is ["Geometry.fs", "Shapes.fs"], seconds: 60);
        Assert.Equal(["Geometry.fs", "Shapes.fsi", "Shapes.fs"], CompileOrder.Of(f.Shell.Solutions.Projects[library]).Select(Path.GetFileName));
        f.Shell.Explorer.Reveal("f:" + geometry);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Selected?.Node.Path == geometry);
        await f.Shell.MoveInCompileOrderAsync(down: true);
        await ShellFixture.PumpUntil(() => Names(f, library) is ["Shapes.fs", "Geometry.fs"], seconds: 60);

        // F#'s property pages.
        var properties = f.Shell.OpenProjectProperties(library);
        var names = properties!.Pages.SelectMany(p => p.Properties).Select(p => p.Name).ToList();
        Assert.Contains("WarnOn", names);
        Assert.Contains("Tailcalls", names);
        Assert.DoesNotContain("AllowUnsafeBlocks", names);
        Assert.DoesNotContain("ImplicitUsings", names);
    }
}

/// <summary>
/// M36b (ADR 0068): F# Interactive is <c>dotnet fsi</c> in a terminal; Send to F# Interactive writes the code with a
/// line directive and <c>;;</c>, Send References adds a built project with <c>#r</c>, and Reset starts it again.
/// </summary>
public sealed class FSharpInteractiveTests
{
    private static string Text(Workbench.Terminal.TerminalSession session) =>
        string.Join("\n", Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));

    /// <summary>The rows without line breaks: a long line wraps at the terminal's width wherever the path's length puts it.</summary>
    private static bool Shows(Workbench.Terminal.TerminalSession session, string text) =>
        string.Concat(Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText().TrimEnd())).Contains(text, StringComparison.Ordinal);

    [Fact]
    public void Submissions_carry_a_line_directive_and_end_with_double_semicolons()
    {
        var path = Path.Combine(Path.GetTempPath(), "Script.fsx");
        Assert.Equal($"# 3 @\"{path}\"\nlet x = 1\nlet y = 2\n;;\n", ShellHost.FSharpSubmission("let x = 1\r\nlet y = 2\r\n", path, 3));
        Assert.Equal("1 + 1\n;;\n", ShellHost.FSharpSubmission("1 + 1", null, 1));
        Assert.Equal("# 1 @\"C:\\a \"\"b\"\".fsx\"\nx\n;;\n", ShellHost.FSharpSubmission("x", "C:\\a \"b\".fsx", 1));
    }

    [Fact]
    public void References_skip_the_frameworks_reference_assemblies()
    {
        var arguments = new[]
        {
            "-o:obj/Debug/net10.0/Lib.dll",
            "-r:/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Runtime.dll",
            "-r:/home/me/.nuget/packages/fsharp.core/10.0.100/lib/netstandard2.1/FSharp.Core.dll",
            "-r:/src/CsLibrary/bin/Debug/net10.0/CsLibrary.dll",
            "--optimize-",
        };
        Assert.Equal(
            "#r @\"/home/me/.nuget/packages/fsharp.core/10.0.100/lib/netstandard2.1/FSharp.Core.dll\"\n#r @\"/src/CsLibrary/bin/Debug/net10.0/CsLibrary.dll\"\n#r @\"/src/Lib/bin/Debug/net10.0/Lib.dll\"\n;;\n",
            ShellHost.FSharpReferences(arguments, "/src/Lib/bin/Debug/net10.0/Lib.dll"));
    }

    [AvaloniaFact]
    public async Task Code_sent_from_an_fsharp_file_runs_in_fsharp_interactive_and_reset_starts_it_again()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("FSharp");
        var script = Path.Combine(folder, "Scripts", "Explore.fsx");
        var document = await f.Shell.OpenFileAsync(script, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var editor = document!.Controller!.Editor;

        // The selection: two definitions; F# Interactive answers with their values.
        var start = document.Model.Text.Text.IndexOf("let square", StringComparison.Ordinal);
        var end = document.Model.Text.Text.IndexOf("// [fsi-end]", StringComparison.Ordinal);
        editor.Select(start, end - start);
        f.Shell.SendToFSharpInteractive();
        var session = f.Shell.FSharpInteractive;
        Assert.NotNull(session);
        Assert.Equal(ShellHost.FSharpInteractiveName, session!.Name);
        try
        {
            await ShellFixture.PumpUntil(() => Shows(session, "val squares: int list = [1; 4; 9]"), seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("F# Interactive said:\n" + Text(session));
        }

        // An error names the file and line (the line directive).
        editor.Select(0, 0);
        document.Model.Text.Insert(0, "let broken: int = \"x\"\n");
        editor.CaretOffset = 0;
        f.Shell.SendToFSharpInteractive(lineOnly: true);
        await ShellFixture.PumpUntil(() => Shows(session, "Explore.fsx(1,"), seconds: 60);

        // Reset: a new session, without the old definitions.
        var reset = f.Shell.ResetFSharpInteractive();
        Assert.NotSame(session, reset);
        Assert.DoesNotContain(session, f.Shell.Terminals.Sessions);
        reset.Write("squares;;\r"); // Enter
        try
        {
            await ShellFixture.PumpUntil(() => Shows(reset, "error FS0039"), seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("After the reset, F# Interactive said:\n" + Text(reset));
        }
    }
}

/// <summary>M36b: Expecto tests (through YoloDev.Expecto.TestSdk's VSTest adapter) in the Test Explorer, on samples/FSharp.</summary>
public sealed class FSharpTestingTests
{
    [AvaloniaFact]
    public async Task Expecto_tests_run_in_the_test_explorer()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest"));
        var folder = f.CopySample("FSharp");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "FSharp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 5 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
        var tests = Path.Combine(folder, "FsTests", "FsTests.fsproj");
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[tests].IsTestProject, seconds: 300); // after the restore: Microsoft.NET.Test.Sdk says so

        // The restored packages' graph: Expecto's own dependencies, nested under it in the Dependencies node.
        var info = f.Shell.Solutions.Projects[tests];
        Assert.Contains(info.Packages, p => p.Name == "Expecto" && p.Dependencies.Contains("Mono.Cecil"));
        var dependencies = SolutionTree.Project(new Codalus.Projects.Protocol.SolutionProjectInfo(tests, "FsTests", null, null), info, trusted: true).Children.Single(n => n.Kind == ExplorerNodeKind.Dependencies);
        var expecto = dependencies.Children.Single(g => g.Name == "Packages").Children.Single(p => p.Name == "Expecto");
        Assert.Contains(expecto.Children, c => c.Name == "Mono.Cecil");

        // The project shows before it's built; running it builds, discovers and runs its tests.
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Any(r => r.Label == "FsTests")), seconds: 120);
        var project = f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "FsTests"));
        await f.Shell.RunTestsAsync(Workbench.Testing.TestRunKind.Run, [project.Key]);
        try
        {
            await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning && f.Shell.Tests.Read(_ => project.Leaves().Count(l => l.Outcome == Workbench.Testing.TestOutcome.Passed)) == 2, seconds: 300);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("Tests: " + f.Shell.Tests.Read(_ => string.Join(", ", project.Leaves().Select(l => $"{l.Label}={l.Outcome}"))) + "\n" + string.Join("\n", f.Shell.Workbench.Output.Channels.Select(c => $"== {c.Name}\n{c.GetText()}")));
        }

        // Expecto's names: the test list is the class, each test a method.
        var leaves = f.Shell.Tests.Read(_ => project.Leaves().Select(l => l.Label).ToList());
        Assert.Contains("a rectangle's area is width times height", leaves);
        Assert.Contains(f.Shell.Tests.Read(_ => project.SelfAndDescendants().Select(n => n.Label).ToList()), l => l == "Shapes");
    }
}
