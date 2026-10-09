using System.Reflection;

namespace Daedalus.Shell;

/// <summary>The command line of <c>daedalus</c>; see <see cref="HelpText"/>.</summary>
public sealed record ShellOptions
{
    public const string HelpText = """
        Usage: daedalus [path] [options]

          --user-data-dir <dir>   Keep settings, state and plugins under <dir> instead of the OS defaults.
          --plugin-dir <dir>      Load plugins from <dir> (a plugin folder or a folder of plugins). Repeatable.
          --enable-proposed-api   Allow plugins to use proposed APIs (development builds only).
          --dev                   Development mode: shows developer commands.
          --open <file>           Open a file in the editor.
          --version               Print the version and exit.
          --help                  Print this help and exit.
        """;

    /// <summary>A solution, project or folder to open.</summary>
    public string? Path { get; init; }

    public string? UserDataDir { get; init; }

    public IReadOnlyList<string> PluginDirs { get; init; } = [];

    public bool EnableProposedApi { get; init; }

    public bool Development { get; init; }

    public bool ShowVersion { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>
    /// Benchmarks: exit when the window is <c>interactive</c>, the opened file shows its first semantic
    /// <c>highlight</c>, the <c>--bench</c> scenario is <c>done</c>, or the C# <c>language</c> service is ready.
    /// </summary>
    public string? ExitOn { get; init; }

    /// <summary>A file to open in the editor (<c>--open</c>).</summary>
    public string? OpenFile { get; init; }

    /// <summary>Benchmarks: a self-driven scenario on <see cref="OpenFile"/> (<c>--bench typing|scroll</c>).</summary>
    public string? Bench { get; init; }

    /// <summary>Writes a screenshot of the window once it is interactive (<c>--screenshot file.png</c>).</summary>
    public string? Screenshot { get; init; }

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
        for (var i = 0; i < args.Count; i++)
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
                case "--plugin-dir":
                    if (Value() is { } dir)
                    {
                        pluginDirs.Add(System.IO.Path.GetFullPath(dir));
                    }

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
                case "--open":
                    options = options with { OpenFile = Value() is { } file ? System.IO.Path.GetFullPath(file) : null };
                    break;
                case "--bench":
                    options = options with { Bench = Value() };
                    break;
                case "--screenshot":
                    options = options with { Screenshot = Value() };
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
    public const string ProductName = "Daedalus Studio";

    /// <summary>The informational version without the source revision, e.g. <c>0.1.0-dev</c>.</summary>
    public static string Version { get; } =
        (typeof(ShellInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string VersionText =>
        $"{ProductName} {Version}\n.NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
}
