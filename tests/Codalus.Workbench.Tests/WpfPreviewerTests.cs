using System.Diagnostics;
using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>
/// samples/WindowsDesktop's WPF app copied to a temp folder and built once for the test class, on Windows only (D31:
/// WPF runs only there; elsewhere the tests skip and this does nothing).
/// </summary>
public sealed class WpfFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-wpf-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "WindowsDesktop");

    public string App => Path.Combine(Folder, "WpfApp");

    public string AppDll { get; private set; } = string.Empty;

    /// <summary>The WPF previewer built with the repository (its designer host and live agent).</summary>
    public static string Previewer => Path.Combine(DiagnosticsFixture.Metadata("CodalusRepoRoot"), "src", "Codalus.Previewer.Wpf", "bin",
        DiagnosticsFixture.Metadata("CodalusConfiguration"), "net8.0-windows", PreviewService.WpfPreviewerFileName);

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(File.Exists(Previewer), "Build the solution first: " + Previewer);
        CopyDirectory(Path.Combine(DiagnosticsFixture.Metadata("CodalusRepoRoot"), "samples", "WindowsDesktop"), Folder);
        Codalus.Tests.FixtureSdk.Pin(_root);
        var (code, output) = await AvaloniaFixture.DotnetAsync("build", Path.Combine(App, "WpfApp.csproj"), "-nologo", "-v", "quiet");
        Assert.True(code == 0, output);
        AppDll = (await Codalus.Tests.MSBuildQuery.RunAsync(Path.Combine(App, "WpfApp.csproj"), ["TargetPath"])).Property("TargetPath");
        Assert.True(File.Exists(AppDll), AppDll);
    }

    /// <summary>How the IDE starts the WPF previewer for the app (<see cref="PreviewService.LaunchFor"/>).</summary>
    public PreviewerLaunch Launch => PreviewService.LaunchFor(
        new PreviewTarget(Path.Combine(App, "MainWindow.xaml"), Path.Combine(App, "WpfApp.csproj"), Path.Combine(App, "WpfApp.csproj"), AppDll, AppDll, Previewer, "/MainWindow.xaml") { Dialect = PreviewDialect.Wpf },
        null);

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>
/// M40b (ADR 0072), Windows only: the WPF previewer's designer host on the fixture's own build output, driven by the
/// IDE's <see cref="PreviewerSession"/> over Avalonia's remote protocol: the window at its design size with its
/// design-time data, edits answered by new frames, the loader's errors with their positions and recovery, the user
/// control at its design size, a resource dictionary and App.xaml, hit testing and bounds through the XAML's paths,
/// and the render scale.
/// </summary>
public sealed class WpfPreviewerTests(WpfFixture wpf, ITestOutputHelper output) : IClassFixture<WpfFixture>
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "The WPF previewer runs on Windows only (D31).");

    private string Xaml(params string[] parts) => File.ReadAllText(System.IO.Path.Combine([wpf.App, .. parts]));

    private async Task<PreviewerSession> StartAsync(double scale = 1)
    {
        var session = await PreviewerSession.StartAsync(wpf.Launch, scale, s_timeout, Ct);
        session.Log += (_, line) => output.WriteLine("[designer] " + line);
        return session;
    }

    private async Task<PreviewUpdateResult> ShowAsync(PreviewerSession session, string xaml, string path = "/MainWindow.xaml")
    {
        var result = await session.UpdateXamlAsync(xaml, wpf.AppDll, path, s_timeout, Ct);
        output.WriteLine($"{path}: {(result.Succeeded ? "ok" : result.Error)} in {result.Elapsed.TotalMilliseconds:0} ms");
        return result;
    }

    private static PreviewPathStep[] Path(params string[] steps) =>
        [.. steps.Select(s => s.Split(':')).Select(p => new PreviewPathStep(p[0], p.Length > 1 ? int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) : 0))];

    private static readonly PreviewPathStep[] s_remaining = Path("MainWindow", "Grid", "StackPanel", "TextBlock");

    [Fact]
    public async Task The_window_renders_at_its_design_size_with_design_data_follows_edits_and_reports_errors()
    {
        RequireWindows();
        var xaml = Xaml("MainWindow.xaml");
        var started = Stopwatch.StartNew();
        await using var session = await StartAsync();
        var first = await ShowAsync(session, xaml);
        Assert.True(first.Succeeded, first.Error + "\n" + session.RecentOutput);
        output.WriteLine($"first frame in {started.Elapsed.TotalSeconds:0.0} s");
        Assert.NotNull(first.Frame);
        Assert.Equal((480, 360), (first.Frame!.Width, first.Frame.Height)); // d:DesignWidth, d:DesignHeight
        Assert.Equal(1, first.Frame.Scale);
        Assert.Equal(PreviewPixelFormat.Rgba8888, first.Frame.Format); // the IDE asks for RGBA
        Assert.Contains(first.Frame.Data.Chunk(4), p => p[0] != p[1] || p[1] != p[2]); // the accent colour, not just greys

        // d:DesignInstance (IsDesignTimeCreatable) is the data context: "1 remaining" has a width.
        var remaining = await session.BoundsAsync(s_remaining, Ct);
        Assert.True(remaining is { Width: > 0 }, "the design-time view model's RemainingCount shows: " + remaining);

        // Each edit is answered by a frame that shows it.
        var edited = await ShowAsync(session, xaml.Replace("Content=\"Add\"", "Content=\"Add a to-do item to the list\"", StringComparison.Ordinal));
        Assert.True(edited.Succeeded, edited.Error);
        Assert.False(edited.Frame!.Data.AsSpan().SequenceEqual(first.Frame.Data), "the edit changed the frame");

        // Not creatable at design time: no design-time data, so the text is empty.
        var plain = await ShowAsync(session, xaml.Replace("IsDesignTimeCreatable=True", "IsDesignTimeCreatable=False", StringComparison.Ordinal));
        Assert.True(plain.Succeeded, plain.Error);
        Assert.True(await session.BoundsAsync(s_remaining, Ct) is { Width: 0 } or null);

        // An unknown property: the loader's error with its line; the last good frame stays.
        var lines = xaml.Split('\n');
        var buttonLine = Array.FindIndex(lines, l => l.Contains("Content=\"Add\"", StringComparison.Ordinal)) + 1;
        var unknown = await ShowAsync(session, xaml.Replace("Content=\"Add\"", "Content=\"Add\" Colour=\"Red\"", StringComparison.Ordinal));
        Assert.False(unknown.Succeeded);
        Assert.Contains("Colour", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(buttonLine, unknown.Line);

        // Malformed XAML: the XML reader's position.
        var malformed = await ShowAsync(session, xaml.Replace("</Grid>", "</Grd>", StringComparison.Ordinal));
        Assert.False(malformed.Succeeded);
        Assert.NotNull(malformed.Line);

        // And back.
        var recovered = await ShowAsync(session, xaml);
        Assert.True(recovered.Succeeded, recovered.Error);
        Assert.True(recovered.Frame!.Data.AsSpan().SequenceEqual(first.Frame.Data), "the same XAML draws the same frame");
    }

    [Fact]
    public async Task The_user_control_a_resource_dictionary_and_app_xaml_render()
    {
        RequireWindows();
        await using var session = await StartAsync();
        var control = await ShowAsync(session, Xaml("Controls", "RatingControl.xaml"), "/Controls/RatingControl.xaml");
        Assert.True(control.Succeeded, control.Error + "\n" + session.RecentOutput);
        Assert.Equal((120, 24), (control.Frame!.Width, control.Frame.Height));
        Assert.True(await session.BoundsAsync(Path("RatingControl", "StackPanel", "TextBlock:2"), Ct) is { Width: >= 0 });

        // App.xaml's resources (merged dictionaries) are loaded: the styles file previews, and so does App.xaml.
        var styles = await ShowAsync(session, Xaml("Themes", "Styles.xaml"), "/Themes/Styles.xaml");
        Assert.True(styles.Succeeded, styles.Error);
        Assert.NotNull(styles.Frame);
        var app = await ShowAsync(session, Xaml("App.xaml"), "/App.xaml");
        Assert.True(app.Succeeded, app.Error);
        Assert.NotNull(app.Frame);
    }

    [Fact]
    public async Task Hits_and_bounds_follow_the_xamls_paths_both_ways()
    {
        RequireWindows();
        await using var session = await StartAsync();
        Assert.True((await ShowAsync(session, Xaml("MainWindow.xaml"))).Succeeded);

        // XAML → preview: the button's bounds through its path from the root (named by x:Class).
        var button = await session.BoundsAsync(Path("MainWindow", "Grid", "DockPanel", "Button"), Ct);
        Assert.True(button is { Width: > 0, Height: > 0 }, "the button's bounds: " + button);
        Assert.True(button!.Value.X > 240, "the button is docked right: " + button);

        // Preview → XAML: a click on the button (its template's text) lands on the button the XAML declares.
        var hit = await session.HitTestAsync(button.Value.X + button.Value.Width / 2, button.Value.Y + button.Value.Height / 2, Ct);
        Assert.NotNull(hit);
        Assert.Equal(["MainWindow", "Grid", "DockPanel", "Button"], hit!.Path.Select(s => s.Type));
        Assert.Equal(button.Value, hit.Bounds);

        // The named text box, and an element that isn't there.
        var box = await session.BoundsAsync(Path("MainWindow", "Grid", "DockPanel", "TextBox"), Ct);
        Assert.True(box is { Width: > 0 });
        hit = await session.HitTestAsync(box!.Value.X + 4, box.Value.Y + box.Value.Height / 2, Ct);
        Assert.Equal("NewTitleBox", hit!.Path[^1].Name);
        Assert.Null(await session.BoundsAsync(Path("MainWindow", "Grid", "DockPanel", "Button:3"), Ct));

        // The list (its items are its template's): a hit in an item is in the ListBox the XAML declares.
        var list = await session.BoundsAsync(Path("MainWindow", "Grid", "ListBox"), Ct);
        hit = await session.HitTestAsync(list!.Value.X + 20, list.Value.Y + 10, Ct);
        Assert.Equal(["MainWindow", "Grid", "ListBox"], hit!.Path.Take(3).Select(s => s.Type));
    }

    [Fact]
    public async Task The_render_scale_multiplies_the_frame()
    {
        RequireWindows();
        await using var session = await StartAsync(scale: 2);
        var result = await ShowAsync(session, Xaml("MainWindow.xaml"));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((960, 720, 2d), (result.Frame!.Width, result.Frame.Height, result.Frame.Scale));

        await session.SetScaleAsync(1, Ct);
        result = await ShowAsync(session, Xaml("MainWindow.xaml"));
        Assert.Equal((480, 360), (result.Frame!.Width, result.Frame.Height));
    }
}
