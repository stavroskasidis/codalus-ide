namespace Codalus.Workbench.Debugging;

/// <summary>A Chromium browser js-debug can drive (ADR 0026). <see cref="LaunchType"/> is js-debug's launch type.</summary>
public sealed record Browser(string Kind, string Path)
{
    public string LaunchType => Kind == Browsers.Edge ? "pwa-msedge" : "pwa-chrome";

    public string Display => Kind switch
    {
        Browsers.Chrome => "Chrome",
        Browsers.Edge => "Edge",
        Browsers.Chromium => "Chromium",
        _ => System.IO.Path.GetFileNameWithoutExtension(Path),
    };
}

/// <summary>
/// Finds the browser for browser debugging (ADR 0026): Chrome, then Edge, then Chromium at their usual install places,
/// or the one the setting <see cref="SettingKey"/> names (<c>auto</c>, <c>chrome</c>, <c>edge</c>, <c>chromium</c> or a
/// path). Firefox and Safari aren't supported: js-debug needs the Chrome DevTools Protocol.
/// </summary>
public static class Browsers
{
    public const string SettingKey = "debug.browser";

    /// <summary><c>1</c> runs the browser headless (tests and CI).</summary>
    public const string HeadlessVariable = "CODALUS_BROWSER_HEADLESS";

    public const string Chrome = "chrome";
    public const string Edge = "edge";
    public const string Chromium = "chromium";

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
            // A path: Edge when it looks like Edge, otherwise a Chrome-like browser.
            return File.Exists(setting)
                ? new Browser(setting.Contains("edge", StringComparison.OrdinalIgnoreCase) ? Edge : Chrome, setting)
                : null;
        }

        return Candidates()
            .Where(c => setting == "auto" || c.Kind == setting)
            .Select(c => (c.Kind, Path: Resolve(c.Path)))
            .Where(c => c.Path is not null)
            .Select(c => new Browser(c.Kind, c.Path!))
            .FirstOrDefault();
    }

    /// <summary>Usual install places, most preferred first. Bare names are looked up on the PATH.</summary>
    private static IEnumerable<(string Kind, string Path)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { programFiles, programFilesX86, local })
            {
                yield return (Chrome, System.IO.Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }

            foreach (var root in new[] { programFilesX86, programFiles, local })
            {
                yield return (Edge, System.IO.Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }

            yield return (Chromium, System.IO.Path.Combine(local, "Chromium", "Application", "chrome.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { "/Applications", System.IO.Path.Combine(home, "Applications") })
            {
                yield return (Chrome, System.IO.Path.Combine(root, "Google Chrome.app", "Contents", "MacOS", "Google Chrome"));
                yield return (Edge, System.IO.Path.Combine(root, "Microsoft Edge.app", "Contents", "MacOS", "Microsoft Edge"));
                yield return (Chromium, System.IO.Path.Combine(root, "Chromium.app", "Contents", "MacOS", "Chromium"));
            }
        }
        else
        {
            yield return (Chrome, "google-chrome");
            yield return (Chrome, "google-chrome-stable");
            yield return (Edge, "microsoft-edge");
            yield return (Edge, "microsoft-edge-stable");
            yield return (Chromium, "chromium");
            yield return (Chromium, "chromium-browser");
        }
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
}
