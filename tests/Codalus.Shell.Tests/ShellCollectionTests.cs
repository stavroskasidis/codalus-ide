using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>
/// A disposed shell and its closed window are garbage. Every shell test leaves one, so one that something still holds
/// is kept for the rest of the run with all it reaches (its documents, its views, their rendering's native memory): a
/// full run of these tests grew to 6 GB and ran out of memory (docs/ci-flakes.md).
/// </summary>
public sealed class ShellCollectionTests
{
    [AvaloniaFact]
    public Task A_disposed_shell_and_its_closed_window_are_collected() => AssertCollectedAsync(_ => Task.CompletedTask);

    [AvaloniaFact]
    public Task A_disposed_shell_with_a_solution_and_an_open_file_is_collected() => AssertCollectedAsync(async f =>
    {
        var folder = await f.OpenLanguageFeaturesAsync();
        await f.Shell.OpenFileAsync(Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).First(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
    });

    [AvaloniaFact]
    public Task A_disposed_shell_with_unsaved_changes_a_status_message_and_a_toast_is_collected() => AssertCollectedAsync(async f =>
    {
        // A window with unsaved changes asks before it closes (and stayed open); the status bar's message and the toast
        // wait to hide themselves.
        var file = Path.Combine(f.UserData, "notes.txt");
        File.WriteAllText(file, "notes\n");
        await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        f.Shell.ActiveEditor!.Model.Text.Insert(0, "more ");
        f.Shell.StatusBar.ShowMessage("Saved.", TimeSpan.FromMinutes(1));
        f.Shell.Workbench.Notifications.Info("Done.");
        ShellFixture.Pump();
        Assert.True(f.Shell.HasUnsavedDocuments);
    });

    private static async Task AssertCollectedAsync(Func<ShellFixture, Task> use)
    {
        var (shell, window) = await CreateUseAndDisposeAsync(use);
        await Task.Yield(); // off the frames of the dispose, whose continuations ran inline and still hold the fixture

        // AvaloniaEdit's commands keep the last focused element (a static) until another one takes the focus, as the next
        // window does: an editor's text area would hold its closed editor, and through it the shell, until then.
        var next = new Window { Content = new TextBox() };
        next.Show();
        ((TextBox)next.Content).Focus();
        ShellFixture.Pump();
        next.Close();
        // The work the dispose queued runs, but no timer is waited for: a pending one holds the shell until it ticks, and
        // once a test ends its dispatcher never ticks it (the repository discovery, started again by the solution closing
        // during the dispose, kept every shell of a full run).
        for (var i = 0; i < 10 && (shell.IsAlive || window.IsAlive); i++)
        {
            ShellFixture.Pump();
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        if (Environment.GetEnvironmentVariable("LEAK_PAUSE") is { } pause && shell.IsAlive) { File.WriteAllText(pause, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); Thread.Sleep(240_000); } // TEMP
        Assert.False(shell.IsAlive, "The disposed shell is still reachable.");
        Assert.False(window.IsAlive, "The closed main window is still reachable.");
    }

    /// <summary>Not inlined, so no local of the test's method holds the fixture.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Shell, WeakReference Window)> CreateUseAndDisposeAsync(Func<ShellFixture, Task> use)
    {
        var f = ShellFixture.Create();
        var references = (new WeakReference(f.Shell), new WeakReference(f.Window));
        try
        {
            await use(f);
        }
        finally
        {
            await f.DisposeAsync();
        }

        return references;
    }
}
