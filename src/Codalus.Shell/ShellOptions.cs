using System.Reflection;

namespace Codalus.Shell;

/// <summary>The command line of <c>codalus</c>; see <see cref="HelpText"/>.</summary>
public sealed record ShellOptions
{
    public const string HelpText = """
        Usage: codalus [path] [options]
               codalus diff <left> <right> [options]   Compare two files (in the running window, if there is one).
               codalus merge <local> <remote> <base> <merged> [options]
                                                        Resolve a merge (git mergetool); exits when it is accepted.
               codalus --wait <file> [options]         Edit a file and exit when its tab closes (git's core.editor).

          --user-data-dir <dir>   Keep settings, state and plugins under <dir> instead of the OS defaults.
          --locale <name>         The display language for this run (qps-ploc: pseudo-language).
          --plugin-dir <dir>      Load plugins from <dir> (a plugin folder or a folder of plugins). Repeatable.
          --enable-proposed-api   Allow plugins to use proposed APIs (development builds only).
          --debug-plugin-host <file>  Run the --plugin-dir plugins in a plugin host that writes its process id to <file> and waits for a debugger.
          --dev                   Development mode: shows developer commands.
          --open <file>           Open a file in the editor.
          --remote <target>       Open [path] on another machine: ssh:user@host[:port] or wsl:<distribution>.
          --version               Print the version and exit.
          --help                  Print this help and exit.
        """;

    /// <summary>A solution, project or folder to open.</summary>
    public string? Path { get; init; }

    public string? UserDataDir { get; init; }

    /// <summary>Whether the start window may open when nothing else is to be opened (M38; tests turn it off).</summary>
    public bool StartWindow { get; init; } = true;

    /// <summary>The display language for this run (M23b), over the setting.</summary>
    public string? Locale { get; init; }

    public IReadOnlyList<string> PluginDirs { get; init; } = [];

    public bool EnableProposedApi { get; init; }

    /// <summary>
    /// <c>--debug-plugin-host &lt;file&gt;</c> (M22b, from Debug Plugin in Development Instance): the <c>--plugin-dir</c>
    /// plugins run in a plugin host of their own that writes its process id to the file and waits for a debugger.
    /// </summary>
    public string? DebugPluginHost { get; init; }

    public bool Development { get; init; }

