using System.Reflection;

namespace Codalus.Shell;

/// <summary>The command line of <c>codalus</c>; see <see cref="HelpText"/>.</summary>
public sealed record ShellOptions
{
    public const string HelpText = """
        Usage: codalus [path[:line[:column]]] [options]
               codalus diff <left> <right> [options]   Compare two files (in the running window, if there is one).
               codalus merge <local> <remote> <base> <merged> [options]
                                                        Resolve a merge (git mergetool); exits when it is accepted.
               codalus --wait <file> [options]         Edit a file and exit when its tab closes (git's core.editor).
               codalus mcp [options]                   An MCP server on stdio for AI agents, reaching the running IDE
                                                        (turn on Tools > Options > AI > MCP Server first).

          --user-data-dir <dir>   Keep settings, state and plugins under <dir> instead of the OS defaults.
          --locale <name>         The display language for this run (qps-ploc: pseudo-language).
          --plugin-dir <dir>      Load plugins from <dir> (a plugin folder or a folder of plugins). Repeatable.
          --enable-proposed-api   Allow plugins to use proposed APIs (development builds only).
          --debug-plugin-host <file>  Run the --plugin-dir plugins in a plugin host that writes its process id to <file> and waits for a debugger.
          --dev                   Development mode: shows developer commands.
          --open <file>           Open a file in the editor.
          --new-window            Open a file in a new window, not in the running one.
          --skip-setup            Don't show the first-run setup (for automation; CODALUS_SKIP_SETUP=1 does the same).
          --list-plugins          Print the installed plugins and exit (--show-versions: with their versions).
          --install-plugin <id[@version] | file.nupkg>
                                  Install a plugin from the plugin galleries or a package file, and exit. Repeatable.
          --remote <target>       Open [path] on another machine: ssh:user@host[:port] or wsl:<distribution>.
          --version               Print the version and exit.
          --help                  Print this help and exit.
        """;

    /// <summary>A solution, project or folder to open.</summary>
    public string? Path { get; init; }

    public string? UserDataDir { get; init; }

    /// <summary>Whether the start window may open when nothing else is to be opened (M38; tests turn it off).</summary>
    public bool StartWindow { get; init; } = true;

    /// <summary>
    /// <c>--skip-setup</c> (M46): the first-run setup doesn't show at this start, for automation (tests turn it on too).
    /// It isn't recorded, so a later start without it still shows the setup when it hasn't run.
    /// </summary>
    public bool SkipSetup { get; init; }

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

    /// <summary><c>codalus mcp</c> (M28c): serve MCP on stdio, relaying to the running IDE.</summary>
    public bool Mcp { get; init; }

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

    /// <summary>A file to open in the editor (<c>--open</c>, or a path that is a file other than a solution or project).</summary>
    public string? OpenFile { get; init; }

    /// <summary>Where to put the caret in <see cref="OpenFile"/>: <c>codalus file:line[:column]</c> (zero-based here).</summary>
    public (int Line, int Column)? OpenAt { get; init; }

    /// <summary><c>--new-window</c>: a file opens in a new window instead of the running one.</summary>
    public bool NewWindow { get; init; }

    /// <summary><c>--list-plugins</c>: print the installed plugins and exit.</summary>
    public bool ListPlugins { get; init; }

    /// <summary><c>--show-versions</c>, with <c>--list-plugins</c>.</summary>
    public bool ShowVersions { get; init; }

    /// <summary><c>--install-plugin</c> (repeatable): plugin ids (with an optional <c>@version</c>) or <c>.nupkg</c> files to install.</summary>
    public IReadOnlyList<string> InstallPlugins { get; init; } = [];

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
        var installs = new List<string>();
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
        else if (args.Count > 0 && args[0] == "mcp")
        {
            options = options with { Mcp = true };
            start = 1;
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
                case "--new-window":
                    options = options with { NewWindow = true };
                    break;
                case "--skip-setup":
                    options = options with { SkipSetup = true };
                    break;
                case "--list-plugins":
                    options = options with { ListPlugins = true };
                    break;
                case "--show-versions":
                    options = options with { ShowVersions = true };
                    break;
                case "--install-plugin":
                    if (Value() is { } plugin)
                    {
                        installs.Add(plugin);
                    }

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
                    options = OpenPath(options, arg);
                    break;
            }
        }

        return options with { PluginDirs = pluginDirs, InstallPlugins = installs, Errors = errors };
    }

    /// <summary>
    /// The path argument: a solution, project or folder opens as one; another file opens in the editor, at
    /// <c>file:line[:column]</c> when that's how it's written (git, compilers and test runners print locations so).
    /// </summary>
    private static ShellOptions OpenPath(ShellOptions options, string argument)
    {
        if (options.Remote is not null || Directory.Exists(argument) || !File.Exists(argument) && LocationOf(argument) is null)
        {
            return options with { Path = argument };
        }

        var (file, at) = File.Exists(argument) ? (argument, null) : LocationOf(argument)!.Value;
        return Workbench.Projects.SolutionTree.IsSolutionOrProjectFile(file)
            ? options with { Path = file }
            : options with { OpenFile = System.IO.Path.GetFullPath(file), OpenAt = at };
    }

    /// <summary><c>file:line</c> or <c>file:line:column</c> (1-based) for an existing file, else null.</summary>
    public static (string File, (int Line, int Column)? At)? LocationOf(string argument)
    {
        var match = System.Text.RegularExpressions.Regex.Match(argument, @"^(?<file>.+?):(?<line>\d+)(?::(?<column>\d+))?$");
        if (!match.Success || !File.Exists(match.Groups["file"].Value))
        {
            return null;
        }

        var line = Math.Max(1, int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        var column = match.Groups["column"].Success ? Math.Max(1, int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture)) : 1;
        return (match.Groups["file"].Value, (line - 1, column - 1));
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
