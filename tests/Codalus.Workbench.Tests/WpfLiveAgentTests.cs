using System.Diagnostics;
using Codalus.Platform;
using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M40b (ADR 0072), Windows only: the WPF live agent (the WPF previewer's assembly as a startup hook) in the fixture's
/// running app, through the IDE's <see cref="LiveSession"/>: windows, the logical and visual trees, dependency
/// properties with their value sources, a local value set and refused, the highlight, pick mode, and the path Go to
/// XAML resolves.
/// </summary>
public sealed class WpfLiveAgentTests(WpfFixture wpf, ITestOutputHelper output) : IClassFixture<WpfFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        Exception? last = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try
            {
                var value = await read();
                if (done(value))
                {
                    return value;
                }
            }
            catch (PreviewerException ex)
            {
                last = ex; // the app hasn't made its Application or window yet
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException(what + (last is null ? string.Empty : ": " + last.Message));
    }

    [Fact]
    public async Task The_agent_leaves_a_dotnet_process_that_isnt_a_wpf_app_alone()
    {
        // Under Hot Reload, dotnet watch (and the builds it runs) gets the app's environment, the agent's hook and port
        // included. The hook read WPF's trace sources at once, which ended any process without WPF at its start
        // (WindowsDesktopShellTests' Hot Reload step, CI run 43587454991). This test's own program has no WPF, on any OS:
        // with the hook it ends as it does without it.
        Assert.True(File.Exists(WpfFixture.Previewer), WpfFixture.Previewer);
        var (plainExit, _) = await RunHelpAsync(hook: false);
        var (hookedExit, error) = await RunHelpAsync(hook: true);
        Assert.DoesNotContain("WindowsBase", error, StringComparison.Ordinal);
        Assert.Equal(plainExit, hookedExit);
    }

    /// <summary>This test program's <c>--help</c>, with the WPF agent's hook and port or without.</summary>
    private static async Task<(int ExitCode, string Error)> RunHelpAsync(bool hook)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(WpfLiveAgentTests).Assembly.Location);
        start.ArgumentList.Add("--help");
        if (hook)
        {
            start.Environment["DOTNET_STARTUP_HOOKS"] = WpfFixture.Previewer;
            start.Environment["CODALUS_LIVE_AGENT_PORT"] = "1";
            start.Environment["CODALUS_LIVE_AGENT_TOKEN"] = "token";
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync(Ct);
        var standardOutput = process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        _ = await standardOutput;
        return (process.ExitCode, await error);
    }

    [Fact]
    public async Task A_running_wpf_app_shows_its_trees_and_properties_takes_a_value_and_gives_its_paths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF apps run on Windows only (D31).");
        await using var session = LiveSession.Start(WpfFixture.Previewer) ?? throw new InvalidOperationException("No agent.");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += (_, _) => connected.TrySetResult();
        session.Disconnected += (_, _) => disconnected.TrySetResult();

        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(wpf.AppDll)! };
        start.ArgumentList.Add(wpf.AppDll);
        foreach (var (name, value) in session.Environment(null))
        {
            start.Environment[name] = value;
        }

        // The app's output is collected and written while the test is still running: a line that arrives
        // after the test ends would throw on the thread pool (no current test) and take the test process down.
        var appOutput = new System.Text.StringBuilder();
        using var app = Process.Start(start)!;
        app.OutputDataReceived += (_, e) => { lock (appOutput) { appOutput.AppendLine("[app] " + e.Data); } };
        app.ErrorDataReceived += (_, e) => { lock (appOutput) { appOutput.AppendLine("[app] " + e.Data); } };
        app.BeginOutputReadLine();
        app.BeginErrorReadLine();
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            // The agent connects before Main; it answers once the app has its Application and window.
            var windows = await EventuallyAsync(() => session.WindowsAsync(Ct), w => w.Count > 0, "the app's window");
            var window = Assert.Single(windows);
            Assert.Equal("WpfApp.MainWindow", window.Type);
            Assert.Equal("To do", window.Title);

            // The logical tree: the XAML's elements (the user control's own content included), nothing a template made.
            var logical = await session.TreeAsync(window.Id, visual: false, Ct);
            Assert.Equal("MainWindow", logical[0].Type);
            Assert.Equal(0, logical[0].ParentId);
            var button = Assert.Single(logical, n => n.Type == "Button");
            var box = Assert.Single(logical, n => n.Name == "NewTitleBox");
            Assert.Equal("TextBox", box.Type);
            Assert.Contains(logical, n => n.Type == "RatingControl");
            Assert.Contains(logical, n => n.Type == "ListBox");
            Assert.DoesNotContain(logical, n => n.Templated);

            // The visual tree adds the templates' parts (the button's Chrome border, the list's items).
            var visual = await session.TreeAsync(window.Id, visual: true, Ct);
            Assert.Contains(visual, n => n.Templated && n.Name == "Chrome");
            Assert.Contains(visual, n => n.Type == "ListBoxItem");
            Assert.True(visual.Count > logical.Count);

            // Properties and where their values come from.
            var properties = await session.PropertiesAsync(box.Id, Ct);
            Assert.Equal("Local", properties.Single(p => p.Name == "Text").Source); // a binding is a local value
            Assert.Equal("0,0,8,0", properties.Single(p => p.Name == "Margin").Value);
            Assert.Equal("Local", properties.Single(p => p.Name == "Margin").Source);
            Assert.Contains(properties, p => p.Name == "DockPanel.Dock" || p.Name == "Background");
            var buttonProperties = await session.PropertiesAsync(button.Id, Ct);
            Assert.Equal("Style", buttonProperties.Single(p => p.Name == "Padding").Source); // AccentButton's setter
            Assert.Equal("DockPanel.Dock", Assert.Single(buttonProperties, p => p.Name == "DockPanel.Dock").Name);
            Assert.True(buttonProperties.Single(p => p.Name == "IsMouseOver").IsReadOnly);

            // A value set from text becomes local; a read-only one and a bad value are refused with the reason.
            await session.SetAsync(box.Id, "Margin", "4", Ct);
            properties = await session.PropertiesAsync(box.Id, Ct);
            Assert.Equal("4,4,4,4", properties.Single(p => p.Name == "Margin").Value);
            await session.SetAsync(box.Id, "Background", "Red", Ct);
            Assert.Equal("#FFFF0000", (await session.PropertiesAsync(box.Id, Ct)).Single(p => p.Name == "Background").Value);
            await session.SetAsync(box.Id, "Width", "Auto", Ct); // Width's own LengthConverter
            await Assert.ThrowsAsync<PreviewerException>(() => session.SetAsync(button.Id, "IsMouseOver", "True", Ct));
            await Assert.ThrowsAsync<PreviewerException>(() => session.SetAsync(box.Id, "Margin", "not a thickness", Ct));

            // The highlight goes on and off; pick mode turns on and off.
            await session.HighlightAsync(button.Id, Ct);
            await session.HighlightAsync(null, Ct);
            await session.PickAsync(on: true, Ct);
            await session.PickAsync(on: false, Ct);

            // Go to XAML's path: the window's class and the steps from it, as the XAML declares them.
            var path = await session.PathAsync(button.Id, Ct);
            Assert.Equal("WpfApp.MainWindow", path.WindowType);
            Assert.Equal(["MainWindow", "Grid", "DockPanel", "Button"], path.Steps.Select(s => s.Type));
            var chrome = visual.First(n => n.Name == "Chrome");
            Assert.Equal("Button", (await session.PathAsync(chrome.Id, Ct)).Steps[^1].Type); // out of the template first
            Assert.Equal("NewTitleBox", (await session.PathAsync(box.Id, Ct)).Steps[^1].Name);

            // M64b: why the button's style applies (AccentButton, its Padding setter), and its triggers' states.
            var styles = await session.StylesAsync(button.Id, Ct);
            var accent = Assert.Single(styles, s => s.Kind == "style");
            Assert.True(accent.State == LiveStyleState.Applies && accent.Selector.StartsWith("Style TargetType=", StringComparison.Ordinal), $"{accent.Selector} {accent.State} {accent.Reason}");
            Assert.Contains(accent.Setters, s => s.StartsWith("Padding = ", StringComparison.Ordinal));
            Assert.All(styles.Where(s => s.Kind == "trigger"), t => Assert.True(t.State == LiveStyleState.Applies || t.Reason.Length > 0, t.Selector));
            var unstyled = await session.StylesAsync(box.Id, Ct);
            Assert.Contains(unstyled, s => s.State == LiveStyleState.No || s.Kind == "style");

            // XAML breakpoints reach the agent and come back with a state each (none stops: no debugger is attached);
            // Break When Value Changes turns on and off, and is refused for a property the element doesn't have.
            var windowXaml = Path.Combine(wpf.App, "MainWindow.xaml");
            var windowText = await File.ReadAllTextAsync(windowXaml, Ct);
            var states = await session.SetXamlBreakpointsAsync(XamlBreakpoints.Encode(
                [new XamlBreakpoints.AgentBreakpoint("button", windowXaml, XamlBreakpoints.Resolve(windowText, 28)), new XamlBreakpoints.AgentBreakpoint("title", windowXaml, XamlBreakpoints.Resolve(windowText, 12))],
                breakOnFailedBinding: false), Ct);
            Assert.Equal(["button", "title"], states.Select(s => s.Id));
            Assert.All(states, s => Assert.True(s.Bound || s.Message.Length > 0, s.Id));
            await session.WatchAsync(box.Id, "Margin", on: true, Ct);
            await session.SetAsync(box.Id, "Margin", "5", Ct);
            await session.WatchAsync(box.Id, "Margin", on: false, Ct);
            await Assert.ThrowsAsync<PreviewerException>(() => session.WatchAsync(box.Id, "NoSuchProperty", on: true, Ct));

            // XAML Hot Reload (M64): the window loaded again from changed XAML, its handlers and named fields connected
            // to the running class; a binding that fails in it is reported; what doesn't load leaves the window as it was.
            var bindingsFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.BindingsFailed += (_, _) => bindingsFailed.TrySetResult();
            var xaml = await File.ReadAllTextAsync(Path.Combine(wpf.App, "MainWindow.xaml"), Ct);
            var changed = xaml.Replace("Title=\"{Binding Title}\"", "Title=\"Reloaded\"", StringComparison.Ordinal)
                .Replace("Margin=\"0,0,8,0\"", "Margin=\"0,0,9,0\" Tag=\"{Binding NoSuchPath}\"", StringComparison.Ordinal);
            var reloaded = await session.ReloadAsync("-", "-", "-", false, changed, Ct);
            Assert.True(reloaded.Outcome == LiveReloadOutcome.Applied && reloaded.Count == 1, $"{reloaded.Outcome} {reloaded.Count} {reloaded.Message}");
            Assert.Equal("Reloaded", Assert.Single(await session.WindowsAsync(Ct)).Title);
            var newBox = Assert.Single(await session.TreeAsync(window.Id, visual: false, Ct), n => n.Name == "NewTitleBox");
            Assert.Equal("0,0,9,0", (await session.PropertiesAsync(newBox.Id, Ct)).Single(p => p.Name == "Margin").Value);
            await bindingsFailed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Contains(await session.BindingFailuresAsync(Ct), f => f.Path == "NoSuchPath" && f.Property == "Tag" && f.ElementName == "NewTitleBox");
            var failed = await session.ReloadAsync("-", "-", "-", false, changed.Replace("Click=\"OnAddClicked\"", "Click=\"NoSuchHandler\"", StringComparison.Ordinal), Ct);
            Assert.True(failed.Outcome == LiveReloadOutcome.Failed && failed.Line > 0, $"{failed.Outcome} {failed.Line} {failed.Message}");
            Assert.Equal("Reloaded", Assert.Single(await session.WindowsAsync(Ct)).Title);
        }
        finally
        {
            if (!app.HasExited)
            {
                ProcessTree.Kill(app);
            }

            app.WaitForExit(); // also waits for the redirected output to end
            lock (appOutput)
            {
                output.WriteLine(appOutput.ToString());
            }
        }

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(session.IsConnected);
    }

    /// <summary>
    /// M60b (ADR 0099): the desktop-app tools' commands in the WPF fixture: the window's size and picture, its controls
    /// with text, state and bounds, and input inside the app (text typed into the box, the Add button clicked, a scroll;
    /// a disabled button and a chord with modifiers refused).
    /// </summary>
    [Fact]
    public async Task A_running_wpf_app_lists_its_controls_takes_a_picture_and_takes_input_inside_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF apps run on Windows only (D31).");
        await using var session = LiveSession.Start(WpfFixture.Previewer) ?? throw new InvalidOperationException("No agent.");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += (_, _) => connected.TrySetResult();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(wpf.AppDll)! };
        start.ArgumentList.Add(wpf.AppDll);
        foreach (var (name, value) in session.Environment(null))
        {
            start.Environment[name] = value;
        }

        var appOutput = new System.Text.StringBuilder();
        using var app = Process.Start(start)!;
        app.OutputDataReceived += (_, e) => { lock (appOutput) { appOutput.AppendLine("[app] " + e.Data); } };
        app.ErrorDataReceived += (_, e) => { lock (appOutput) { appOutput.AppendLine("[app] " + e.Data); } };
        app.BeginOutputReadLine();
        app.BeginErrorReadLine();
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var window = Assert.Single(await EventuallyAsync(() => session.WindowsAsync(Ct), w => w.Count > 0, "the app's window"));
            Assert.True(window.Width > 0 && window.Height > 0, $"{window.Width}x{window.Height}");

            // The picture: a PNG of the client area.
            var picture = await session.PictureAsync(window.Id, Ct);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], Convert.FromBase64String(picture.Png)[..4]);
            Assert.True(picture.Width > 0 && picture.Height > 0);

            // The controls: the box, the Add button (disabled while the box is empty), the count; bounds in the window.
            var controls = await session.ControlsAsync(window.Id, Ct);
            var box = Assert.Single(controls, c => c.Name == "NewTitleBox");
            Assert.Equal("TextBox", box.Type);
            Assert.NotNull(box.Bounds);
            var add = Assert.Single(controls, c => c.Type == "Button" && c.Text == "Add");
            Assert.Contains("disabled", add.State);
            Assert.Contains(controls, c => c.Type == "TextBlock" && c.Text == "1 remaining");
            var refused = await Assert.ThrowsAsync<PreviewerException>(() => session.InputAsync(add.Id, "click", "", false, 0, Ct));
            Assert.Contains("disabled", refused.Message, StringComparison.Ordinal);

            // Text typed into the box enables Add; a click adds the item, and the count follows.
            Assert.Contains("Buy milk", await session.InputAsync(box.Id, "text", "Buy milk", replace: true, 0, Ct), StringComparison.Ordinal);
            await EventuallyAsync(() => session.ControlsAsync(window.Id, Ct), c => !c.Single(x => x.Id == add.Id).State.Contains("disabled"), "Add enabled");
            Assert.Contains("invoked", await session.InputAsync(add.Id, "click", "", false, 0, Ct), StringComparison.Ordinal);
            await EventuallyAsync(() => session.ControlsAsync(window.Id, Ct), c => c.Any(x => x.Type == "TextBlock" && x.Text == "2 remaining"), "the count after Add");

            // A key alone is pressed on the control; a chord with modifiers is refused (WPF reads them from the keyboard).
            Assert.StartsWith("Pressed End on TextBox 'NewTitleBox'", await session.InputAsync(box.Id, "keys", "End", false, 0, Ct), StringComparison.Ordinal);
            await Assert.ThrowsAsync<PreviewerException>(() => session.InputAsync(box.Id, "keys", "Ctrl+A", false, 0, Ct));

            // The list scrolls through its scroll viewer.
            var list = Assert.Single(controls, c => c.Type == "ListBox");
            Assert.StartsWith("Scrolled ScrollViewer", await session.InputAsync(list.Id, "scroll", "bottom", false, 0, Ct), StringComparison.Ordinal);
        }
        finally
        {
            if (!app.HasExited)
            {
                ProcessTree.Kill(app);
            }

            app.WaitForExit();
            lock (appOutput)
            {
                output.WriteLine(appOutput.ToString());
            }
        }
    }
}
