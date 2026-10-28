using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

/// <summary>
/// Candidate (b) of ADR 0086's launch: a program started with DOTNET_DefaultDiagnosticPortSuspend=1 runs this hook
/// after the debugger has resumed it and before Main. It clears the variable, and itself from DOTNET_STARTUP_HOOKS,
/// so the program's own .NET children aren't held. Environment.SetEnvironmentVariable changes only .NET's copy of the
/// environment off Windows (what Process.Start passes on); unsetenv clears libc's too (what a native spawn passes on).
/// SPIKE_HOOK_MANAGED_ONLY=1 skips unsetenv, to show the difference.
/// </summary>
internal static class StartupHook
{
    private const string Suspend = "DOTNET_DefaultDiagnosticPortSuspend";
    private const string Hooks = "DOTNET_STARTUP_HOOKS";

    public static void Initialize()
    {
        var self = typeof(StartupHook).Assembly.Location;
        var hooks = (Environment.GetEnvironmentVariable(Hooks) ?? "").Split(Path.PathSeparator)
            .Where(h => h.Length > 0 && !string.Equals(Path.GetFullPath(h), self, StringComparison.OrdinalIgnoreCase)).ToArray();
        var rest = hooks.Length == 0 ? null : string.Join(Path.PathSeparator.ToString(), hooks);
        var native = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.GetEnvironmentVariable("SPIKE_HOOK_MANAGED_ONLY") != "1";

        Environment.SetEnvironmentVariable(Suspend, null);
        Environment.SetEnvironmentVariable(Hooks, rest);
        if (native)
        {
            unsetenv(Suspend);
            if (rest is null) unsetenv(Hooks); else setenv(Hooks, rest, 1);
        }
    }

    [DllImport("libc")]
    private static extern int unsetenv(string name);

    [DllImport("libc")]
    private static extern int setenv(string name, string value, int overwrite);
}
