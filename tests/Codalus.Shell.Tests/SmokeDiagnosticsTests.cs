using System.Diagnostics;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// What the smoke adds to a failure that doesn't reproduce (the Mac's smoke of the release candidate, 2026-11-18): the
/// exception behind an error toast it hid before a picture, and the IDE's child processes when a build wait timed out.
/// </summary>
public sealed class SmokeDiagnosticsTests
{
    private const string Log = """
        [setup] Using the .NET SDK in /usr/local/share/dotnet.
        [unhandled] System.ArgumentException: An older failure.
           at Codalus.Shell.Old.Method()
        [smoke] debug.remote: passed
        [unhandled] System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
           at System.Collections.Generic.List`1.Enumerator.MoveNext()
           at Codalus.Shell.Views.Example.OnChanged()
        [diagnostics] something after it

        """;

    [Fact]
    public void An_error_toast_from_an_unhandled_exception_is_given_its_logged_stack()
    {
        var behind = SmokeTest.UnhandledBehind(Log.ReplaceLineEndings("\n"), SmokeTest.UnhandledToast + "Collection was modified; enumeration operation may not execute.");
        Assert.Equal(
            "[unhandled] System.InvalidOperationException: Collection was modified; enumeration operation may not execute.\n"
            + "   at System.Collections.Generic.List`1.Enumerator.MoveNext()\n"
            + "   at Codalus.Shell.Views.Example.OnChanged()",
            behind);

        // The older one, by its own message: the entry ends where the next one starts.
        Assert.Equal("[unhandled] System.ArgumentException: An older failure.\n   at Codalus.Shell.Old.Method()", SmokeTest.UnhandledBehind(Log.ReplaceLineEndings("\n"), SmokeTest.UnhandledToast + "An older failure."));
    }

    [Fact]
    public void An_unexpected_error_without_its_entry_shows_the_latest_and_another_error_shows_none()
    {
        var latest = SmokeTest.UnhandledBehind(Log.ReplaceLineEndings("\n"), SmokeTest.UnhandledToast + "Something else.");
        Assert.NotNull(latest);
        Assert.StartsWith("(the latest unhandled exception logged; its message isn't the toast's)\n[unhandled] System.InvalidOperationException", latest, StringComparison.Ordinal);

        // An error the IDE reported itself (not an unhandled exception) has no stack to show.
        Assert.Null(SmokeTest.UnhandledBehind(Log.ReplaceLineEndings("\n"), "Could not open App.slnx: it isn't there."));
        Assert.Null(SmokeTest.UnhandledBehind("[setup] nothing unhandled\n", SmokeTest.UnhandledToast + "Boom."));
    }

    [Fact]
    public void The_child_processes_are_listed_with_their_command_lines()
    {
        // A child of this process that waits: it's listed with how it was started (its image name on Windows).
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >NUL"])
            : new ProcessStartInfo("/bin/sh", ["-c", "sleep 30; echo smoke-diagnostics-child"]);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        using var child = Process.Start(start)!;
        try
        {
            var listed = SmokeTest.ChildProcesses();
            var line = listed.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(child.Id + " ", StringComparison.Ordinal));
            Assert.True(line is not null, $"process {child.Id} isn't listed:\n{listed}");
            Assert.Contains(OperatingSystem.IsWindows() ? "cmd" : "smoke-diagnostics-child", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("(running ", line, StringComparison.Ordinal);
        }
        finally
        {
            Codalus.Platform.ProcessTree.Kill(child);
        }
    }
}
