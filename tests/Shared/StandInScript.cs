using System.Diagnostics;

namespace Codalus.Tests;

/// <summary>
/// Writes a stand-in program, a script a test runs right away: executable on Linux and macOS, as given on Windows.
/// Linux refuses to run a file that any process holds open for writing ("Text file busy"), and a process this one starts
/// (another test's, any thread's) holds a copy of every handle this one has from its fork until its exec: a script this
/// process wrote itself can fail to start, though it was closed (1 run in 100 with processes starting meanwhile; Linux,
/// WSL 2026-10-28, #209). So <c>cp</c> writes it from text this process wrote next to it: no process holds that handle.
/// </summary>
internal static class StandInScript
{
    public static void Write(string file, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(file, text);
            return;
        }

        var source = file + ".text";
        File.WriteAllText(source, text);
        try
        {
            using var cp = Process.Start(new ProcessStartInfo("/bin/cp", [source, file]) { UseShellExecute = false })!;
            cp.WaitForExit();
            if (cp.ExitCode != 0)
            {
                throw new IOException($"cp couldn't write {file} (exit code {cp.ExitCode}).");
            }
        }
        finally
        {
            File.Delete(source);
        }

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
