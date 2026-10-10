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
        Infrastructure.Installation.Run();
        var options = ShellOptions.Parse(args);
        if (options.ShowVersion || options.ShowHelp)
        {
            AttachToParentConsole();
            Console.Out.WriteLine(options.ShowVersion ? ShellInfo.VersionText : ShellOptions.HelpText);
            return 0;
        }

        // daedalus diff: hand it to the running shell of this user-data folder, if there is one.
        if (options.Diff is { } diff)
        {
            var paths = DaedalusStudio.Platform.AppPaths.Resolve(DaedalusStudio.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            if (Infrastructure.SingleInstance.TrySend(Infrastructure.SingleInstance.PipeName(paths.State), new Infrastructure.InstanceRequest("diff", [diff.Left, diff.Right])))
            {
                return 0;
            }
        }

        // daedalus merge and --wait: the running shell does the work and answers when it's done (git waits for us).
        if (options.Merge is { } merge || options.WaitFile is not null)
        {
            var paths = DaedalusStudio.Platform.AppPaths.Resolve(DaedalusStudio.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            var request = options.Merge is { } m
                ? new Infrastructure.InstanceRequest("merge", [m.Local, m.Remote, m.Base, m.Merged], Wait: true)
                : new Infrastructure.InstanceRequest("wait", [options.WaitFile!], Wait: true);
            if (Infrastructure.SingleInstance.Send(Infrastructure.SingleInstance.PipeName(paths.State), request) is { } succeeded)
            {
                return succeeded ? 0 : 1;
            }
        }

        App.Options = options;
        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return code != 0 ? code : ShellHost.ExitCode;
        }
        catch (InvalidOperationException ex) when (ex.StackTrace?.Contains("AppBuilder.Setup", StringComparison.Ordinal) == true)
        {
            // The windowing system couldn't start: no display (an SSH session, a Mac with its display asleep, Linux
            // without X or Wayland). Say so instead of crashing with a stack trace.
            AttachToParentConsole();
            Console.Error.WriteLine($"Daedalus Studio needs a display and could not start its windowing system: {ex.Message}");
            Console.Error.WriteLine(OperatingSystem.IsLinux() ? "Set DISPLAY or WAYLAND_DISPLAY, or run it under xvfb-run." : "Run it in a desktop session with an active display.");
            return 3;
        }
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
