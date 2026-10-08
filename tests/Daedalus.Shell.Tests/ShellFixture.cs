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
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>A shell and its main window on a fresh user-data directory, running headless.</summary>
internal sealed class ShellFixture : IAsyncDisposable
{
    private ShellFixture(string userData)
    {
        UserData = userData;
        Environment.SetEnvironmentVariable("DAEDALUS_PLUGINHOST", Path.Combine(RepoRoot, "src", "Daedalus.Plugins.Host", "bin", Configuration, "net10.0", "daedalus-pluginhost.dll"));
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
