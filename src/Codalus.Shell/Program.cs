using System.Runtime.InteropServices;
using Avalonia;
using Codalus.Diagnostics.BenchEvents;

namespace Codalus.Shell;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started by git or ssh as askpass (ADR 0031): ask the shell that ran git, print the answer, exit.
        if (Codalus.Workbench.Git.AskpassClient.IsInvocation(args))
        {
            return Codalus.Workbench.Git.AskpassClient.Run(args[0], Console.Out, error: Console.Error);
        }

        BenchEvents.Emit(BenchEventNames.MainEntered);
        Infrastructure.Installation.Run();

        // Avalonia makes a synthetic bold or italic typeface (one a system font lacks, common on Linux) from the font's
        // bytes, read with SKStream.Read, which doesn't keep its stream alive: a background collection running alongside
        // the read (started by the large buffer it allocates) can finalize the stream and free the memory Skia copies
        // from. That was the start window's segmentation fault on Linux CI. A blocking collection finishes before the
        // read starts. TODO(issue): remove once Avalonia's SkiaTypeface.TryGetStream disposes the stream it opens.
        if (OperatingSystem.IsLinux())
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
        }

        var options = ShellOptions.Parse(args);
        if (options.ShowVersion || options.ShowHelp)
        {
            AttachToParentConsole();
            Console.Out.WriteLine(options.ShowVersion ? ShellInfo.VersionText : ShellOptions.HelpText);
            return 0;
        }

        if (options.UpdateNow)
        {
            AttachToParentConsole();
            return Infrastructure.Updates.UpdateNow(Console.Out);
        }

        // codalus --list-plugins and --install-plugin: the user plugins folder, without a window.
        if (options.ListPlugins || options.InstallPlugins.Count > 0)
        {
            AttachToParentConsole();
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            using var http = new HttpClient();
            var plugins = new Codalus.Workbench.Plugins.PluginCommandLine(paths.PluginsDirectory, Codalus.Platform.PlatformInfo.Current, new Codalus.Workbench.Plugins.PluginGallery(http))
            {
                SettingsFile = paths.SettingsFile,
            };
            var exitCode = 0;
            foreach (var plugin in options.InstallPlugins)
            {
                exitCode |= plugins.InstallAsync(plugin, Console.Out, CancellationToken.None).GetAwaiter().GetResult();
            }

            if (options.ListPlugins)
            {
                foreach (var line in plugins.List(options.ShowVersions))
                {
                    Console.Out.WriteLine(line);
                }
            }

            return exitCode;
        }

        // codalus mcp (M28c): an MCP server on stdio for an AI agent, relaying each call to the running IDE.
        if (options.Mcp)
        {
            AttachToParentConsole();
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            var instance = Infrastructure.SingleInstance.PipeName(paths.State);

            // An external agent's session (M48): its own pipe, open only while that AI Chat session lives.
            var pipe = options.AgentSession is { Length: > 0 } session
                ? Codalus.Workbench.Ai.Mcp.McpBridge.SessionPipeName(instance, session)
                : Codalus.Workbench.Ai.Mcp.McpBridge.PipeName(instance);
            var notListening = options.AgentSession is { Length: > 0 } ? "The AI Chat session of Codalus IDE that started this agent has ended." : null;
            var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            var server = new Codalus.Workbench.Ai.Mcp.McpStdioServer(input, output, (tool, arguments, ct) => Codalus.Workbench.Ai.Mcp.McpBridge.CallAsync(pipe, tool, arguments, notListening, ct));
            server.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }

        // codalus diff: hand it to the running shell of this user-data folder, if there is one.
        if (options.Diff is { } diff)
        {
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            if (Infrastructure.SingleInstance.TrySend(Infrastructure.SingleInstance.PipeName(paths.State), new Infrastructure.InstanceRequest("diff", [diff.Left, diff.Right])))
            {
                return 0;
            }
        }

        // codalus file[:line[:column]]: opens in the running window (unless --new-window), which comes to the front.
        if (options.OpenFile is { } open && !options.NewWindow && options.Bench is null && options.ExitOn is null && options.Remote is null && options.Path is null)
        {
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            string[] arguments = options.OpenAt is { } at
                ? [open, at.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), at.Column.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                : [open];
            if (Infrastructure.SingleInstance.TrySend(Infrastructure.SingleInstance.PipeName(paths.State), new Infrastructure.InstanceRequest("open", arguments)))
            {
                return 0;
            }
        }

        // codalus merge and --wait: the running shell does the work and answers when it's done (git waits for us).
        if (options.Merge is { } merge || options.WaitFile is not null)
        {
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
            var request = options.Merge is { } m
                ? new Infrastructure.InstanceRequest("merge", [m.Local, m.Remote, m.Base, m.Merged], Wait: true)
                : new Infrastructure.InstanceRequest("wait", [options.WaitFile!], Wait: true);
            if (Infrastructure.SingleInstance.Send(Infrastructure.SingleInstance.PipeName(paths.State), request) is { } succeeded)
            {
                return succeeded ? 0 : 1;
            }
        }

        // Started from the Finder, macOS gives the app only the system's PATH: take the login shell's (dotnet, git…).
        Codalus.Workbench.Setup.LoginShellPath.Import();
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
            Console.Error.WriteLine($"Codalus IDE needs a display and could not start its windowing system: {ex.Message}");
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
