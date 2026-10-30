using System.Runtime.CompilerServices;
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

    [AvaloniaFact]
    public async Task A_code_viewer_releases_its_grammar_when_its_window_closes()
    {
        // A tool window's viewer (Code Definition, an HTTP response), which its owner never releases: its grammar's
        // tokenizer thread waited for work and held it, its window and the shell.
        await using var f = ShellFixture.Create();
        var viewer = new Codalus.Shell.Editor.CodeViewer(f.Shell, f.Shell.EditorFont);
        var window = new Avalonia.Controls.Window { Content = viewer, Width = 400, Height = 300 };
        window.Show();
        viewer.Show("class C { int x = 1; }", "csharp", null);
        ShellFixture.Pump();
        Assert.True(viewer.HasGrammar);

        window.Content = null;
        Assert.False(viewer.HasGrammar);

        window.Content = viewer; // back in a window: its colors come back
        ShellFixture.Pump();
        Assert.True(viewer.HasGrammar);

        window.Close();
        Assert.False(viewer.HasGrammar);
    }

    [AvaloniaFact]
    public Task A_shell_disposed_before_its_window_is_interactive_is_collected() => AssertCollectedAsync(
        _ => Task.CompletedTask,
        afterDispose: f => f.Shell.OnInteractive()); // the window's first frames came after its close: the listener for other processes kept the shell

    [AvaloniaFact]
    public Task A_disposed_shell_whose_window_became_interactive_twice_is_collected() => AssertCollectedAsync(f =>
    {
        // The window's first frames, and a caller that asks again (tests do): a second listener for other processes
        // replaced the first, which was never disposed and kept the shell.
        f.Shell.OnInteractive();
        f.Shell.OnInteractive();
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public Task A_disposed_shell_whose_entries_were_in_the_application_menu_is_collected() => AssertCollectedAsync(f =>
    {
        // macOS's application menu (About, Settings…) outlives the window: its entries' commands kept every shell, and
        // failed every case of this class on the Mac.
        Codalus.Shell.Views.MenuBuilder.AttachApplicationMenu(f.Shell);
        return Task.CompletedTask;
    });

    private static async Task AssertCollectedAsync(Func<ShellFixture, Task> use, Action<ShellFixture>? afterDispose = null)
    {
        var (shell, window) = await CreateUseAndDisposeAsync(use, afterDispose);
        await Task.Yield(); // off the frames of the dispose, whose continuations ran inline and still hold the fixture

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

        if (shell.IsAlive)
        {
            // TODO(issue): on the macOS runner every case failed here, after the shell's own git and probes were cancelled
            // on dispose (docs/ci-flakes.md). What ran then, and whether the shell goes later (work that ends by itself)
            // or never (something keeps it), say what keeps it.
            var children = ChildProcesses();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (shell.IsAlive && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                ShellFixture.Pump();
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.Fail($"The disposed shell is still reachable{(shell.IsAlive ? " 30 s later too" : $" (collected {clock.Elapsed.TotalSeconds:0.0} s later)")}. The test host's child processes then: {children}");
        }

        Assert.False(window.IsAlive, "The closed main window is still reachable.");
    }

    /// <summary>The test host's child processes, with their command lines on Unix.</summary>
    private static string ChildProcesses()
    {
        var ids = Codalus.Platform.ProcessTree.Children(Environment.ProcessId);
        if (ids.Count == 0)
        {
            return "none";
        }

        if (OperatingSystem.IsWindows())
        {
            return string.Join(", ", ids);
        }

        try
        {
            var ps = new System.Diagnostics.ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in (string[])["-o", "pid=,etime=,command=", "-p", string.Join(',', ids)])
            {
                ps.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(ps)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return "\n" + output.TrimEnd();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return string.Join(", ", ids);
        }
    }

    /// <summary>Not inlined, so no local of the test's method holds the fixture.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Shell, WeakReference Window)> CreateUseAndDisposeAsync(Func<ShellFixture, Task> use, Action<ShellFixture>? afterDispose)
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

        afterDispose?.Invoke(f);

        return references;
    }
}
