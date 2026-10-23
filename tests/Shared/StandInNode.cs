namespace Codalus.Tests;

/// <summary>How the stand-in npx's download step ends (npm's own lines for each).</summary>
public enum StandInDownload
{
    Ok,
    NotFound,
    Offline,
    BadEngine,

    /// <summary>The download never ends (Cancel stops it).</summary>
    Hang,
}

/// <summary>
/// Stand-in <c>node</c> and <c>npx</c> (ADR 0083) in a folder of their own, put on a test's search path so the Connect flow
/// never meets this machine's Node.js or npm: <c>node --version</c> prints the version given, <c>npx --version</c> its
/// own, the download step (<c>--loglevel=http</c>) prints recorded <c>http fetch</c> lines or an npm error, and any other
/// npx run (<c>npx -y &lt;package&gt;</c>, the agent itself) starts the stand-in ACP agent with its arguments. Shell scripts
/// on Linux and macOS, <c>.cmd</c> scripts on Windows; nothing reaches npm's registry.
/// </summary>
public static class StandInNode
{
    /// <summary>Writes the stand-ins into <paramref name="folder"/> and returns it; <paramref name="nodeVersion"/> null leaves Node.js out.</summary>
    /// <param name="agentEnv">Variables the stand-in agent starts with (its ACP_FAKE_* modes), set by the npx script itself so a test's modes never leak into another's.</param>
    public static string Create(string folder, string? nodeVersion, string dotnet, string agentDll, StandInDownload download = StandInDownload.Ok, string npxVersion = "10.9.0", bool npx = true, IReadOnlyDictionary<string, string>? agentEnv = null)
    {
        Directory.CreateDirectory(folder);
        var lines = download switch
        {
            StandInDownload.NotFound => (Out: new[] { "npm http fetch GET 404 https://registry.npmjs.org/stand-in 41ms (cache miss)" }, Error: new[] { "npm error code E404", "npm error 404 Not Found - GET https://registry.npmjs.org/stand-in - Not found" }, Exit: 1),
            StandInDownload.Offline => (Out: Array.Empty<string>(), Error: new[] { "npm error code ETIMEDOUT", "npm error network request to https://registry.npmjs.org/stand-in failed, reason: connect ETIMEDOUT 104.16.2.35:443" }, Exit: 1),
            StandInDownload.Hang => (Out: new[] { "npm http fetch GET 200 https://registry.npmjs.org/stand-in 12ms (cache miss)" }, Error: Array.Empty<string>(), Exit: -1),
            StandInDownload.BadEngine => (Out: new[] { "npm http fetch GET 200 https://registry.npmjs.org/stand-in 12ms (cache miss)" }, Error: new[] { "npm warn EBADENGINE Unsupported engine {", "npm warn EBADENGINE   required: { node: '>=22' },", "npm error code EBADENGINE" }, Exit: 1),
            _ => (Out: new[] { "npm http fetch GET 200 https://registry.npmjs.org/stand-in 12ms (cache miss)", "npm http fetch GET 200 https://registry.npmjs.org/stand-in/-/stand-in-1.0.0.tgz 30ms (cache miss)" }, Error: Array.Empty<string>(), Exit: 0),
        };

        if (OperatingSystem.IsWindows())
        {
            if (nodeVersion is not null)
            {
                File.WriteAllText(Path.Combine(folder, "node.cmd"), $"@echo off\r\necho {nodeVersion}\r\n");
            }

            if (npx)
            {
                var echo = string.Concat(lines.Out.Select(l => $"echo {l}\r\n")) + string.Concat(lines.Error.Select(l => $"echo {l.Replace(">", "^>", StringComparison.Ordinal)} 1>&2\r\n"))
                    + (lines.Exit < 0 ? "ping -n 120 127.0.0.1 >nul\r\n" : "");
                File.WriteAllText(Path.Combine(folder, "npx.cmd"),
                    "@echo off\r\n"
                    + string.Concat((agentEnv ?? new Dictionary<string, string>()).Select(e => $"set \"{e.Key}={e.Value}\"\r\n"))
                    + $"if \"%1\"==\"--version\" (echo {npxVersion}& exit /b 0)\r\n"
                    + "echo %* | findstr /c:\"--loglevel=http\" >nul && goto download\r\n"
                    + $"\"{dotnet}\" \"{agentDll}\" %*\r\nexit /b %errorlevel%\r\n"
                    + ":download\r\n" + echo + $"exit /b {Math.Max(lines.Exit, 0)}\r\n");
            }

            return folder;
        }

        if (nodeVersion is not null)
        {
            Write(Path.Combine(folder, "node"), $"#!/bin/sh\necho {nodeVersion}\n");
        }

        if (npx)
        {
            var output = string.Concat(lines.Out.Select(l => $"  echo '{l}' >&2\n")) + string.Concat(lines.Error.Select(l => $"  echo '{l}' >&2\n"));
            Write(Path.Combine(folder, "npx"),
                "#!/bin/sh\n"
                + $"if [ \"$1\" = \"--version\" ]; then echo {npxVersion}; exit 0; fi\n"
                + "case \" $* \" in\n"
                + " *--loglevel=http*)\n"
                + output
                + (lines.Exit < 0 ? "  sleep 120\n" : "")
                + $"  exit {Math.Max(lines.Exit, 0)};;\n"
                + "esac\n"
                + string.Concat((agentEnv ?? new Dictionary<string, string>()).Select(e => $"export {e.Key}='{e.Value}'\n"))
                + $"exec '{dotnet}' '{agentDll}' \"$@\"\n");
        }

        return folder;
    }

    /// <summary>A stand-in program that prints <paramref name="output"/> and exits with <paramref name="exit"/> (an agent's CLI, apt-cache).</summary>
    public static void Program(string folder, string name, string output, int exit = 0)
    {
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(folder, name + ".cmd"), "@echo off\r\n" + string.Concat(output.Split('\n').Select(l => $"echo {l}\r\n")) + $"exit /b {exit}\r\n");
            return;
        }

        Write(Path.Combine(folder, name), "#!/bin/sh\n" + string.Concat(output.Split('\n').Select(l => $"echo '{l}'\n")) + $"exit {exit}\n");
    }

    private static void Write(string file, string text)
    {
        File.WriteAllText(file, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
