using System.Text;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// Changing an SDK in a folder the user can't write (ADR 0104): the classic IDE installs the Android SDK in
/// <c>C:\Program Files (x86)\Android\android-sdk</c>, which only an administrator can change. On Windows the IDE runs the
/// same command elevated (one UAC prompt), through a <c>cmd</c> script that writes its output to a log the IDE follows
/// and its exit code to a file; elsewhere it shows the command under <c>sudo</c>, to run in the terminal.
/// </summary>
public static class Elevation
{
    /// <summary>Whether this process can create files in <paramref name="folder"/> (an absent folder: whether it can create it).</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var existing = folder;
            while (!Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(existing))
                {
                    return false;
                }
            }

            var probe = Path.Combine(existing, ".codalus-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>cmd</c> script that runs <paramref name="command"/> elevated: its environment and folder, its input from
    /// <paramref name="inputPath"/> (which the caller writes, when the command has input), its output to
    /// <paramref name="logPath"/> and its exit code to <paramref name="exitPath"/>. A batch file (<c>sdkmanager.bat</c>)
    /// is started with <c>call</c> so the script goes on after it.
    /// </summary>
    public static string WindowsScript(ToolCommand command, string logPath, string exitPath, string inputPath)
    {
        var script = new StringBuilder();
        script.Append("@echo off\r\n");
        script.Append("chcp 65001 >nul\r\n");
        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string>())
        {
            script.Append("set \"").Append(Escape(key)).Append('=').Append(Escape(value)).Append("\"\r\n");
        }

        if (command.WorkingDirectory is { } directory)
        {
            script.Append("cd /d ").Append(Quote(directory)).Append("\r\n");
        }

        if (command.Program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || command.Program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            script.Append("call ");
        }

        script.Append(Quote(command.Program));
        foreach (var argument in command.Arguments)
        {
            script.Append(' ').Append(Quote(argument));
        }

        if (command.Input is not null)
        {
            script.Append(" < ").Append(Quote(inputPath));
        }

        script.Append(" > ").Append(Quote(logPath)).Append(" 2>&1\r\n");

        // The redirection first: "echo 1> file" would redirect handle 1 and write "ECHO is off.".
        script.Append("> ").Append(Quote(exitPath)).Append(" echo %ERRORLEVEL%\r\n");
        return script.ToString();
    }

    /// <summary>
    /// The command under <c>sudo</c>, as typed in a POSIX shell: <c>sudo env ANDROID_HOME=… JAVA_HOME=… program args</c>,
    /// with its input piped in (<c>yes</c> answers to the licenses the user accepted).
    /// </summary>
    public static string SudoCommand(ToolCommand command)
    {
        var line = new StringBuilder();
        if (command.WorkingDirectory is { } directory)
        {
            line.Append("cd ").Append(ShellQuote(directory)).Append(" && ");
        }

        if (command.Input is { } input && input.Split('\n').Where(a => a.Trim().Length > 0).Distinct().ToList() is [var answer])
        {
            line.Append("yes ").Append(ShellQuote(answer.Trim())).Append(" | ");
        }

        line.Append("sudo env");
        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string>())
        {
            line.Append(' ').Append(key).Append('=').Append(ShellQuote(value));
        }

        line.Append(' ').Append(ShellQuote(command.Program));
        foreach (var argument in command.Arguments)
        {
            line.Append(' ').Append(ShellQuote(argument));
        }

        return line.ToString();
    }

    /// <summary>
    /// A <c>dotnet workload</c> command line for the terminal (ADR 0064). <c>dotnet workload</c> doesn't ask for
    /// permission itself outside Windows: in a .NET root the user can't write (<c>/usr/local/share/dotnet</c>, a
    /// distribution's package) it ends with "requires elevated privileges", so there it runs under <c>sudo</c>, with
    /// <paramref name="dotnet"/>'s full path (sudo's own <c>PATH</c> may not have it). On Windows the SDK's installer
    /// asks (UAC) by itself.
    /// </summary>
    /// <param name="dotnet">The <c>dotnet</c> the terminal runs, when it's known.</param>
    /// <param name="writable">Whether the user can write its root.</param>
    public static string WorkloadCommand(Codalus.Platform.PlatformKind platform, string? dotnet, bool writable, IReadOnlyList<string> arguments)
    {
        if (platform == Codalus.Platform.PlatformKind.Windows)
        {
            return "dotnet " + string.Join(' ', arguments.Select(a => a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? a : "\"" + a + "\""));
        }

        var line = string.Join(' ', arguments.Select(ShellQuote));
        return writable || dotnet is null ? "dotnet " + line : "sudo " + ShellQuote(dotnet) + " " + line;
    }

    /// <summary>A POSIX shell word: as is when it's safe, else in single quotes.</summary>
    public static string ShellQuote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '=' or ':' or '+' or ',' or '@')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A <c>cmd</c> argument in double quotes (a double quote can't be in a Windows path; <c>%</c> is doubled in a script).</summary>
    private static string Quote(string value) => "\"" + Escape(value) + "\"";

    private static string Escape(string value) => value.Replace("%", "%%", StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal);
}
