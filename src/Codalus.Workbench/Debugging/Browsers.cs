namespace Codalus.Workbench.Debugging;

/// <summary>
/// A Chromium browser js-debug can drive (ADR 0026). <see cref="LaunchType"/> is js-debug's launch type.
/// <see cref="FlatpakApp"/> is the application ID of a browser installed from Flatpak (Linux), whose <see cref="Path"/>
/// is its exported launcher; such a browser starts through <see cref="Executable"/>.
/// </summary>
public sealed record Browser(string Kind, string Path, string? FlatpakApp = null)
{
    public string LaunchType => Kind == Browsers.Edge ? "pwa-msedge" : "pwa-chrome";

    public string Display => Kind switch
    {
        Browsers.Chrome => "Chrome",
        Browsers.Edge => "Edge",
        Browsers.Chromium => "Chromium",
        _ => System.IO.Path.GetFileNameWithoutExtension(Path),
    };

    /// <summary>Where the browser is, for the Debug Console: its path, or the Flatpak it runs from.</summary>
    public string Location => FlatpakApp is { } app ? "Flatpak " + app : Path;

    /// <summary>
    /// The browser's version from its executable's version resource (Windows), or null: for the Debug Console, so a
    /// launch that fails after a browser update says which version it was.
    /// </summary>
    public string? Version
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return System.Diagnostics.FileVersionInfo.GetVersionInfo(Path).ProductVersion is { Length: > 0 } version ? version : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The executable that starts the browser on the profile in <paramref name="profileFolder"/>, which it creates:
    /// <see cref="Path"/>, or for a Flatpak a launcher script in that folder. The script runs the browser with access to
    /// the folder, which its sandbox doesn't otherwise see (the home folder and the host's <c>/tmp</c> are outside it), so
    /// the browser can keep its profile there and the IDE can read its <c>DevToolsActivePort</c>.
    /// <paramref name="pageFolder"/>, the folder of a page opened from a file, is given to it read-only.
    /// </summary>
    public string Executable(string profileFolder, string? pageFolder = null)
    {
        if (FlatpakApp is not { } app || !OperatingSystem.IsLinux())
        {
            return Path;
        }

        profileFolder = System.IO.Path.GetFullPath(profileFolder);
        Directory.CreateDirectory(profileFolder);
        var launcher = System.IO.Path.Combine(profileFolder, Browsers.FlatpakLauncherName);
        var script = Browsers.FlatpakLauncher(Browsers.FlatpakCommand() ?? "flatpak", app, profileFolder, pageFolder is null ? null : System.IO.Path.GetFullPath(pageFolder));
        if (!File.Exists(launcher) || File.ReadAllText(launcher) != script)
        {
            File.WriteAllText(launcher, script);
        }

        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return launcher;
    }
}

/// <summary>
/// Finds the browser for browser debugging (ADR 0026): Chrome, then Edge, then Chromium at their usual install places
/// (on Linux each one installed natively, then from Flatpak), or the one the setting <see cref="SettingKey"/> names
/// (<c>auto</c>, <c>chrome</c>, <c>edge</c>, <c>chromium</c> or a path). Firefox and Safari aren't supported: js-debug
/// needs the Chrome DevTools Protocol.
/// </summary>
public static class Browsers
{
    public const string SettingKey = "debug.browser";

    /// <summary><c>1</c> runs the browser headless (tests and CI).</summary>
    public const string HeadlessVariable = "CODALUS_BROWSER_HEADLESS";

    public const string Chrome = "chrome";
    public const string Edge = "edge";
    public const string Chromium = "chromium";

    /// <summary>The launcher <see cref="Browser.Executable"/> writes into a Flatpak browser's profile folder.</summary>
    internal const string FlatpakLauncherName = "codalus-flatpak-launcher.sh";

    /// <summary>The Flathub applications of each kind.</summary>
    private static readonly (string Kind, string App)[] s_flatpaks =
    [
        (Chrome, "com.google.Chrome"),
        (Edge, "com.microsoft.Edge"),
        (Chromium, "org.chromium.Chromium"),
    ];

    public static bool Headless => Environment.GetEnvironmentVariable(HeadlessVariable) == "1";

    /// <summary>A folder for vscode-js-debug's logs, one per session (diagnostics).</summary>
    public const string TraceVariable = "CODALUS_JSDEBUG_TRACE";

    /// <summary>The log file of a new browser session when <see cref="TraceVariable"/> names a folder; else null.</summary>
    public static string? TraceFile() =>
        Environment.GetEnvironmentVariable(TraceVariable) is { Length: > 0 } folder
            ? Path.Combine(Directory.CreateDirectory(folder).FullName, $"js-debug-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log")
            : null;

