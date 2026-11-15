using System.Diagnostics;
using System.Text;

#if CODALUS_PROCESS_TREE_INTERNAL
namespace Codalus.Platform.Internal;
#else
namespace Codalus.Platform;
#endif

/// <summary>
/// A Windows command line: each argument quoted the way <c>CommandLineToArgvW</c> parses it, except the command line
/// <c>cmd.exe</c> is given after <c>/c</c> or <c>/k</c>. <c>cmd.exe</c> reads the rest of its own command line, and has no
/// <c>\"</c> escape: a line with quotes in it (<c>dotnet workload restore "D:\My App\App.csproj"</c>) quoted for
/// <c>CommandLineToArgvW</c> (as ConPTY's launch did, and as <see cref="ProcessStartInfo.ArgumentList"/> does) reaches the
/// program as <c>\"D:\My App\App.csproj\"</c>, a path with quotes in it. That one argument goes as <c>/s /c "line"</c>
/// instead, which <c>cmd.exe</c> runs exactly as written. Several arguments after <c>/c</c> (a program and its arguments)
/// stay quoted for <c>CommandLineToArgvW</c>: <c>cmd.exe</c> passes them on to the program, which parses them so. Pure
/// string logic, so it is tested on every OS.
/// </summary>
/// <remarks>Codalus.Rpc compiles this file in as an internal copy, as it does <c>ProcessTree</c>.</remarks>
#if CODALUS_PROCESS_TREE_INTERNAL
internal static class WindowsCommandLine
#else
public static class WindowsCommandLine
#endif
{
    /// <summary>The whole command line, <paramref name="file"/> first (CreateProcess's <c>lpCommandLine</c>).</summary>
    public static string Build(string file, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        Append(builder, file);
        if (arguments.Count > 0)
        {
            builder.Append(' ').Append(Arguments(file, arguments));
        }

        return builder.ToString();
    }

    /// <summary>The arguments' part of the command line (<see cref="ProcessStartInfo.Arguments"/>).</summary>
    public static string Arguments(string file, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        var line = LineIndex(file, arguments);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            if (i == line - 1)
            {
                // /s: cmd.exe removes the first and the last quote and runs what's between as it is.
                if (!arguments.Take(i).Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase)))
                {
                    builder.Append("/s ");
                }

                builder.Append(arguments[i]).Append(" \"").Append(arguments[line]).Append('"');
                break;
            }

            Append(builder, arguments[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gives <paramref name="start"/> its arguments: on Windows a line for <c>cmd.exe</c> as <see cref="Arguments(string, IReadOnlyList{string})"/>
    /// writes it, otherwise in <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public static void SetArguments(ProcessStartInfo start, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows() && LineIndex(start.FileName, arguments) >= 0)
        {
            start.Arguments = Arguments(start.FileName, arguments);
            return;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
    }

    /// <summary>The index of the one argument <c>cmd.exe</c> runs as a line (the last, after <c>/c</c> or <c>/k</c>), or -1.</summary>
    private static int LineIndex(string file, IReadOnlyList<string> arguments)
    {
        var name = file.Split('\\', '/')[^1];
        if (arguments.Count < 2 || !(name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("cmd", StringComparison.OrdinalIgnoreCase)))
        {
            return -1;
        }

        var run = arguments[^2];
        return run.Equals("/c", StringComparison.OrdinalIgnoreCase) || run.Equals("/k", StringComparison.OrdinalIgnoreCase) ? arguments.Count - 1 : -1;
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

            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }
}
