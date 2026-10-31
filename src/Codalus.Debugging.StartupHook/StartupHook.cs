using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

/// <summary>
/// A .NET startup hook (<c>DOTNET_STARTUP_HOOKS</c>, M14c, ADR 0039) for programs that Codalus IDE starts in the
/// integrated terminal under the debugger. netcoredbg can't launch into a terminal (it has no <c>runInTerminal</c>), so
/// the IDE starts the program there itself and attaches: before <c>Main</c>, this hook writes the process id to
/// <c>$CODALUS_DEBUG_WAIT/pid</c> and waits for <c>$CODALUS_DEBUG_WAIT/go</c>, which the IDE writes once the debugger
/// has attached and set the breakpoints. After <c>CODALUS_DEBUG_WAIT_SECONDS</c> (120) it goes on without it.
/// The variables are cleared, so the program's own child processes don't wait.
/// Programs the Codalus debugger launches get it too, for <see cref="HotReloadAgent"/> (ADR 0090).
/// </summary>
internal static class StartupHook
{
    public const string WaitVariable = "CODALUS_DEBUG_WAIT";
    public const string TimeoutVariable = "CODALUS_DEBUG_WAIT_SECONDS";

    /// <summary>The Codalus debugger's host, whose launch mode starts the program (ADR 0086).</summary>
    private const string DebugHost = "codalus-debughost";

    public static void Initialize()
    {
        // The Codalus debugger's launcher runs with the program's environment, and its runtime runs DOTNET_STARTUP_HOOKS
        // although its runtimeconfig turns them off: the variables are the program's, left for it.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == DebugHost)
        {
            return;
        }

        HotReloadAgent.Start();
        var folder = Environment.GetEnvironmentVariable(WaitVariable);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable(TimeoutVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 120;
        Environment.SetEnvironmentVariable(WaitVariable, null);
        Environment.SetEnvironmentVariable(TimeoutVariable, null);
        try
        {
            int id;
            using (var self = Process.GetCurrentProcess())
            {
                id = self.Id;
            }

            // Written whole, then renamed: the IDE never reads half a number.
            var pid = Path.Combine(folder, "pid");
            File.WriteAllText(pid + ".tmp", id.ToString(CultureInfo.InvariantCulture));
            File.Move(pid + ".tmp", pid);

            var go = Path.Combine(folder, "go");
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!File.Exists(go))
            {
                if (DateTime.UtcNow > deadline)
                {
                    Console.Error.WriteLine("The debugger didn't attach in time; the program runs without it.");
                    return;
                }

                Thread.Sleep(10);
            }

            // The signal arrived: the folder is ours to clean up.
            File.Delete(go);
            File.Delete(pid);
            Directory.Delete(folder);
        }
        catch (IOException)
        {
            // The IDE removed the folder (it gave up): run on.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
