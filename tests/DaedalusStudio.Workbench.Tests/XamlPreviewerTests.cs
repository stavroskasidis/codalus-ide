using System.Diagnostics;
using System.Text.Json;
using DaedalusStudio.Workbench.Previewer;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>samples/Avalonia copied to a temp folder and built once for the test class.</summary>
public sealed class AvaloniaFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-avalonia-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "Avalonia");

    public string App => Path.Combine(Folder, "AvaloniaApp");

    public string AppDll { get; private set; } = string.Empty;

    public string ControlsDll { get; private set; } = string.Empty;

    public string HostApp { get; private set; } = string.Empty;

    public TimeSpan BuildTime { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var repo = DiagnosticsFixture.Metadata("DaedalusRepoRoot");
        Copy(Path.Combine(repo, "samples", "Avalonia"), Folder);
        File.Copy(Path.Combine(repo, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        File.Copy(Path.Combine(repo, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        DaedalusStudio.Tests.FixtureSdk.Pin(_root);
        var watch = Stopwatch.StartNew();
        var (code, output) = await DotnetAsync("build", Path.Combine(App, "AvaloniaApp.csproj"), "-nologo", "-v", "quiet");
        BuildTime = watch.Elapsed;
        Assert.True(code == 0, output);

        (code, output) = await DotnetAsync("msbuild", Path.Combine(App, "AvaloniaApp.csproj"), "-nologo", "-getProperty:TargetPath", "-getProperty:AvaloniaPreviewerNetCoreToolPath");
        Assert.True(code == 0, output);
        using var properties = JsonDocument.Parse(output);
        AppDll = properties.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
        HostApp = Path.GetFullPath(properties.RootElement.GetProperty("Properties").GetProperty("AvaloniaPreviewerNetCoreToolPath").GetString()!);
        ControlsDll = Path.Combine(Path.GetDirectoryName(AppDll)!, "AvaloniaControls.dll");
    }

    public static async Task<(int Code, string Output)> DotnetAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    public PreviewerLaunch Launch => new(HostApp, AppDll, PreviewService.ResolveAgent());

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>
/// M19a (ADR 0048): the real designer host (Avalonia's <c>Avalonia.Designer.HostApp</c>) running samples/Avalonia,
/// driven over the remote protocol by <see cref="PreviewerSession"/>, with the previewer agent loaded: frames for a
/// window, a user control and a library's resource dictionary, edits, errors with positions, scale, theme variants,
/// hit testing and bounds. The budgets (edit to preview p95 ≤ 300 ms, build to first frame ≤ 3 s) are asserted off CI
/// and reported on CI.
/// </summary>
public sealed class XamlPreviewerTests(AvaloniaFixture avalonia, ITestOutputHelper output) : IClassFixture<AvaloniaFixture>
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool OnCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private string MainWindow => Path.Combine(avalonia.App, "Views", "MainWindow.axaml");

    private async Task<PreviewerSession> StartAsync(double scale = 1)
    {
        var session = await PreviewerSession.StartAsync(avalonia.Launch, scale, s_timeout, Ct);
        session.Log += (_, line) => output.WriteLine("[designer] " + line);
        return session;
    }

    private static int Differences(PreviewFrame a, PreviewFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return int.MaxValue;
        }

        var count = 0;
        for (var i = 0; i < Math.Min(a.Data.Length, b.Data.Length); i += 4)
        {
            if (a.Data[i] != b.Data[i] || a.Data[i + 1] != b.Data[i + 1] || a.Data[i + 2] != b.Data[i + 2])
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task A_window_renders_updates_on_edit_and_reports_errors()
    {
        var xaml = await File.ReadAllTextAsync(MainWindow, Ct);
        var started = Stopwatch.StartNew();
        await using var session = await StartAsync();
        var first = await session.UpdateXamlAsync(xaml, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        var firstFrame = started.Elapsed;
        Assert.True(first.Succeeded, first.Error + "\n" + session.RecentOutput);
        var frame = Assert.IsType<PreviewFrame>(first.Frame);
        Assert.Equal((480, 360), (frame.Width, frame.Height)); // d:DesignWidth and d:DesignHeight
        Assert.Equal(frame.Width * 4, frame.Stride);
        Assert.Contains(frame.Data, b => b != 0);

        // Edits: the greeting's binding replaced by text, then sizes; each answered by a frame that differs.
        var times = new List<double>();
        var previous = frame;
        for (var i = 0; i < 12; i++)
        {
            var edited = xaml.Replace("Text=\"{Binding Greeting}\"", $"Text=\"Edit number {i} {new string('W', i)}\"", StringComparison.Ordinal);
            var update = await session.UpdateXamlAsync(edited, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
            Assert.True(update.Succeeded, update.Error);
            Assert.NotNull(update.Frame);
            Assert.True(Differences(previous, update.Frame) > 0, $"Edit {i} didn't change the preview.");
            previous = update.Frame;
            times.Add(update.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        output.WriteLine($"Build: {avalonia.BuildTime.TotalMilliseconds:0} ms; start to first frame: {firstFrame.TotalMilliseconds:0} ms; edit to frame p50 {times[times.Count / 2]:0} ms, p95 {p95:0} ms");
        if (!OnCi)
        {
            Assert.True(p95 <= 300, $"Edit to preview p95 was {p95:0} ms (budget 300 ms).");
            Assert.True(firstFrame <= TimeSpan.FromSeconds(3), $"The designer took {firstFrame.TotalMilliseconds:0} ms to its first frame (budget 3 s).");
        }

        // An error: the loader's message and its position, and the previous frame stays.
        var broken = xaml.Replace("<ListBox x:Name", "<ListBox Orientation=\"Sideways\" x:Name", StringComparison.Ordinal);
        var error = await session.UpdateXamlAsync(broken, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        Assert.False(error.Succeeded);
        Assert.NotNull(error.Line);
        output.WriteLine($"Error: {error.Error} at {error.Line}:{error.Column}");
        var malformed = await session.UpdateXamlAsync(xaml.Replace("</DockPanel>", string.Empty, StringComparison.Ordinal), avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        Assert.False(malformed.Succeeded);
        Assert.False(session.HasExited);

        // And back.
        var again = await session.UpdateXamlAsync(xaml, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        Assert.True(again.Succeeded, again.Error);
    }

    [Fact]
    public async Task User_controls_and_library_resources_render_with_design_time_data()
    {
        await using var session = await StartAsync();
        var item = await session.UpdateXamlAsync(await File.ReadAllTextAsync(Path.Combine(avalonia.App, "Views", "TodoItemView.axaml"), Ct), avalonia.AppDll, "/Views/TodoItemView.axaml", s_timeout, Ct);
        Assert.True(item.Succeeded, item.Error + "\n" + session.RecentOutput);
        Assert.Equal((240, 32), (item.Frame!.Width, item.Frame.Height));

        // The library's resource dictionary, previewed through the app (Design.PreviewWith), with its own assembly.
        var generic = await session.UpdateXamlAsync(await File.ReadAllTextAsync(Path.Combine(avalonia.Folder, "AvaloniaControls", "Themes", "Generic.axaml"), Ct), avalonia.ControlsDll, "/Themes/Generic.axaml", s_timeout, Ct);
        Assert.True(generic.Succeeded, generic.Error + "\n" + session.RecentOutput);
        Assert.True(generic.Frame!.Width > 0);

        // Styles with Design.PreviewWith.
        var styles = await session.UpdateXamlAsync(await File.ReadAllTextAsync(Path.Combine(avalonia.App, "Styles", "AppStyles.axaml"), Ct), avalonia.AppDll, "/Styles/AppStyles.axaml", s_timeout, Ct);
        Assert.True(styles.Succeeded, styles.Error + "\n" + session.RecentOutput);
    }

    [Fact]
    public async Task The_agent_hit_tests_finds_bounds_and_switches_themes_and_scale()
    {
        var xaml = await File.ReadAllTextAsync(MainWindow, Ct);
        await using var session = await StartAsync();
        var shown = await session.UpdateXamlAsync(xaml, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        Assert.True(shown.Succeeded, shown.Error);

        // The button's bounds through its XAML path, then a hit at its centre comes back to it.
        PreviewPathStep[] button = [new("MainWindow", 0), new("DockPanel", 0), new("StackPanel", 0), new("Button", 0)];
        var bounds = await session.BoundsAsync(button, Ct);
        Assert.True(bounds is { Width: > 0, Height: > 0 }, $"No bounds for the button.\n{session.RecentOutput}");
        var hit = await session.HitTestAsync(bounds.Value.X + bounds.Value.Width / 2, bounds.Value.Y + bounds.Value.Height / 2, Ct);
        Assert.NotNull(hit);
        output.WriteLine(string.Join(" / ", hit.Path.Select(s => $"{s.Type}:{s.Ordinal}:{s.Name}")));
        Assert.Equal("MainWindow", hit.Path[0].Type);
        // The button's text (an AccessText its presenter made for the string content) is in its logical tree too.
        Assert.Contains(new PreviewPathStep("Button", 0, "IncrementButton"), hit.Path);
        Assert.True(hit.Bounds.X >= bounds.Value.X && hit.Bounds.X + hit.Bounds.Width <= bounds.Value.X + bounds.Value.Width + 0.5, $"{hit.Bounds} isn't inside {bounds}");

        // The text block at the top.
        var top = await session.HitTestAsync(20, 20, Ct);
        Assert.Equal("GreetingText", top?.Path[^1].Name);

        // The dark theme changes the pixels; the light one changes them back.
        var light = session.LastFrame!;
        var frames = new List<PreviewFrame>();
        session.FrameReceived += (_, f) => frames.Add(f);
        Assert.True(await session.SetThemeAsync("Dark", Ct));
        await WaitAsync(() => frames.Count > 0 && Differences(light, frames[^1]) > 1000, "a dark frame");
        Assert.True(await session.SetThemeAsync("Light", Ct));
        await WaitAsync(() => Differences(light, frames[^1]) < 100, "the light frame again");

        // Scale 2: twice the pixels.
        await session.SetScaleAsync(2, Ct);
        var doubled = await session.UpdateXamlAsync(xaml, avalonia.AppDll, "/Views/MainWindow.axaml", s_timeout, Ct);
        Assert.True(doubled.Succeeded, doubled.Error);
        Assert.Equal((960, 720), (doubled.Frame!.Width, doubled.Frame.Height));
        Assert.Equal(2, doubled.Frame.Scale);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < s_timeout, $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }
}
