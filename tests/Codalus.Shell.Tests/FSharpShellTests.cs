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
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

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
        var client = await f.Shell.Languages.ClientAsync(ShellHost.FSharpLanguageId);
        Assert.NotNull(client);
        var uri = DocumentUris.FromPath(path);
        var line = model.Text.GetLineByOffset(call);
        var hover = await client!.HoverAsync(uri, new Position(line.LineNumber - 1, call - line.Offset + "(totalA".Length), TestContext.Current.CancellationToken);
        Assert.Contains("val totalArea", hover?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("The total area of the shapes.", hover?.Contents.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("command:", hover?.Contents.Value, StringComparison.Ordinal);

        // Formatting is Fantomas's.
        var greeter = model.Text.Text.IndexOf("let greeter = Greeter(\"Hello\")", StringComparison.Ordinal);
        model.Text.Replace(greeter, "let greeter = Greeter(\"Hello\")".Length, "let greeter   =   Greeter(\"Hello\")");
        await Task.Delay(500, TestContext.Current.CancellationToken); // the change reaches the server
        var edits = await client.FormatAsync(uri, null, new FormattingOptions(4, true), TestContext.Current.CancellationToken);
        Assert.Contains(edits, e => e.NewText.Contains("let greeter = Greeter(\"Hello\")", StringComparison.Ordinal));
        model.Text.Text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

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
