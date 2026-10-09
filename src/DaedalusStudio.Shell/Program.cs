using System.Runtime.InteropServices;
using Avalonia;
using DaedalusStudio.Diagnostics.BenchEvents;

namespace DaedalusStudio.Shell;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        BenchEvents.Emit(BenchEventNames.MainEntered);
        var options = ShellOptions.Parse(args);
        if (options.ShowVersion || options.ShowHelp)
        {
            AttachToParentConsole();
            Console.Out.WriteLine(options.ShowVersion ? ShellInfo.VersionText : ShellOptions.HelpText);
            return 0;
        }

        App.Options = options;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer and headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>A GUI-subsystem process on Windows has no console; borrow the parent's for <c>--version</c> and <c>--help</c>.</summary>
    private static void AttachToParentConsole()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AttachConsole(-1);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
