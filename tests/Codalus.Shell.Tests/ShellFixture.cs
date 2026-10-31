using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Codalus.Shell.Views;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Codalus.Shell.Tests.TestAppBuilder))]

namespace Codalus.Shell.Tests;

public static class TestAppBuilder
{
    /// <summary>The headless platform's options, which every headless window reads when it opens a popup.</summary>
    private static readonly AvaloniaHeadlessPlatformOptions s_options = new() { UseHeadlessDrawing = false }; // real rendering, so frames can be captured

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(s_options);

        // Each test from an empty root locator (HeadlessIsolation). The name stays "Headless", or the session sets it up again.
        var headless = builder.WindowingSubsystemInitializer!;
        return builder.UseWindowingSubsystem(() =>
        {
            HeadlessIsolation.ClearRootLocator();
            headless();
        }, builder.WindowingSubsystemName!);
    }

    /// <summary>
    /// Until disposed, popups that open (their <c>IsOpen</c> set, a menu's <c>Open</c>) are windows of their own
    /// (<see cref="Avalonia.Controls.Primitives.PopupRoot"/>), as in the windowed app, instead of headless's default, the
    /// window's overlay layer. Dispose it before the next await: other tests' popups share the setting.
    /// </summary>
    public static IDisposable PopupsInWindowsOfTheirOwn()
    {
        s_options.OverlayPopups = false;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => s_options.OverlayPopups = true;
    }
}

