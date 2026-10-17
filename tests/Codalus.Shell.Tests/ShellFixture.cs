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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }); // real rendering, so frames can be captured
}

/// <summary>A shell and its main window on a fresh user-data directory, running headless.</summary>
internal sealed class ShellFixture : IAsyncDisposable
{
    private ShellFixture(string userData, string? builtInPlugins)
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
        var settings = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, userData).SettingsFile;
        if (!File.Exists(settings))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{ \"run.console\": \"outputWindow\", \"debug.console\": \"internalConsole\" }");
        }

        Shell = new ShellHost(Application.Current!, new ShellOptions
        {
            UserDataDir = userData,
            WatchFiles = false,
            StartWindow = false,
            BuiltInPluginsDir = builtInPlugins ?? PluginBin("Codalus.Plugins.Welcome"),
        });
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
        Copy(Path.Combine(RepoRoot, "samples", name), Path.Combine(target, name));
        return Path.Combine(target, name);
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

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "node_modules")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public static ShellFixture Create(string? userData = null, string? builtInPlugins = null) =>
        new(userData ?? Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N")), builtInPlugins);

    private static string Metadata(string key) =>
        typeof(ShellFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>Runs pending dispatcher work and a render tick.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

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
    }

    /// <summary>
    /// A token for one language request: canceled after <paramref name="seconds"/>, so a server that never answers
    /// fails the test instead of hanging it (a blocked UI thread never reaches PumpUntil's own timeout).
    /// </summary>
    public static CancellationToken RequestTimeout(int seconds = 30) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

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
        Window.Close();
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
