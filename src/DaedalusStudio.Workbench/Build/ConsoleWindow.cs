using System.Diagnostics;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// A program in a console window of its own (Windows), as a classic IDE runs console and web apps: Run Without
/// Debugging's <c>externalConsole</c>, and Start Debugging's console window (<see cref="Debugging.ConsoleWindowLauncher"/>).
/// <c>cmd</c> keeps the window open after the program ends, until a key is pressed.
/// </summary>
public static class ConsoleWindow
{
    /// <summary>The start of <paramref name="program"/> in a new window titled <paramref name="title"/>.</summary>
    public static ProcessStartInfo StartInfo(string title, string program, IReadOnlyList<string> arguments, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var command = string.Join(' ', new[] { program }.Concat(arguments).Select(Quote));
        // The shell has no console, so cmd gets a new window (CreateNoWindow = false).
        var start = new ProcessStartInfo("cmd.exe", $"/s /c \"title {Title(title)} & {command} & echo. & pause\"")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        return start;
    }

    /// <summary>An argument for cmd's command line: quoted when it has spaces, quotes or cmd's operators.</summary>
    public static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? argument
            : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>A window title without cmd's operators.</summary>
    private static string Title(string title)
    {
        var plain = new string([.. title.Where(c => c is not ('&' or '|' or '<' or '>' or '^' or '"' or '%'))]).Trim();
        return plain.Length == 0 ? "Daedalus Studio" : plain;
    }
}
