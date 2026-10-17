using Codalus.Projects.Host;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// WinForms and WPF projects (M40a, ADR 0071) in the real build host on a copy of samples/WindowsDesktop, on every OS:
/// on Linux and macOS the host sets <c>EnableWindowsTargeting</c>, so they restore, load with their XAML as additional
/// files, and build, and the WPF markup compiler's errors come back with their file and line.
/// </summary>
public sealed class WindowsDesktopTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(4);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-windesktop-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;
    private BuildService _builds = null!;

    private string Fixture => Path.Combine(_root, "WindowsDesktop");

    private string WpfApp => Path.Combine(Fixture, "WpfApp", "WpfApp.csproj");

    private string WinFormsApp => Path.Combine(Fixture, "WinFormsApp", "WinFormsApp.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "WindowsDesktop"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _solutions.RestoreOutput += (_, line) => Console.Error.WriteLine("[restore] " + line);
        _builds = new BuildService(_solutions, _output);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> message)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < s_timeout, "Timed out. " + message());
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void The_build_host_enables_windows_targeting_off_windows_unless_it_is_set()
    {
        var environment = new Dictionary<string, string?>();
        Assert.False(WindowsDesktop.EnableWindowsTargeting(windows: true, n => environment.GetValueOrDefault(n), (n, v) => environment[n] = v));
        Assert.Empty(environment);

        Assert.True(WindowsDesktop.EnableWindowsTargeting(windows: false, n => environment.GetValueOrDefault(n), (n, v) => environment[n] = v));
        Assert.Equal("true", environment["EnableWindowsTargeting"]);

        environment["EnableWindowsTargeting"] = "false"; // the user's own value wins
        Assert.False(WindowsDesktop.EnableWindowsTargeting(windows: false, n => environment.GetValueOrDefault(n), (n, v) => environment[n] = v));
        Assert.Equal("false", environment["EnableWindowsTargeting"]);
    }

    [Fact]
    public void WinUI_design_time_builds_leave_out_the_xaml_compiler_off_windows_only()
    {
        Assert.Null(WindowsDesktop.WinUIDesignTimeTargets(windows: true, _root));
        var targets = WindowsDesktop.WinUIDesignTimeTargets(windows: false, Path.Combine(_root, "targets"));
        Assert.NotNull(targets);
        var text = File.ReadAllText(targets);
        Assert.Contains("<Target Name=\"DesignTimeMarkupCompilation\" />", text, StringComparison.Ordinal);
        Assert.Equal(targets, WindowsDesktop.WinUIDesignTimeTargets(windows: false, Path.Combine(_root, "targets"))); // written once, then reused
    }

    /// <summary>
    /// M44 (ADR 0074): the WinUI 3 fixture restores and loads on every OS (off Windows without the XAML compiler's
    /// design-time pass), says what it is and how it runs (unpackaged, then packaged once switched), and its XAML joins
    /// the design-time command line. On Windows it builds, and a broken x:Bind fails the build with the XAML compiler's
    /// WMC error (which, from dotnet's MSBuild, has lost its message and file: see below).
    /// </summary>
    [Fact]
    public async Task A_winui_project_loads_everywhere_and_says_how_it_runs()
    {
        var project = Path.Combine(Fixture, "WinUIApp", "WinUIApp.csproj");
        await _solutions.OpenAsync(Path.Combine(Fixture, "WinUIApp.slnx"), trusted: true, restore: true, Timeout());
        await WaitUntilAsync(
            () => _solutions.CompilerInputs.TryGetValue(project, out var inputs) && inputs.State == CompilerInputStates.Ok
                  && inputs.Arguments.Any(a => a.Contains("Microsoft.WinUI.dll", StringComparison.OrdinalIgnoreCase)),
            () => $"design-time builds: {string.Join("; ", _solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error}"))}");

        var info = _solutions.Projects[project];
        Assert.True(info.UsesWinUI);
        Assert.False(info.UsesWpf);
        Assert.Equal("None", info.WindowsPackageType);
        Assert.Equal(["net11.0-windows10.0.19041.0"], info.TargetFrameworks);
        Assert.Contains(info.Items, i => i.Kind == "ApplicationDefinition" && i.Path.EndsWith("App.xaml", StringComparison.Ordinal));
        Assert.Contains(info.Items, i => i.Kind == "Page" && i.Path.EndsWith("MainPage.xaml", StringComparison.Ordinal));
        var arguments = _solutions.CompilerInputs[project].Arguments;
        foreach (var xaml in new[] { "App.xaml", "MainPage.xaml", "MainWindow.xaml", Path.Combine("Themes", "Colors.xaml"), Path.Combine("Controls", "RatingControl.xaml") })
        {
            Assert.Contains("/additionalfile:" + Path.Combine(Fixture, "WinUIApp", xaml), arguments);
        }

        var run = await _solutions.GetRunInfoAsync(project, Timeout());
        Assert.Equal("None", run.WindowsPackageType);
        Assert.True(WindowsApps.IsWindowsAppSdk(info));
        Assert.False(WindowsApps.IsPackaged(info.WindowsPackageType));

        if (OperatingSystem.IsWindows())
        {
            // The XAML compiler runs here: the app builds, and its errors come back in their .xaml files.
            var built = await _builds.BuildAsync(BuildTargets.Build, project, Timeout());
            Assert.True(built.Succeeded, _builds.Channel.GetText());
            var page = Path.Combine(Fixture, "WinUIApp", "MainPage.xaml");
            var text = await File.ReadAllTextAsync(page, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(page, text.Replace("{x:Bind ViewModel.Items}", "{x:Bind ViewModel.NoSuchItems}", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            built = await _builds.BuildAsync(BuildTargets.Build, project, Timeout());
            Assert.False(built.Succeeded);
            // On dotnet's MSBuild the Windows App SDK's XAML compiler is XamlCompiler.exe, which can't load its own error
            // messages (it embeds them as Microsoft.Windows.UI.Xaml.Build.Tasks.ErrorMessages.resources and looks them
            // up as Microsoft.UI.Xaml.Markup.Compiler.ErrorMessages): every error, the x:Bind path's WMC1110 too, comes
            // back as WMC9999 for that exception, without its file and line, on the compiler task's line in the
            // package's targets. So the build fails with it and no squiggle; the editor's own x:Bind check marks the
            // path. TODO(issue): back to the error in MainPage.xaml once a Windows App SDK's XamlCompiler.exe finds its
            // messages (1.8.260528001 through 1.8.260803003 and 2.3.9 don't).
            var error = Assert.Single(_builds.Diagnostics, d => d.Code?.StartsWith("WMC", StringComparison.Ordinal) == true);
            var where = $"{error.Code} at {error.File}({error.Line},{error.Column}): {error.Message}\n{_builds.Channel.GetText()}";
            Assert.True(error.Code == "WMC9999" && error.Message.Contains("Microsoft.UI.Xaml.Markup.Compiler.ErrorMessages.resources", StringComparison.Ordinal), where);
            Assert.EndsWith("Microsoft.UI.Xaml.Markup.Compiler.interop.targets", error.File, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(XamlBuildDiagnostics.ByFile(_builds.Diagnostics), f => string.Equals(f.Key, page, StringComparison.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(page, text, TestContext.Current.CancellationToken);
        }

        // The packaged variant: MSIX, with the manifest its build writes.
        var csproj = await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(project, csproj.Replace("<OutputType>WinExe</OutputType>", "<OutputType>WinExe</OutputType>\n    <WinUIAppPackaged>true</WinUIAppPackaged>", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _solutions.Projects[project].WindowsPackageType == "MSIX", () => $"WindowsPackageType: {_solutions.Projects[project].WindowsPackageType}");
        run = await _solutions.GetRunInfoAsync(project, Timeout());
        Assert.Equal("MSIX", run.WindowsPackageType);
        Assert.EndsWith("AppxManifest.xml", run.AppxManifest, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(run.AppxManifest!));
    }

    [Fact]
    public void Wpf_xaml_joins_the_design_time_command_line_once()
    {
        var page = Path.Combine(_root, "MainWindow.xaml");
        string[] arguments = ["/noconfig", "/additionalfile:" + Path.Combine(_root, "App.xaml"), "Program.cs"];
        var result = WindowsDesktop.WithWpfXaml(arguments, [page, Path.Combine(_root, "App.xaml"), Path.Combine(_root, "Readme.md"), page]);
        Assert.Equal([.. arguments, "/additionalfile:" + page], result);
        Assert.Same(arguments, WindowsDesktop.WithWpfXaml(arguments, []));
    }

    [Fact]
    public async Task Wpf_and_winforms_projects_load_build_and_report_markup_errors_in_their_files()
    {
        var restored = new List<string>();
        _solutions.RestoreStateChanged += (_, e) =>
        {
            lock (restored)
            {
                restored.Add(e.Message is null ? e.State : $"{e.State}: {e.Message}");
            }
        };

        var solution = await _solutions.OpenAsync(Path.Combine(Fixture, "WindowsDesktop.slnx"), trusted: true, restore: true, Timeout());
        await WaitUntilAsync(
            () => _solutions.CompilerInputs.Count(i => i.Value.State == CompilerInputStates.Ok) == solution.Projects.Length
                  && _solutions.Projects.Count(p => p.Value.Restored && (p.Value.IsTestProject || !p.Key.Contains(".Tests", StringComparison.Ordinal))) == solution.Projects.Length
                  && _solutions.CompilerInputs.TryGetValue(WpfApp, out var app) && app.Arguments.Any(a => a.Contains("PresentationFramework.dll", StringComparison.OrdinalIgnoreCase))
                  && _solutions.CompilerInputs.TryGetValue(WinFormsApp, out var forms) && forms.Arguments.Any(a => a.Contains("System.Windows.Forms.dll", StringComparison.OrdinalIgnoreCase)),
            () => $"restore: {string.Join(", ", restored)}; design-time builds: {string.Join("; ", _solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error}"))}");

        // The projects say what they are.
        var wpf = _solutions.Projects[WpfApp];
        Assert.True(wpf.UsesWpf);
        Assert.False(wpf.UsesWindowsForms);
        Assert.Equal(["net11.0-windows"], wpf.TargetFrameworks);
        Assert.Equal("WinExe", wpf.OutputType);
        Assert.Contains(wpf.Items, i => i.Kind == "ApplicationDefinition" && i.Path.EndsWith("App.xaml", StringComparison.Ordinal));
        Assert.Contains(wpf.Items, i => i.Kind == "Page" && i.Path.EndsWith("MainWindow.xaml", StringComparison.Ordinal));
        Assert.True(_solutions.Projects[WinFormsApp].UsesWindowsForms);
        Assert.True(_solutions.Projects[Path.Combine(Fixture, "WpfApp.Tests", "WpfApp.Tests.csproj")].IsTestProject);
        Assert.True(_solutions.Projects[Path.Combine(Fixture, "WinFormsApp.Tests", "WinFormsApp.Tests.csproj")].IsTestProject);

        // The design-time build: WPF's references, its XAML as additional files, and the markup compiler's code.
        var inputs = _solutions.CompilerInputs[WpfApp].Arguments;
        Assert.Contains(inputs, a => a.Contains("PresentationFramework.dll", StringComparison.OrdinalIgnoreCase));
        foreach (var xaml in new[] { "App.xaml", "MainWindow.xaml", Path.Combine("Themes", "Styles.xaml"), Path.Combine("Controls", "RatingControl.xaml") })
        {
            Assert.Contains("/additionalfile:" + Path.Combine(Fixture, "WpfApp", xaml), inputs);
        }

        Assert.Contains(inputs, a => a.Contains("MainWindow.g.", StringComparison.Ordinal));
        Assert.Contains(_solutions.CompilerInputs[WinFormsApp].Arguments, a => a.Contains("System.Windows.Forms.dll", StringComparison.OrdinalIgnoreCase));

        // The whole solution builds here (Linux and macOS through EnableWindowsTargeting).
        var result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        Assert.True(File.Exists(Path.Combine(Fixture, "WpfApp", "bin", "Debug", "net11.0-windows", "WpfApp.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "WinFormsApp", "bin", "Debug", "net11.0-windows", "WinFormsApp.dll")));

        // A markup compiler error (MC…) lands in its .xaml file at its line, as an editor diagnostic.
        var window = Path.Combine(Fixture, "WpfApp", "MainWindow.xaml");
        var text = await File.ReadAllTextAsync(window, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(window, text.Replace("<ListBox Grid.Row=\"1\"", "<ListBox Bogus=\"1\" Grid.Row=\"1\"", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        result = await _builds.BuildAsync(BuildTargets.Build, WpfApp, Timeout());
        Assert.False(result.Succeeded);
        var error = Assert.Single(_builds.Diagnostics, d => d.Code?.StartsWith("MC", StringComparison.Ordinal) == true);
        Assert.Equal(window, error.File, ignoreCase: OperatingSystem.IsWindows());
        var line = text[..text.IndexOf("<ListBox Grid.Row", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        Assert.Equal(line, error.Line);
        var squiggles = XamlBuildDiagnostics.ByFile(_builds.Diagnostics);
        var squiggle = Assert.Single(Assert.Single(squiggles, f => string.Equals(f.Key, window, StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal(line - 1, squiggle.Range.Start.Line);
        Assert.StartsWith("MC", squiggle.Code, StringComparison.Ordinal);
    }
}
