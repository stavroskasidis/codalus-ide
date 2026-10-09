namespace DaedalusStudio.Platform;

/// <summary>
/// Where Daedalus keeps its files, following each OS's conventions:
/// <list type="bullet">
/// <item>Windows: roaming config in <c>%APPDATA%</c>; data, state, cache and logs in <c>%LOCALAPPDATA%</c>.</item>
/// <item>macOS: <c>~/Library/Application Support</c>, <c>~/Library/Caches</c> and <c>~/Library/Logs</c>.</item>
/// <item>Linux: the XDG base directories (<c>XDG_CONFIG_HOME</c>, <c>XDG_DATA_HOME</c>, <c>XDG_STATE_HOME</c>,
/// <c>XDG_CACHE_HOME</c>) with their specified defaults.</item>
/// </list>
/// <c>DAEDALUS_USER_DATA_DIR</c> (or <c>--user-data-dir</c>) puts everything under one root, for tests,
/// benchmarks and portable installs.
/// </summary>
public sealed record AppPaths(string Config, string Data, string State, string Cache, string Logs)
{
    public const string UserDataDirVariable = "DAEDALUS_USER_DATA_DIR";
    private const string ProductFolder = "Daedalus Studio";
    private const string XdgFolder = "daedalus-studio";

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string KeybindingsFile => Path.Combine(Config, "keybindings.json");

    public string LayoutFile => Path.Combine(State, "layout.json");

    public string PluginsDirectory => Path.Combine(Data, "plugins");

    public static AppPaths Default { get; } = Resolve(PlatformInfo.Current, Environment.GetEnvironmentVariable, null);

    /// <summary>Resolves the paths for <paramref name="platform"/>; <paramref name="getEnv"/> is injectable for tests.</summary>
    public static AppPaths Resolve(PlatformKind platform, Func<string, string?> getEnv, string? userDataDir)
    {
        var root = userDataDir ?? Nonempty(getEnv(UserDataDirVariable));
        if (root is not null)
        {
            root = Path.GetFullPath(root);
            return new AppPaths(
                Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "state"),
                Path.Combine(root, "cache"), Path.Combine(root, "logs"));
        }

        var home = Nonempty(getEnv("HOME")) ?? Nonempty(getEnv("USERPROFILE")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        switch (platform)
        {
            case PlatformKind.Windows:
            {
                var roaming = Nonempty(getEnv("APPDATA")) ?? Path.Combine(home, "AppData", "Roaming");
                var local = Nonempty(getEnv("LOCALAPPDATA")) ?? Path.Combine(home, "AppData", "Local");
                var localRoot = Path.Combine(local, ProductFolder);
                return new AppPaths(
                    Path.Combine(roaming, ProductFolder), localRoot, Path.Combine(localRoot, "State"),
                    Path.Combine(localRoot, "Cache"), Path.Combine(localRoot, "Logs"));
            }

            case PlatformKind.MacOS:
            {
                var support = Path.Combine(home, "Library", "Application Support", ProductFolder);
                return new AppPaths(
                    support, support, Path.Combine(support, "State"),
                    Path.Combine(home, "Library", "Caches", ProductFolder), Path.Combine(home, "Library", "Logs", ProductFolder));
            }

            default:
            {
                string Xdg(string variable, params string[] fallback) =>
                    Path.Combine(Nonempty(getEnv(variable)) is { } dir && Path.IsPathRooted(dir) ? dir : Path.Combine([home, .. fallback]), XdgFolder);
                var state = Xdg("XDG_STATE_HOME", ".local", "state");
                return new AppPaths(
                    Xdg("XDG_CONFIG_HOME", ".config"), Xdg("XDG_DATA_HOME", ".local", "share"), state,
                    Xdg("XDG_CACHE_HOME", ".cache"), Path.Combine(state, "logs"));
            }
        }
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Config, Data, State, Cache, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
