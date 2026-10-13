using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>The C# Interactive window (ADR 0015) in the headless shell, with the real REPL host.</summary>
public sealed class InteractiveTests
{
    private static string Transcript(ShellFixture f) => f.Shell.InteractiveWindow.Transcript.GetText();

    [AvaloniaFact]
    public async Task Submissions_run_from_the_input_with_history_completion_and_send_to_interactive()
    {
        await using var f = ShellFixture.Create();

        // The REPL host starts lazily, when the window first shows (roadmap: every host has a lazy-start test).
        ShellFixture.Pump();
        Assert.False(f.Shell.Interactive.IsRunning);
        f.Shell.Run("workbench.view.interactive");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<InteractiveView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<InteractiveView>().Single();
        var vm = f.Shell.InteractiveWindow;
        view.FocusPrimary();

        // Enter runs a complete submission; the state carries over.
        f.Type("var x = 6;");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => vm.History.Count == 1 && !vm.IsBusy, seconds: 60);
        f.Type("x * 7");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Transcript(f).Split('\n').Any(l => l.Trim() == "42"), seconds: 60);
        Assert.Equal(string.Empty, view.Input.Text);

        // An incomplete submission gets a new line instead of running.
        f.Type("if (x > 1) {");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => view.Input.Text.Contains('\n', StringComparison.Ordinal));
        Assert.Equal(2, vm.History.Count);
        f.Type("Console.WriteLine(\"big\"); }");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Transcript(f).Split('\n').Any(l => l.Trim() == "big"), seconds: 30);

        // Alt+Up recalls the previous submission.
        f.Press(Key.Up, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => view.Input.Text.StartsWith("if (x > 1)", StringComparison.Ordinal));
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => view.Input.Text.Length == 0);

        // Completion after a dot lists members.
        f.Type("Math.");
        await ShellFixture.PumpUntil(() => view.CompletionWindow is not null, seconds: 30);
        Assert.Contains(view.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "Max");
        f.Press(Key.Escape);
        f.Press(Key.Escape);

        // Send to Interactive runs the editor's selection (here, its current line).
        var file = Path.Combine(f.UserData, "Snippet.cs");
        await File.WriteAllTextAsync(file, "Console.WriteLine(\"sent \" + (x + 1));\n", TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(file, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        f.Shell.Run("interactive.sendSelection");
        await ShellFixture.PumpUntil(() => Transcript(f).Contains("sent 7", StringComparison.Ordinal), seconds: 30);

        // Reset drops the state.
        await vm.ResetAsync(null);
        vm.Input = "x";
        Assert.True(await vm.SubmitAsync(force: true));
        await ShellFixture.PumpUntil(() => Transcript(f).Contains("CS0103", StringComparison.Ordinal), seconds: 30);
    }

    [AvaloniaFact]
    public async Task Initialize_interactive_with_project_references_its_build_output()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        var greetings = Path.Combine(folder, "src", "Greetings", "Greetings.csproj");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Project(greetings) is not null, seconds: 90);

        await f.Shell.InitializeInteractiveWithProjectAsync(greetings);
        Assert.Contains("Initializing C# Interactive with Greetings", Transcript(f), StringComparison.Ordinal);
        var vm = f.Shell.InteractiveWindow;
        vm.Input = "Greetings.Greeter.Greet(\"the REPL\")";
        Assert.True(await vm.SubmitAsync());
        await ShellFixture.PumpUntil(() => Transcript(f).Contains("\"Hello from the REPL\"", StringComparison.Ordinal), seconds: 60);
        Assert.Equal(Path.GetDirectoryName(greetings), vm.Service.Configuration.WorkingDirectory);
    }
}