    public bool ShowVersion { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>
    /// Benchmarks: exit when the window is <c>interactive</c>, the opened file shows its first semantic
    /// <c>highlight</c>, the <c>--bench</c> scenario is <c>done</c>, or the C# <c>language</c> service is ready.
    /// </summary>
    public string? ExitOn { get; init; }

    /// <summary><c>codalus diff left right</c>: two files to compare side by side.</summary>
    public (string Left, string Right)? Diff { get; init; }

    /// <summary><c>codalus merge local remote base merged</c>: git mergetool's four files.</summary>
    public (string Local, string Remote, string Base, string Merged)? Merge { get; init; }

    /// <summary><c>--wait file</c>: edit the file and exit (or answer) when its tab closes.</summary>
    public string? WaitFile { get; init; }

    /// <summary>
    /// <c>--update-now</c>: check the update channel, download what it has and have it applied once this process exits,
    /// without a window (M32; the update test script, eng/test-update.sh). Exit codes: 0 updated, 3 up to date,
    /// 2 failed, 4 not an install that updates itself.
    /// </summary>
    public bool UpdateNow { get; init; }

    /// <summary>
    /// <c>--remote ssh:host</c> or <c>wsl:Ubuntu</c> (M21): this window connects there and opens <see cref="Path"/>, a
    /// path on that machine.
    /// </summary>
    public string? Remote { get; init; }

    /// <summary>A file to open in the editor (<c>--open</c>).</summary>
    public string? OpenFile { get; init; }

    /// <summary>Benchmarks: a self-driven scenario on <see cref="OpenFile"/> (<c>--bench typing|scroll</c>).</summary>
    public string? Bench { get; init; }

    /// <summary>Writes a screenshot of the window once it is interactive (<c>--screenshot file.png</c>).</summary>
    public string? Screenshot { get; init; }

    /// <summary>
    /// CI: runs the end-to-end smoke test (<see cref="Infrastructure.SmokeTest"/>) on fixtures copied from this samples
    /// folder, writes a report to the logs folder and exits with 0 when every step passed (<c>--smoke-test samples</c>).
    /// </summary>
    public string? SmokeTest { get; init; }

    /// <summary>Where the built-in plugins live; defaults to <c>plugins/</c> next to the executable.</summary>
    public string? BuiltInPluginsDir { get; init; }

    /// <summary>Watch settings.json and keybindings.json for external edits (off in tests).</summary>
    public bool WatchFiles { get; init; } = true;

    /// <summary>Problems found while parsing, reported once the window is up.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ShellOptions Parse(IReadOnlyList<string> args)
    {
        var options = new ShellOptions();
        var pluginDirs = new List<string>();
        var errors = new List<string>();
        var start = 0;
        if (args.Count > 0 && args[0] == "merge")
        {
            if (args.Count >= 5 && args.Skip(1).Take(4).All(a => !a.StartsWith("--", StringComparison.Ordinal)))
            {
                options = options with { Merge = (System.IO.Path.GetFullPath(args[1]), System.IO.Path.GetFullPath(args[2]), System.IO.Path.GetFullPath(args[3]), System.IO.Path.GetFullPath(args[4])) };
                start = 5;
            }
            else
            {
                errors.Add("'merge' needs four files: codalus merge <local> <remote> <base> <merged>.");
                start = args.Count;
            }
        }
        else if (args.Count > 0 && args[0] == "diff")
        {
            if (args.Count >= 3 && !args[1].StartsWith("--", StringComparison.Ordinal) && !args[2].StartsWith("--", StringComparison.Ordinal))
            {
                options = options with { Diff = (System.IO.Path.GetFullPath(args[1]), System.IO.Path.GetFullPath(args[2])) };
                start = 3;
            }
            else
            {
                errors.Add("'diff' needs two files: codalus diff <left> <right>.");
                start = args.Count;
            }
        }

        for (var i = start; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return args[++i];
                }

                errors.Add($"Option '{arg}' needs a value.");
                return null;
            }

            switch (arg)
            {
                case "--user-data-dir":
                    options = options with { UserDataDir = Value() };
                    break;
                case "--locale":
                    options = options with { Locale = Value() };
                    break;
                case "--plugin-dir":
                    if (Value() is { } dir)
                    {
                        pluginDirs.Add(System.IO.Path.GetFullPath(dir));
                    }

                    break;
                case "--debug-plugin-host":
                    options = options with { DebugPluginHost = Value() is { } debugFile ? System.IO.Path.GetFullPath(debugFile) : null };
                    break;
                case "--enable-proposed-api":
                    options = options with { EnableProposedApi = true };
                    break;
                case "--dev":
                    options = options with { Development = true };
                    break;
                case "--version" or "-v":
                    options = options with { ShowVersion = true };
                    break;
                case "--help" or "-h" or "-?":
                    options = options with { ShowHelp = true };
                    break;
                case "--exit-on":
                    options = options with { ExitOn = Value() };
                    break;
                case "--wait":
                    options = options with { WaitFile = Value() is { } waited ? System.IO.Path.GetFullPath(waited) : null };
                    break;
                case "--open":
                    options = options with { OpenFile = Value() is { } file ? System.IO.Path.GetFullPath(file) : null };
                    break;
                case "--remote":
                    options = options with { Remote = Value() };
                    break;
                case "--bench":
                    options = options with { Bench = Value() };
                    break;
                case "--screenshot":
                    options = options with { Screenshot = Value() };
                    break;
                case "--update-now":
                    options = options with { UpdateNow = true };
                    break;
                case "--smoke-test":
                    options = options with { SmokeTest = Value() is { } samples ? System.IO.Path.GetFullPath(samples) : null };
                    break;
                case var other when other.StartsWith('-'):
                    errors.Add($"Unknown option '{other}'.");
                    break;
                default:
                    options = options with { Path = arg };
                    break;
            }
        }

        return options with { PluginDirs = pluginDirs, Errors = errors };
    }
}

public static class ShellInfo
{
    public const string ProductName = "Codalus IDE";

    /// <summary>The informational version without the source revision, e.g. <c>0.1.0-dev</c>.</summary>
    public static string Version { get; } =
        (typeof(ShellInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string VersionText =>
        $"{ProductName} {Version}\n.NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
}
