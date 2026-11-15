using System.Text;

namespace Codalus.Platform.Pty;

/// <summary>
/// The command line ConPTY starts a program with: each argument quoted the way <c>CommandLineToArgvW</c> parses it, except
/// the command line <c>cmd.exe</c> is given after <c>/c</c> or <c>/k</c>. <c>cmd.exe</c> reads the rest of its own command
/// line, and has no <c>\"</c> escape: a line with quotes in it (<c>dotnet workload restore "D:\My App\App.csproj"</c>)
/// quoted for <c>CommandLineToArgvW</c> reached the program as <c>\"D:\My App\App.csproj\"</c>, a path with quotes in it.
/// That one argument goes as <c>/s /c "line"</c> instead, which <c>cmd.exe</c> runs exactly as written. Several arguments
/// after <c>/c</c> (a program and its arguments) stay quoted for <c>CommandLineToArgvW</c>: <c>cmd.exe</c> passes them on
/// to the program, which parses them so. Pure string logic, so it is tested on every OS.
/// </summary>
internal static class WindowsCommandLine
{
    public static string Build(string file, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        Append(builder, file);
        var name = file.Split('\\', '/')[^1];
        var cmd = name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            builder.Append(' ');
            if (cmd && i == arguments.Count - 2 && (argument.Equals("/c", StringComparison.OrdinalIgnoreCase) || argument.Equals("/k", StringComparison.OrdinalIgnoreCase)))
            {
                // /s: cmd.exe removes the first and the last quote and runs what's between as it is.
                if (!arguments.Take(i).Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase)))
                {
                    builder.Append("/s ");
                }

                builder.Append(argument).Append(" \"").Append(arguments[i + 1]).Append('"');
                break;
            }

            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }
}
