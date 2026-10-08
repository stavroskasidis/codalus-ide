using Avalonia;
using Daedalus.Diagnostics.BenchEvents;

namespace Daedalus.Spike.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        BenchEvents.Emit(BenchEventNames.MainEntered);
        App.Options = SpikeOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer and headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>Command line: <c>[--solution path] [--open file] [--bench typing|scroll] [--exit-on interactive|highlight|done] [--screenshot file.png]</c>.</summary>
internal sealed record SpikeOptions(string? Solution, string? OpenFile, string? Bench, string? ExitOn, string? Screenshot = null)
{
    public static SpikeOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new SpikeOptions(Value("--solution"), Value("--open"), Value("--bench"), Value("--exit-on"), Value("--screenshot"));
    }
}
