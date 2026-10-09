using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Daedalus.Shell.Views;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Daedalus.Shell.Tests.TestAppBuilder))]

namespace Daedalus.Shell.Tests;

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
    private ShellFixture(string userData)
    {
        UserData = userData;
        Environment.SetEnvironmentVariable("DAEDALUS_PLUGINHOST", Path.Combine(RepoRoot, "src", "Daedalus.Plugins.Host", "bin", Configuration, "net10.0", "daedalus-pluginhost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_BUILDHOST", Path.Combine(RepoRoot, "src", "Daedalus.Projects.Host", "bin", Configuration, "net10.0", "daedalus-buildhost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_LSHOST", Path.Combine(RepoRoot, "src", "Daedalus.LanguageServices.Host", "bin", Configuration, "net10.0", "daedalus-lshost.dll"));
        Environment.SetEnvironmentVariable("DAEDALUS_TRASH_DIR", Path.Combine(userData, "trash"));
        Shell = new ShellHost(Application.Current!, new ShellOptions
        {
            UserDataDir = userData,
            WatchFiles = false,
            BuiltInPluginsDir = Path.Combine(RepoRoot, "src", "Plugins", "Daedalus.Plugins.Welcome", "bin", Configuration, "net10.0"),
        });
        Window = new MainWindow(Shell);
        Window.Show();
        Pump();
    }

    public string UserData { get; }

    public ShellHost Shell { get; }

    public MainWindow Window { get; }

    internal static string RepoRoot => Metadata("DaedalusRepoRoot");

    private static string Configuration => Metadata("DaedalusConfiguration");

    /// <summary>Copies samples/ProjectSystem into the user-data folder (so tests can change it) and returns the .slnx path.</summary>
    public string CopyFixture()
    {
        var target = Path.Combine(UserData, "fixture");
        Copy(Path.Combine(RepoRoot, "samples", "ProjectSystem"), Path.Combine(target, "ProjectSystem"));
        File.WriteAllText(Path.Combine(target, "Directory.Build.props"), "<Project />");
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

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public static ShellFixture Create(string? userData = null) =>
        new(userData ?? Path.Combine(Path.GetTempPath(), "daedalus-shell-" + Guid.NewGuid().ToString("N")));

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
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the shell.");
            Pump();
            await Task.Delay(15);
        }
    }

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
            Directory.Delete(UserData, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
