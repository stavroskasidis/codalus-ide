using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using DaedalusStudio.Shell.Views;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(DaedalusStudio.Shell.Tests.TestAppBuilder))]

namespace DaedalusStudio.Shell.Tests;

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
        Environment.SetEnvironmentVariable("DAEDALUS_PLUGINHOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.Plugins.Host", "bin", Configuration, "net10.0", "daedalus-pluginhost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_BUILDHOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.Projects.Host", "bin", Configuration, "net10.0", "daedalus-buildhost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_LSHOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.LanguageServices.Host", "bin", Configuration, "net10.0", "daedalus-lshost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_MARKUPHOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.Markup.Host", "bin", Configuration, "net10.0", "daedalus-markuphost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_REPLHOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.Interactive.Host", "bin", Configuration, "net10.0", "daedalus-replhost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_T4HOST", Path.Combine(RepoRoot, "src", "DaedalusStudio.TextTemplating.Host", "bin", Configuration, "net10.0", "daedalus-t4host.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_TRASH_DIR", Path.Combine(userData, "trash"));

        // Hosting tokens stay in this process: the tests never touch the OS credential store (M13b).
        Environment.SetEnvironmentVariable(DaedalusStudio.Platform.Credentials.CredentialStore.EnvironmentVariable, "memory");
        Shell = new ShellHost(Application.Current!, new ShellOptions
        {
            UserDataDir = userData,
            WatchFiles = false,
            BuiltInPluginsDir = builtInPlugins ?? PluginBin("DaedalusStudio.Plugins.Welcome"),
        });
        Window = new MainWindow(Shell);
        Window.Show();
        Pump();
    }

    public string UserData { get; }

    public ShellHost Shell { get; }

    public MainWindow Window { get; }

    internal static string RepoRoot => Metadata("DaedalusRepoRoot");

    /// <summary>A built-in plugin's build output (a folder with its manifest).</summary>
    public static string PluginBin(string project) => Path.Combine(RepoRoot, "src", "Plugins", project, "bin", Configuration, "net10.0");

    private static string Configuration => Metadata("DaedalusConfiguration");

    /// <summary>Copies samples/ProjectSystem into the user-data folder (so tests can change it) and returns the .slnx path.</summary>
    public string CopyFixture()
    {
        var target = Path.Combine(UserData, "fixture");
        Copy(Path.Combine(RepoRoot, "samples", "ProjectSystem"), Path.Combine(target, "ProjectSystem"));
        File.WriteAllText(Path.Combine(target, "Directory.Build.props"), "<Project />");
        DaedalusStudio.Tests.FixtureSdk.Pin(target);
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
        DaedalusStudio.Tests.FixtureSdk.Pin(target);
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
        new(userData ?? Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-shell-" + Guid.NewGuid().ToString("N")), builtInPlugins);

    private static string Metadata(string key) =>
        typeof(ShellFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>Runs pending dispatcher work and a render tick.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task PumpUntil(Func<bool> condition, int seconds = 30)
    {
        // A monotonic clock: a machine that sleeps during the wait (a Mac with the display off) doesn't time out at once.
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(seconds), "Timed out waiting for the shell.");
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
