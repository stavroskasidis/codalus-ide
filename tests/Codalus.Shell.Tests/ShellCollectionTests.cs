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
    public void The_failure_lists_the_dispatchers_running_timers_by_what_their_ticks_call()
    {
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += OnTick;
        timer.Start();
        try
        {
            Assert.Matches($@"(^|, )300000 ms: {System.Text.RegularExpressions.Regex.Escape(typeof(ShellCollectionTests).FullName!)}\.[^,]*{nameof(OnTick)}", DispatcherTimers());
        }
        finally
        {
            timer.Stop();
        }

        Assert.DoesNotContain(nameof(OnTick), DispatcherTimers(), StringComparison.Ordinal);

        static void OnTick(object? sender, EventArgs e)
        {
        }
    }

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
    public Task A_disposed_shell_whose_explorer_hears_of_an_edit_is_collected() => AssertCollectedAsync(
        _ => Task.CompletedTask,
        // An edit tells the Solution Explorer, through a post, which reads the file's members again 500 ms later: its
        // timer, started just before the dispose or after it, held the shell until then (Linux CI, run 41236941035).
        afterDispose: f => f.Shell.Explorer.OnFileChanged(Path.Combine(f.UserData, "notes.txt")));

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

    [AvaloniaFact]
    public Task A_disposed_shell_whose_settings_changed_with_sync_on_is_collected() => AssertCollectedAsync(async f =>
    {
        // A change schedules a sync 5 s later: it held the disposed shell until then, and then synced from it.
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(ShellHost.SyncTargetSetting, Path.Combine(f.UserData, "cloud-drive"), Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
        await settings.SetAsync("editor.fontSize", 17, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
    });

    [AvaloniaFact]
    public Task A_disposed_shell_whose_document_just_changed_is_collected() => AssertCollectedAsync(async f =>
    {
        // An edit tells the plugins' proposed API, which batches edited documents for 500 ms: its timer held the shell,
        // and the edit of the unsaved-changes case, when it came just before the dispose, failed it (macOS CI).
        await f.Shell.Languages.ChangeAsync(Path.Combine(f.UserData, "notes.txt"), 2, []);
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
            // What ran then, and whether the shell goes later (work that ends by itself) or never (something keeps it): on
            // the macOS runner this told the application menu's entries from the shell's own git (docs/ci-flakes.md).
            var children = ChildProcesses();
            var timers = DispatcherTimers();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (shell.IsAlive && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                ShellFixture.Pump();
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.Fail($"The disposed shell is still reachable{(shell.IsAlive ? " 30 s later too" : $" (collected {clock.Elapsed.TotalSeconds:0.0} s later)")}. The test host's child processes then: {children}. "
                + $"The dispatcher's running timers then: {timers}; 30 s later: {DispatcherTimers()}");
        }

        Assert.False(window.IsAlive, "The closed main window is still reachable.");
    }

    /// <summary>
    /// The dispatcher's running timers, by what their ticks call (failure messages). A running timer holds what its handler
    /// reaches until it ticks, and a test's dispatcher stops ticking at its end: every cause of this class's failures so far
    /// was one (a debouncer, the plugins' batch of edits, a settings sync). Linux CI kept a shell 30 s, past any of those
    /// (PR #291's run 41616893942). Read through reflection: Avalonia doesn't list them.
    /// </summary>
    internal static string DispatcherTimers()
    {
        const System.Reflection.BindingFlags Fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        if (typeof(Avalonia.Threading.Dispatcher).GetFields(Fields).Select(f => f.GetValue(dispatcher)).OfType<IEnumerable<Avalonia.Threading.DispatcherTimer>>().FirstOrDefault() is not { } timers)
        {
            return "unknown (the dispatcher has no list of timers)";
        }

        var tick = typeof(Avalonia.Threading.DispatcherTimer).GetField(nameof(Avalonia.Threading.DispatcherTimer.Tick), Fields);
        var running = timers.ToList();
        return running.Count == 0
            ? "none"
            : string.Join(", ", running.Select(t => $"{t.Interval.TotalMilliseconds:0} ms: {string.Join(" + ", ((tick?.GetValue(t) as Delegate)?.GetInvocationList() ?? []).Select(d => $"{d.Method.DeclaringType?.FullName}.{d.Method.Name}"))}"));
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