    /// <summary>The browser for <paramref name="setting"/>, or null when none is installed.</summary>
    public static Browser? Find(string? setting = "auto")
    {
        setting = string.IsNullOrWhiteSpace(setting) ? "auto" : setting.Trim();
        if (setting is not ("auto" or Chrome or Edge or Chromium))
        {
            // A path: a Flatpak's exported launcher, Edge when it looks like Edge, otherwise a Chrome-like browser.
            if (!File.Exists(setting))
            {
                return null;
            }

            if (FlatpakAppOf(setting) is { } app)
            {
                var kind = s_flatpaks.FirstOrDefault(f => f.App == app).Kind ?? (app.Contains("edge", StringComparison.OrdinalIgnoreCase) ? Edge : Chrome);
                return new Browser(kind, setting, app);
            }

            return new Browser(setting.Contains("edge", StringComparison.OrdinalIgnoreCase) ? Edge : Chrome, setting);
        }

        return Candidates()
            .Where(c => setting == "auto" || c.Kind == setting)
            .Select(c => (c.Kind, Path: Resolve(c.Path), c.FlatpakApp))
            .Where(c => c.Path is not null && (c.FlatpakApp is null || FlatpakCommand() is not null))
            .Select(c => new Browser(c.Kind, c.Path!, c.FlatpakApp))
            .FirstOrDefault();
    }

    /// <summary>
    /// The script that starts the Flatpak <paramref name="app"/> with read and write access to
    /// <paramref name="profileFolder"/> (and read access to <paramref name="pageFolder"/>), passing its own arguments on.
    /// </summary>
    internal static string FlatpakLauncher(string flatpak, string app, string profileFolder, string? pageFolder = null) =>
        "#!/bin/sh\n"
        + "# Written by Codalus IDE: starts a Flatpak browser with access to its profile folder (debug.browser).\n"
        + $"exec {ShellQuote(flatpak)} run {ShellQuote("--filesystem=" + profileFolder)} "
        + (pageFolder is null ? string.Empty : ShellQuote("--filesystem=" + pageFolder + ":ro") + " ")
        + $"{ShellQuote(app)} \"$@\"\n";

    /// <summary>The <c>flatpak</c> command on the PATH, or null.</summary>
    internal static string? FlatpakCommand() => OperatingSystem.IsLinux() ? Resolve("flatpak") : null;

    /// <summary>
    /// The application ID when <paramref name="path"/> is a launcher Flatpak exports
    /// (<c>…/flatpak/exports/bin/com.microsoft.Edge</c>), else null.
    /// </summary>
    internal static string? FlatpakAppOf(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        return folder is not null && folder.EndsWith("/flatpak/exports/bin", StringComparison.Ordinal) && Path.GetFileName(full) is { Length: > 0 } app
            ? app
            : null;
    }

    /// <summary>Usual install places, most preferred first. Bare names are looked up on the PATH.</summary>
    private static IEnumerable<(string Kind, string Path, string? FlatpakApp)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { programFiles, programFilesX86, local })
            {
                yield return (Chrome, System.IO.Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"), null);
            }

            foreach (var root in new[] { programFilesX86, programFiles, local })
            {
                yield return (Edge, System.IO.Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"), null);
            }

            yield return (Chromium, System.IO.Path.Combine(local, "Chromium", "Application", "chrome.exe"), null);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { "/Applications", System.IO.Path.Combine(home, "Applications") })
            {
                yield return (Chrome, System.IO.Path.Combine(root, "Google Chrome.app", "Contents", "MacOS", "Google Chrome"), null);
                yield return (Edge, System.IO.Path.Combine(root, "Microsoft Edge.app", "Contents", "MacOS", "Microsoft Edge"), null);
                yield return (Chromium, System.IO.Path.Combine(root, "Chromium.app", "Contents", "MacOS", "Chromium"), null);
            }
        }
        else
        {
            (string Kind, string[] Names)[] native =
            [
                (Chrome, ["google-chrome", "google-chrome-stable"]),
                (Edge, ["microsoft-edge", "microsoft-edge-stable"]),
                (Chromium, ["chromium", "chromium-browser"]),
            ];
            foreach (var (kind, names) in native)
            {
                foreach (var name in names)
                {
                    yield return (kind, name, null);
                }

                // From Flatpak: the per-user installation, then the system's.
                var app = s_flatpaks.First(f => f.Kind == kind).App;
                foreach (var exports in FlatpakExports())
                {
                    yield return (kind, System.IO.Path.Combine(exports, app), app);
                }
            }
        }
    }

    /// <summary>The folders Flatpak exports its applications' launchers to: the user's installation's, then the system's.</summary>
    private static IEnumerable<string> FlatpakExports()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathRooted(data)
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        yield return Path.Combine(dataHome, "flatpak", "exports", "bin");
        yield return "/var/lib/flatpak/exports/bin";
    }

    private static string? Resolve(string path)
    {
        if (System.IO.Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => System.IO.Path.Combine(directory, path))
            .FirstOrDefault(File.Exists);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
