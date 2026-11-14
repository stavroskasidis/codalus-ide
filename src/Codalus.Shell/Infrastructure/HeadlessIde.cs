using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Microsoft.Win32.SafeHandles;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>codalus mcp --folder &lt;path&gt;</c> (M60b, ADR 0099): a Codalus IDE with no window for an agent that runs
/// unattended. It refuses a folder that isn't trusted (the user's trusted folders, as the windowed IDE keeps them), then
/// runs the shell on Avalonia's headless platform (the shell tests' platform: real layout and rendering, no display),
/// opens the folder, and serves the IDE's tools on stdio with the same dispatcher and checks as a window's
/// (<see cref="ShellHost.IdeToolAsync"/>). Nobody is there to answer a question: actions run only when the user's
/// <c>ai.agent.ideActions</c> is <c>allow</c>, and every dialog is cancelled. It ends when the agent's pipe closes
/// (standard input ends). The per-folder question of the general pipe doesn't apply: the agent started this IDE on a
/// folder it named. Standard input and output are the agent's alone: the processes the IDE starts get none of them.
/// TODO(issue): a remote folder (<c>--remote</c>) without a window.
/// </summary>
internal static partial class HeadlessIde
{
    /// <summary>Who calls, for the AI Output channel's lines.</summary>
    internal const string Caller = "codalus mcp --folder";

    /// <summary>How often the headless platform renders: layout and the frames a window's render loop would draw.</summary>
    private static readonly TimeSpan s_frame = TimeSpan.FromMilliseconds(50);

    /// <summary>The exit code for a folder that can't be served (missing, or not trusted).</summary>
    public const int RefusedExitCode = 2;

    public static int Run(ShellOptions options)
    {
        var folder = options.McpFolder!;
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"codalus mcp --folder: {folder} isn't a folder.");
            return RefusedExitCode;
        }

        var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
        if (Refusal(folder, paths.State) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return RefusedExitCode;
        }

        var (input, output) = TakeStandardStreams();
        App.Options = options with { Path = null, StartWindow = false, SkipSetup = true };
        BuildAvaloniaApp().SetupWithoutStarting();
        var application = (App)Application.Current!;
        var shell = new ShellHost(application, App.Options);
        application.Shell = shell;
        var window = new MainWindow(shell);
        window.Show();

        // A window's render loop, which the headless platform leaves to its caller: layout, and what is drawn.
        var frames = new DispatcherTimer(s_frame, DispatcherPriority.Background, (_, _) => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        frames.Start();
        var opened = shell.OpenFolderAsync(folder);
        shell.Workbench.Log.AppendLine($"[headless] Serving the IDE's tools on stdio for {folder} (codalus mcp --folder).");

        using var exit = new CancellationTokenSource();
        var server = new McpStdioServer(
            input,
            output,
            ct => Ui.RunAsync(async () => Codalus.Workbench.Ai.Ide.IdeTools.FromJson((await shell.IdeToolAsync(McpBridge.ListToolsCall, default, Caller, ct).ConfigureAwait(true)).Text)).Unwrap(),
            (tool, arguments, ct) => Ui.RunAsync(async () =>
            {
                await opened.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                return await shell.IdeToolAsync(tool, arguments, Caller, ct).ConfigureAwait(true);
            }).Unwrap());
        var serving = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(exit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The agent's pipe broke: as good as closed.
            }
            finally
            {
                // The agent's pipe closed: the IDE ends with it. A document a tool left changed in an editor is saved
                // (nobody would be asked about it), and the window isn't closed, which would save its layout and editor
                // session over a window's of the same user-data folder.
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await shell.SaveAllAsync().ConfigureAwait(true);
                        frames.Stop();
                        await shell.ShutDownAsync(ShellHost.ShutdownLimit).ConfigureAwait(true);
                    }
                    finally
                    {
                        exit.Cancel();
                    }
                });
            }
        });
        Dispatcher.UIThread.MainLoop(exit.Token);
        serving.Wait(TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <summary>Why the folder isn't served, or null: it must be trusted, as builds and runs need (ADR 0004).</summary>
    internal static string? Refusal(string folder, string stateFolder) =>
        new Codalus.Workbench.Projects.TrustStore(Path.Combine(stateFolder, "trusted-folders.json")).IsTrusted(folder)
            ? null
            : $"codalus mcp --folder: {folder} isn't a trusted folder, so Codalus IDE won't open it without a window (it would build and run its code). "
              + "Open the folder in Codalus IDE and trust it (Trust Folder), or trust a folder above it, then start the agent again.";

    /// <summary>The shell on Avalonia's headless platform, rendering with Skia as the windowed app does.</summary>
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .LogToTrace();

    // ------------------------------------------------------------------ standard input and output

    /// <summary>
    /// The agent's standard input and output, kept for the MCP server; the process's own become the null device and
    /// standard error, so that nothing the IDE or a process it starts writes (or reads) reaches the agent's pipe.
    /// </summary>
    private static (TextReader Input, TextWriter Output) TakeStandardStreams()
    {
        Stream input;
        Stream output;
        if (OperatingSystem.IsWindows())
        {
            var stdin = GetStdHandle(StdInput);
            var stdout = GetStdHandle(StdOutput);
            input = new FileStream(new SafeFileHandle(stdin, ownsHandle: false), FileAccess.Read, 1);
            output = new FileStream(new SafeFileHandle(stdout, ownsHandle: false), FileAccess.Write, 1);
            var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read);
            _ = SetStdHandle(StdInput, nul.DangerousGetHandle());
            _ = SetStdHandle(StdOutput, GetStdHandle(StdError));
            GC.KeepAlive(nul);
            s_keep = nul;
        }
        else
        {
            var stdin = Dup(0);
            var stdout = Dup(1);
            if (stdin < 0 || stdout < 0)
            {
                return (Console.In, Console.Out);
            }

            input = new FileStream(new SafeFileHandle(stdin, ownsHandle: true), FileAccess.Read, 1);
            output = new FileStream(new SafeFileHandle(stdout, ownsHandle: true), FileAccess.Write, 1);
            using (var devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read))
            {
                _ = Dup2((int)devNull.DangerousGetHandle(), 0);
            }

            _ = Dup2(2, 1);
        }

        Console.SetOut(Console.Error);
        return (new StreamReader(input, new UTF8Encoding(false)), new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true });
    }

    /// <summary>The null device that is standard input on Windows, kept open for the process's life.</summary>
    private static SafeFileHandle? s_keep;

    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int handle, nint value);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static partial int Dup2(int fd, int target);
}
