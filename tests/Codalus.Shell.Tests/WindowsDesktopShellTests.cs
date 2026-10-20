using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>
/// M40a (ADR 0071, D31) in the headless shell on samples/WindowsDesktop: WPF and Windows Forms projects open, edit and
/// build on every OS (the build host sets EnableWindowsTargeting off Windows), with their XAML served by the C# host,
/// their files nested and their Add New Item templates; on Linux and macOS, starting or testing them says they're
/// Windows only. On Windows, the WPF app runs, debugs, and both test projects run.
/// </summary>
public sealed class WindowsDesktopShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync(bool tests = false)
    {
        var f = tests ? ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest")) : ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WindowsDesktop.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        return (f, folder);
    }

    private static string WpfApp(string folder) => Path.Combine(folder, "WpfApp", "WpfApp.csproj");

    private static string WinFormsApp(string folder) => Path.Combine(folder, "WinFormsApp", "WinFormsApp.csproj");

    private static ExplorerNode? Find(ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [AvaloniaFact]
    public async Task Wpf_and_windows_forms_projects_open_with_their_xaml_nesting_and_templates()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var wpf = f.Shell.Solutions.Projects[WpfApp(folder)];
        Assert.True(wpf.UsesWpf);
        Assert.True(f.Shell.Solutions.Projects[WinFormsApp(folder)].UsesWindowsForms);
        Assert.True(WindowsDesktopProjects.RunsOnlyOnWindows(wpf));

        // The code-behind, the designer file and the form's resources nest under their files.
        var explorer = f.Shell.Explorer;
        var window = Path.Combine(folder, "WpfApp", "MainWindow.xaml");
        var form = Path.Combine(folder, "WinFormsApp", "MainForm.cs");
        await ShellFixture.PumpUntil(() => explorer.Root is { } root && Find(root, "f:" + window) is { } node && node.Children.Any(c => c.Id == "f:" + window + ".cs"), seconds: 60);
        await ShellFixture.PumpUntil(() => Find(explorer.Root!, "f:" + form) is { } node
            && node.Children.Any(c => c.Name == "MainForm.Designer.cs") && node.Children.Any(c => c.Name == "MainForm.resx"), seconds: 60);
        var resources = Find(explorer.Root!, "f:" + Path.Combine(folder, "WinFormsApp", "Properties", "Resources.resx"));
        Assert.Contains(resources!.Children, c => c.Name == "Resources.Designer.cs");

        // .xaml is XAML for the C# host, with WPF's binding paths from d:DataContext.
        var editor = await f.Shell.OpenFileAsync(window, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        Assert.Equal(ShellHost.XamlLanguageId, editor!.Model.LanguageId);
        var position = editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("Text=\"{Binding NewTitle,", StringComparison.Ordinal) + "Text=\"{Binding ".Length);
        await ShellFixture.RetryUntil(
            "the WPF binding path {Binding NewTitle} to complete the view model's AddCommand",
            async token => await f.Shell.Languages.ClientAsync(ShellHost.XamlLanguageId) is { } client ? await client.CompletionAsync(editor.Model.Uri, position, null, token) : null,
            completion => completion?.Items.Any(i => i.Label == "AddCommand") == true,
            ShellFixture.Describe);

        // Off Windows, Start says the app is Windows only, and builds nothing.
        f.Shell.SetStartupProject(WpfApp(folder));
        if (!OperatingSystem.IsWindows())
        {
            var builds = f.Shell.Builds.LastBuild;
            await f.Shell.StartDebuggingAsync();
            await f.Shell.StartWithoutDebuggingAsync();
            Assert.Equal(2, f.Shell.Workbench.Notifications.Items.Count(n => n.Message.Contains("WpfApp is a WPF project: it runs, debugs and tests only on Windows", StringComparison.Ordinal)));
            Assert.Same(builds, f.Shell.Builds.LastBuild);
            Assert.Empty(f.Shell.Runs.Running);
        }

        // Add New Item: WPF's items in the WPF app, a window with its code-behind; Windows Forms' in the other.
        var add = f.Shell.OpenAddNewItem(WpfApp(folder), Path.Combine(folder, "WpfApp"))!;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AddNewItemView>().Any());
        await ShellFixture.PumpUntil(() => add.Templates.Any(t => t.Template.ShortName == "wpfwindow"), seconds: 60);
        Assert.Contains(add.Templates, t => t.Template.ShortName == "wpfusercontrol");
        Assert.Contains(add.Templates, t => t.Template.ShortName == "wpfresourcedictionary");
        Assert.DoesNotContain(add.Templates, t => t.Template.ShortName == "winformsform");
        add.Selected = add.Templates.First(t => t.Template.ShortName == "wpfwindow");
        add.Name = "AboutWindow";
        Assert.Contains("x:Class=\"WpfApp.AboutWindow\"", add.Preview, StringComparison.Ordinal);
        Assert.Contains("// AboutWindow.xaml.cs", add.Preview, StringComparison.Ordinal);
        var created = await add.AddAsync();
        Assert.Equal(Path.Combine(folder, "WpfApp", "AboutWindow.xaml"), created);
        Assert.Contains("public partial class AboutWindow : Window", File.ReadAllText(created + ".cs"), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Codalus.Shell.Editor.CodeEditorDocument { Title: "AboutWindow.xaml" });
        await ShellFixture.PumpUntil(() => Find(explorer.Root!, "f:" + created) is { } node && node.Children.Any(c => c.Name == "AboutWindow.xaml.cs"), seconds: 60);

        add = f.Shell.OpenAddNewItem(WinFormsApp(folder), Path.Combine(folder, "WinFormsApp"))!;
        await ShellFixture.PumpUntil(() => add.Templates.Any(t => t.Template.ShortName == "winformsform"), seconds: 60);
        Assert.DoesNotContain(add.Templates, t => t.Template.ShortName == "wpfwindow");
        add.Selected = add.Templates.First(t => t.Template.ShortName == "winformsform");
        add.Name = "SettingsForm";
        created = await add.AddAsync();
        Assert.Equal(Path.Combine(folder, "WinFormsApp", "SettingsForm.cs"), created);
        Assert.Contains("private void InitializeComponent()", File.ReadAllText(Path.Combine(folder, "WinFormsApp", "SettingsForm.Designer.cs")), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => Find(explorer.Root!, "f:" + created) is { } node && node.Children.Any(c => c.Name == "SettingsForm.Designer.cs"), seconds: 60);

        // The new items build with their projects, on every OS.
        Assert.True(await f.Shell.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, null), f.Shell.Builds.Channel.GetText());
    }

    [AvaloniaFact]
    public async Task Off_windows_the_windows_test_projects_say_so_instead_of_running()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows they run (Windows_runs_debugs_and_tests_the_desktop_apps).");
        var (f, _) = await OpenAsync(tests: true);
        await using var _ = f;
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Any(r => r.Label == "WpfApp.Tests" && r.Error is not null)), seconds: 180);
        Assert.Contains("its tests run only on Windows", f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "WinFormsApp.Tests").Error), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Windows_runs_debugs_and_tests_the_desktop_apps()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinForms and WPF apps run on Windows only (D31).");
        var (f, folder) = await OpenAsync(tests: true);
        await using var _ = f;

        // Start Without Debugging: the WPF app runs until it's stopped.
        f.Shell.SetStartupProject(WpfApp(folder));
        await f.Shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(p => p.Name == "WpfApp" && !p.HasExited), seconds: 240);
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        Assert.Contains(f.Shell.Runs.Running, p => p.Name == "WpfApp" && !p.HasExited); // a window, still up
        f.Shell.StopRunning();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);

        // Hot Reload: dotnet watch runs it.
        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 240);
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram?.Channel?.GetText().Contains("dotnet watch", StringComparison.OrdinalIgnoreCase) == true, seconds: 240);
        f.Shell.StopRunning();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);

        // F5 (netcoredbg: WPF apps are CoreCLR apps): a breakpoint in the view model's constructor.
        if (DebugAdapterLocator.Resolve() is not null || Environment.GetEnvironmentVariable("CI") == "true")
        {
            var model = Path.Combine(folder, "WpfApp", "ViewModels", "MainViewModel.cs");
            var line = Array.FindIndex(File.ReadAllLines(model), l => l.Contains("Items.Add(new TodoItem(\"Open the WPF fixture\"))", StringComparison.Ordinal)) + 1;
            f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(model, line));
            await f.RunAsync("debug.start");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 240, describe: () => DebugStates(f));
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == line, describe: () => DebugStates(f));
            await ShellFixture.PumpUntil(() => f.Shell.CallStack.Frames.Count > 0, describe: () => DebugStates(f));
            Assert.Contains("MainViewModel", f.Shell.CallStack.Frames[0].Name, StringComparison.Ordinal);
            f.Shell.Debugger.Breakpoints.Clear();
            await f.RunAsync("debug.stop");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        }

        // Both net11.0-windows test projects run.
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Count(r => r.Label is "WpfApp.Tests" or "WinFormsApp.Tests")) == 2, seconds: 180);
        await f.Shell.RunTestsAsync(TestRunKind.Run, null);
        try
        {
            await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning && f.Shell.Tests.Read(roots => roots.Where(r => r.Label is "WpfApp.Tests" or "WinFormsApp.Tests")
                .SelectMany(r => r.Leaves()).Count(l => l.Outcome == TestOutcome.Passed)) == 4, seconds: 300);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run's outcome, output and build in the message: the first Windows run timed out with nothing else to go on.
            var build = f.Shell.Builds.Channel.GetText();
            Assert.Fail($"{ex.Message} Running: {f.Shell.Tests.IsRunning}. Tests: {TestStates(f)}. Runs: "
                        + string.Join(" || ", f.Shell.Tests.Runs.Select(r => $"{r.Name} ({r.Results.Count} results): {r.Output}"))
                        + $" Build output (end): {build[Math.Max(0, build.Length - 4000)..]}");
        }
    }

    /// <summary>
    /// What F5 did, for a failure: the wait for the breakpoint timed out once on Windows CI with nothing else to go on
    /// (PR #26, job 117494115864). The session's state, the programs still running, the notifications, the build F5 ran
    /// first and the Debug Console.
    /// </summary>
    private static string DebugStates(ShellFixture f)
    {
        var console = f.Shell.DebugLog.GetText();
        var build = f.Shell.Builds.Channel.GetText();
        return $"Debugger: {f.Shell.Debugger.State}. Running: {string.Join(", ", f.Shell.Runs.Running.Select(p => $"{p.Name} (exited: {p.HasExited})"))}. "
               + $"Notifications: {string.Join(" || ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message))}. "
               + $"Last build: {f.Shell.Builds.LastBuild?.Result}. Debug Console (end): {console[Math.Max(0, console.Length - 4000)..]} "
               + $"Build output (end): {build[Math.Max(0, build.Length - 2000)..]}";
    }

    private static string TestStates(ShellFixture f) => f.Shell.Tests.Read(roots => string.Join(" || ", roots.Where(r => r.Label is "WpfApp.Tests" or "WinFormsApp.Tests").Select(r =>
        $"{r.Label}{(r.Error is { } error ? $" ({error})" : string.Empty)}: "
        + string.Join(", ", r.Leaves().Select(l => $"{l.Label}={l.Outcome}{(l.Result?.Messages is { Count: > 0 } messages ? $" ({messages[0].Message})" : string.Empty)}")))));
}
