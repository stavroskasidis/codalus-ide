using System.Text;

namespace Codalus.Platform;

/// <summary>
/// Paths as people type them: <c>~</c> for the home folder, environment variables (<c>%NAME%</c> on every OS, and
/// <c>$NAME</c> or <c>${NAME}</c> on Linux and macOS, as their shells do). Path boxes, New Project's location and
/// pickers' start folders read typed text through <see cref="Expand(string)"/>.
/// </summary>
public static class UserPaths
{
    /// <summary>
    /// <paramref name="text"/> trimmed, with its variables and a leading <c>~</c> (<c>~</c>, <c>~/…</c>, <c>~\…</c>)
    /// expanded. A variable that isn't set stays as it was typed; text that can't be expanded comes back trimmed.
    /// </summary>
    public static string Expand(string text) =>
        Expand(text, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows());

    /// <summary>The expansion with the variables, home folder and OS given (tests check every OS's rules on one).</summary>
    internal static string Expand(string text, Func<string, string?> variable, string home, bool windows)
    {
        var path = text.Trim();
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            path = Percent(path, variable);
            if (!windows)
            {
                path = Dollar(path, variable);
            }

            if (home.Length > 0 && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)))
            {
                path = path == "~" ? home : home.TrimEnd('/', '\\') + path[1..];
            }

            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return text.Trim();
        }
    }

    /// <summary><c>%NAME%</c> → the variable's value; an unset name or a lone <c>%</c> stays.</summary>
    private static string Percent(string text, Func<string, string?> variable)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var close = text[i] == '%' ? text.IndexOf('%', i + 1) : -1;
            if (close > i + 1 && variable(text[(i + 1)..close]) is { } value)
            {
                builder.Append(value);
                i = close + 1;
                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary><c>$NAME</c> and <c>${NAME}</c> → the variable's value; an unset name stays as typed.</summary>
    private static string Dollar(string text, Func<string, string?> variable)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '$' && i + 1 < text.Length)
            {
                if (text[i + 1] == '{' && text.IndexOf('}', i + 2) is var close and > 0 && close > i + 2 && variable(text[(i + 2)..close]) is { } braced)
                {
                    builder.Append(braced);
                    i = close + 1;
                    continue;
                }

                var end = i + 1;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
                {
                    end++;
                }

                if (end > i + 1 && !char.IsAsciiDigit(text[i + 1]) && variable(text[(i + 1)..end]) is { } value)
                {
                    builder.Append(value);
                    i = end;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }
}
