using System.Diagnostics;
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
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
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
}