/// <summary>A shell and its main window on a fresh user-data directory, running headless.</summary>
internal sealed class ShellFixture : IAsyncDisposable
{
    private ShellFixture(string userData, string? builtInPlugins, bool firstRun)
    {
        UserData = userData;
        Environment.SetEnvironmentVariable("HISTFILE", "/dev/null"); // the tests' terminals keep no history in the real home
        Environment.SetEnvironmentVariable("CODALUS_PLUGINHOST", Path.Combine(RepoRoot, "src", "Codalus.Plugins.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-pluginhost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_BUILDHOST", Path.Combine(RepoRoot, "src", "Codalus.Projects.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-buildhost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_LSHOST", Path.Combine(RepoRoot, "src", "Codalus.LanguageServices.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-lshost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_MARKUPHOST", Path.Combine(RepoRoot, "src", "Codalus.Markup.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-markuphost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_REPLHOST", Path.Combine(RepoRoot, "src", "Codalus.Interactive.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-replhost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_DATAHOST", Path.Combine(RepoRoot, "src", "Codalus.Data.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-datahost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_T4HOST", Path.Combine(RepoRoot, "src", "Codalus.TextTemplating.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-t4host.dll"));
        Environment.SetEnvironmentVariable("CODALUS_TRASH_DIR", Path.Combine(userData, "trash"));
        Environment.SetEnvironmentVariable("CODALUS_STARTUP_HOOK", Path.Combine(RepoRoot, "src", "Codalus.Debugging.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "Codalus.Debugging.StartupHook.dll"));
        Environment.SetEnvironmentVariable("CODALUS_DEBUGHOST", Path.Combine(RepoRoot, "src", "Codalus.Debugging.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-debughost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_DIAGHOST", Path.Combine(RepoRoot, "src", "Codalus.Diagnostics.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-diaghost.dll"));
        Environment.SetEnvironmentVariable("CODALUS_PREVIEWER_AGENT", Path.Combine(RepoRoot, "src", "Codalus.Debugging.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "Codalus.Previewer.Agent.dll"));
        Environment.SetEnvironmentVariable("CODALUS_WPF_PREVIEWER", Path.Combine(RepoRoot, "src", "Codalus.Previewer.Wpf", "bin", Configuration, "net8.0-windows", "codalus-wpfpreviewer.dll"));
        Environment.SetEnvironmentVariable("CODALUS_WINFORMS_DESIGNER", Path.Combine(RepoRoot, "src", "Codalus.WinForms.Designer", "bin", Configuration, "net8.0-windows", "codalus-winformsdesigner.dll"));

        // Hosting tokens stay in this process: the tests never touch the OS credential store (M13b).
        Environment.SetEnvironmentVariable(Codalus.Platform.Credentials.CredentialStore.EnvironmentVariable, "memory");

        // Programs run and debug with their output in the IDE: Windows' default console windows would open on the
        // desktop and keep the output from the tests (a test that wants them sets the settings).
        // A first-run fixture (M46) starts on an empty user data folder, as a new install does.
        var settings = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, userData).SettingsFile;
        if (!firstRun && !File.Exists(settings))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{ \"run.console\": \"outputWindow\", \"debug.console\": \"internalConsole\" }");
        }

        Shell = new ShellHost(Application.Current!, new ShellOptions
        {
            UserDataDir = userData,
            WatchFiles = false,
            StartWindow = firstRun, // the first-run setup goes on to the start window
            SkipSetup = !firstRun, // the setup (M46) shows only in its own tests
            BuiltInPluginsDir = builtInPlugins ?? PluginBin("Codalus.Plugins.Welcome"),
        });

        // System Integration (M51) never registers anything on the machine itself: the XDG layout under the user data
        // folder, with no tools to run, on every OS.
        Shell.OsIntegration = new Codalus.Workbench.Setup.LinuxOsIntegration(
            Codalus.Workbench.Setup.OsLauncher.Current(userData), Path.Combine(userData, "os", "data"), Path.Combine(userData, "os", "config"),
            (_, _, _, _) => Task.FromResult((0, string.Empty)), _ => null);
        Window = new MainWindow(Shell);
        Window.Show();
        Pump();
    }

    public string UserData { get; }

    public ShellHost Shell { get; }

    public MainWindow Window { get; }

    internal static string RepoRoot => Metadata("CodalusRepoRoot");

    /// <summary>A built-in plugin's build output (a folder with its manifest).</summary>
    public static string PluginBin(string project) => Path.Combine(RepoRoot, "src", "Plugins", project, "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework);

    internal static string Configuration => Metadata("CodalusConfiguration");

    /// <summary>Copies samples/ProjectSystem into the user-data folder (so tests can change it) and returns the .slnx path.</summary>
    public string CopyFixture()
    {
        var target = Path.Combine(UserData, "fixture");
        Copy(Path.Combine(RepoRoot, "samples", "ProjectSystem"), Path.Combine(target, "ProjectSystem"));
        File.WriteAllText(Path.Combine(target, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(target);
        File.WriteAllText(Path.Combine(target, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        return Path.Combine(target, "ProjectSystem", "ProjectSystem.slnx");
    }

    /// <summary>Copies samples/<paramref name="name"/> (standalone, like under samples/) and returns its folder.</summary>
    public string CopySample(string name)
    {
        var target = Path.Combine(UserData, "samples");
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Build.props"), Path.Combine(target, "Directory.Build.props"), overwrite: true);
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(target, "Directory.Packages.props"), overwrite: true);
        Codalus.Tests.FixtureSdk.Pin(target);
        var source = Path.Combine(RepoRoot, "samples", name);
        Copy(source, Path.Combine(target, name), Ignored(source));
        return Path.Combine(target, name);
    }

    /// <summary>
    /// The files and folders under <paramref name="folder"/> that git ignores, as full paths: what a run of the sample in
    /// place left (a script's stamp.txt, a lock file), which a test of a fresh copy mustn't see. Empty outside a git work
    /// tree (CI's checkout and WSL's mirror have none of them anyway).
    /// </summary>
    private static HashSet<string> Ignored(string folder)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git", ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z", "."])
            {
                WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var git = System.Diagnostics.Process.Start(start)!;
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            _ = stderr.Result;
            if (git.ExitCode == 0)
            {
                foreach (var path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    ignored.Add(Path.GetFullPath(Path.Combine(folder, path.TrimEnd('/'))));
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git: copy everything.
        }

        return ignored;
    }

    /// <summary>Opens samples/LanguageFeatures trusted and waits until the C# language service has loaded both projects.</summary>
    public async Task<string> OpenLanguageFeaturesAsync()
    {
        var folder = CopySample("LanguageFeatures");
        Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        await PumpUntil(() => Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180);
        return folder;
    }

    private static void Copy(string source, string target, IReadOnlySet<string>? skip = null)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source).Where(f => skip?.Contains(Path.GetFullPath(f)) != true))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source)
            .Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "node_modules") && skip?.Contains(Path.GetFullPath(d)) != true))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)), skip);
        }
    }

    /// <param name="firstRun">
    /// A new install (M46): no settings file, and the first-run setup and the start window allowed (the other fixtures
    /// skip both).
    /// </param>
    public static ShellFixture Create(string? userData = null, string? builtInPlugins = null, bool firstRun = false) =>
        new(userData ?? Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N")), builtInPlugins, firstRun);

    private static string Metadata(string key) =>
        typeof(ShellFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>Runs pending dispatcher work and a render tick.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Runs pending dispatcher work and layout, then renders a frame with all of it. Call it before hit testing:
    /// <c>InputHitTest</c> reads the compositor's last rendered frame, not the layout, and after <see cref="Pump"/> that
    /// frame can still be the one before the last layout change (its commit waits for the previous one, and the headless
    /// render timer also ticks on its own clock). The sticky scroll's band, laid out but not yet drawn, let the point over
    /// it through to the breakpoint margin under it (develop's macOS run of 0.4.0).
    /// </summary>
    /// <remarks>
    /// It returns only when the frame it waited for is also the last batch committed. A hit test mixes two states: the
    /// visuals' transforms and bounds come from the last rendered frame, but which visuals there are (each one's
    /// children, drawing and visibility) comes from the last commit. Jobs run while waiting can commit a newer batch
    /// right after the awaited one is rendered (the headless render timer ticks inside them too; the render loop's own
    /// batch-finished callback schedules the next commit), and that batch is not rendered yet: its new visuals have no
    /// rendered bounds and are skipped with everything under them. Measured on the first Find widget open after a file
    /// opens: 7 of 224 runs under CPU load returned with such a batch pending. So the frame counts only when this
    /// method's own tick rendered it, before any job ran after it; otherwise it commits and waits again.
    /// </remarks>
    public static void RenderFrame(Avalonia.Controls.TopLevel top)
    {
        var compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(top)!.Compositor;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            top.UpdateLayout();
            var batch = compositor.RequestCompositionBatchCommitAsync();
            while (true)
            {
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "No frame was rendered in 10 s.");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                if (batch.Rendered.IsCompleted)
                {
                    // This tick rendered it, and no job has run since: nothing was committed after it.
                    return;
                }

                Dispatcher.UIThread.RunJobs();
                if (batch.Rendered.IsCompleted)
                {
                    // Rendered by a tick inside the jobs, which may then have committed a newer batch: render again.
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds, then once more: a service's state (set on its own
    /// thread, which the condition usually reads) reaches the views and the context keys through a job posted to the UI
    /// thread, and the last delay could end the wait before that job ran (PR #69's Save As Solution Filter).
    /// </summary>
    /// <param name="describe">What the shell was doing, added to the failure on a timeout.</param>
    public static async Task PumpUntil(Func<bool> condition, int seconds = 30, Func<string>? describe = null)
    {
        // A monotonic clock: a machine that sleeps during the wait (a Mac with the display off) doesn't time out at once.
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= TimeSpan.FromSeconds(seconds))
            {
                Assert.Fail(describe is null ? "Timed out waiting for the shell." : "Timed out waiting for the shell: " + describe());
            }

            Pump();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs(); // the jobs only: a frame each time made the shell tests about a tenth slower
    }

    /// <summary>
    /// The file's text, or empty while another thread or process is writing it, for a condition that polls a file the
    /// shell or a host writes: on Windows a read during the write is a sharing violation, not old text (the build host
    /// saving App.csproj after a rename, jobs 121374349900 and 122158754205).
    /// </summary>
    public static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Waits until <paramref name="id"/> is enabled. A command run right after a state change can find the keys its
    /// enablement reads not set yet: they follow the state a posted job later (a solution's <c>solutionTrusted</c>, a
    /// repository's <c>gitRepository</c>, the focus), and the command was refused (PR #76's View History). On a timeout,
    /// the failure names the keys and their values.
    /// </summary>
    public async Task WhenEnabledAsync(string id, int seconds = 30)
    {
        // Already enabled: nothing is pumped, so a focus a test set just before (as a click would) stays for the command.
        var commands = Shell.Workbench.Commands;
        if (!commands.IsEnabled(id))
        {
            await PumpUntil(() => commands.IsEnabled(id), seconds, () => $"'{id}' to be enabled ({DescribeEnablement(id)})");
        }
    }

    /// <summary>The command's enablement and the context keys it reads, with their values.</summary>
    public string DescribeEnablement(string id) =>
        Shell.Workbench.Commands.Descriptors.FirstOrDefault(d => d.Id == id) is { } command
            ? $"enablement: {command.Enablement.Text}; " + string.Join(", ", Infrastructure.SweepContextWatch.Read(command.Enablement, Shell.Workbench.Context).Select(p => $"{p.Key} = {p.Value ?? "unset"}"))
            : "no such command is registered";

    /// <summary>Runs <paramref name="id"/> as the UI does (<see cref="ShellHost.Run(string)"/>), once it is enabled.</summary>
    public async Task RunAsync(string id)
    {
        await WhenEnabledAsync(id);
        Shell.Run(id);
    }

    /// <summary>Executes <paramref name="id"/> and waits for it, once it is enabled.</summary>
    public async Task<System.Text.Json.JsonElement?> ExecuteAsync(string id, params System.Text.Json.JsonElement[] args)
    {
        await WhenEnabledAsync(id);
        return await Shell.Workbench.Commands.ExecuteAsync(id, args, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Asks <paramref name="attempt"/> again, <paramref name="delayMilliseconds"/> apart, until <paramref name="done"/>
    /// holds, all within <paramref name="seconds"/>. Each attempt gets at most <paramref name="attemptSeconds"/>, and never
    /// more than the time left, so a server that never answers fails the test in minutes instead of attempts × timeout
    /// (60 × 62 s outlasted a CI job's 50 minutes). On the deadline, the failure says what it waited for and the last reply.
    /// </summary>
    /// <param name="describe">The last reply, for the failure.</param>
    /// <param name="retryOn">Exceptions that mean "ask again" (a server starting again); any other fails the test at once.</param>
    public static async Task<T?> RetryUntil<T>(
        string waitingFor,
        Func<CancellationToken, Task<T?>> attempt,
        Func<T?, bool> done,
        Func<T?, string> describe,
        int seconds = 240,
        int attemptSeconds = 60,
        int delayMilliseconds = 2000,
        Func<Exception, bool>? retryOn = null)
    {
        var test = TestContext.Current.CancellationToken;
        var deadline = TimeSpan.FromSeconds(seconds);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var last = "none (no attempt finished)";
        var attempts = 0;
        while (true)
        {
            var left = deadline - started.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                Assert.Fail($"Timed out after {seconds} s and {attempts} attempts waiting for {waitingFor}. The last reply: {last}.");
            }

            attempts++;
            var limit = left < TimeSpan.FromSeconds(attemptSeconds) ? left : TimeSpan.FromSeconds(attemptSeconds);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(test))
            {
                timeout.CancelAfter(limit);
                try
                {
                    // WaitAsync: an attempt that doesn't watch its token is left behind at its limit all the same.
                    var reply = await attempt(timeout.Token).WaitAsync(timeout.Token);
                    if (done(reply))
                    {
                        return reply;
                    }

                    last = describe(reply);
                }
                catch (OperationCanceledException) when (!test.IsCancellationRequested)
                {
                    last = $"no answer within {limit.TotalSeconds:0} s";
                }
                catch (Exception ex) when (retryOn?.Invoke(ex) == true)
                {
                    last = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            left = deadline - started.Elapsed;
            if (left > TimeSpan.Zero)
            {
                var pause = TimeSpan.FromMilliseconds(delayMilliseconds);
                await Task.Delay(pause < left ? pause : left, test);
            }
        }
    }

    /// <summary>A completion list for a <see cref="RetryUntil"/> failure: how many items, and the first labels.</summary>
    public static string Describe(Codalus.LanguageServices.Protocol.CompletionList? completion) =>
        completion is null
            ? "no completion list (no language client yet, or the server returned none)"
            : $"{completion.Items.Length} items ({string.Join(", ", completion.Items.Take(15).Select(i => i.Label))}{(completion.Items.Length > 15 ? ", …" : "")})";

    /// <summary>
    /// A token for one language request: canceled after <paramref name="seconds"/>, so a server that never answers
    /// fails the test instead of hanging it (a blocked UI thread never reaches PumpUntil's own timeout).
    /// </summary>
    public static CancellationToken RequestTimeout(int seconds = 30) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    /// <summary>
    /// The default bindings' <c>primary</c> modifier (<c>Keys.Parse</c>): ⌘ on macOS, Ctrl elsewhere. A test presses
    /// a <c>primary+…</c> binding with it: on macOS, Ctrl+T matches no binding (Go To All is Cmd+T).
    /// </summary>
    public static RawInputModifiers Primary { get; } = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Pump();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Pump();
    }

    public async ValueTask DisposeAsync()
    {
        Window.CloseWithoutConfirming(); // a window with unsaved documents would ask, and stay open with its shell
        Pump();
        await Shell.DisposeAsync();
        try
        {
            // git writes its object files read-only.
            foreach (var file in Directory.EnumerateFiles(UserData, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(UserData, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
