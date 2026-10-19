using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// How a command an agent names starts on Windows (M48): CreateProcess finds only an <c>.exe</c> on the PATH, and npm's
/// commands are <c>.cmd</c> scripts, which only <c>cmd.exe</c> runs, and which <c>cmd.exe</c> reads with its own rules
/// (<c>%VAR%</c>, <c>^</c>, <c>&amp;</c>, its quote stripping). npm and npx from Node.js's layout run as <c>node.exe</c> with
/// their script, as <see cref="Node.Npm.UserNpm"/> runs npm, so no shell reads their arguments; another script runs
/// through <c>cmd.exe /d /c</c> only when neither its path nor its arguments hold anything <c>cmd.exe</c> would read
/// differently, and is refused otherwise. The logic only looks at files, so it is tested on every OS.
/// </summary>
public static class WindowsCommand
{
    private static readonly System.Buffers.SearchValues<char> s_cmdSpecial = System.Buffers.SearchValues.Create("\"%^&|<>!()\r\n");

    /// <summary>
    /// <paramref name="command"/> with <paramref name="args"/> as Windows starts it, finding it on <paramref name="path"/>
    /// (the PATH's form). Throws <see cref="AcpException"/> for a script <c>cmd.exe</c> can't be given safely.
    /// </summary>
    public static ProcessLaunchInfo Launch(string command, IReadOnlyList<string> args, string? path)
    {
        string? script = null;
        var extension = Path.GetExtension(command);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            script = Path.IsPathRooted(command) ? command : Find(command, path, [""]);
        }
        else if (extension.Length == 0 && !Path.IsPathRooted(command) && Find(command, path, [".exe", ".com"]) is null)
        {
            script = Find(command, path, [".cmd", ".bat"]);
        }

        if (script is null)
        {
            return new ProcessLaunchInfo(command, args);
        }

        // npm's and npx's scripts in Node.js's layout (the installer's, nvm-windows', fnm's): their JavaScript on that node.
        var folder = Path.GetDirectoryName(script) ?? "";
        var name = Path.GetFileNameWithoutExtension(script).ToLowerInvariant();
        if (name is "npm" or "npx")
        {
            var node = Path.Combine(folder, "node.exe");
            var cli = Path.Combine(folder, "node_modules", "npm", "bin", name + "-cli.js");
            if (File.Exists(node) && File.Exists(cli))
            {
                return new ProcessLaunchInfo(node, [cli, .. args]);
            }
        }

        if (script.Any(char.IsWhiteSpace) || script.AsSpan().IndexOfAny(s_cmdSpecial) >= 0 || args.Any(a => a.AsSpan().IndexOfAny(s_cmdSpecial) >= 0))
        {
            throw new AcpException($"'{script}' is a script that only cmd.exe runs, and its path or arguments have characters cmd.exe would read differently (spaces in its path, or \" % ^ & | < > ! ( )). Point the command at a program (.exe) instead.");
        }

        return new ProcessLaunchInfo(Path.Combine(Environment.SystemDirectory is { Length: > 0 } system ? system : "", "cmd.exe"), ["/d", "/c", script, .. args]);
    }

    /// <summary>What the dialog says runs, when it isn't the command as written (a script through node or cmd.exe).</summary>
    public static string? Resolved(string command, ProcessLaunchInfo launch) =>
        string.Equals(launch.FileName, command, StringComparison.Ordinal) ? null : string.Join(' ', new[] { launch.FileName }.Concat(launch.Arguments).Select(a => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));

    private static string? Find(string command, string? path, IReadOnlyList<string> extensions)
    {
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
