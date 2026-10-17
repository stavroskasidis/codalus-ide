namespace Codalus.Projects.Host;

/// <summary>WinForms and WPF projects in the build host (M40a, ADR 0071). No MSBuild types, so the tests compile it in.</summary>
internal static class WindowsDesktop
{
    public const string WindowsTargetingProperty = "EnableWindowsTargeting";

    /// <summary>
    /// On Linux and macOS the SDK refuses to restore or build a project that targets Windows (NETSDK1100) unless
    /// <c>EnableWindowsTargeting</c> is true. The build host sets it as an environment variable, the lowest-priority
    /// source of MSBuild properties, so its evaluations, design-time builds and builds, and the <c>dotnet restore</c> it
    /// starts, load such projects with IntelliSense, while a project or user that sets it keeps its value. Running them
    /// stays Windows only (the shell says so).
    /// </summary>
    public static void EnableWindowsTargeting() => EnableWindowsTargeting(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

    internal static bool EnableWindowsTargeting(bool windows, Func<string, string?> get, Action<string, string?> set)
    {
        if (windows || !string.IsNullOrEmpty(get(WindowsTargetingProperty)))
        {
            return false; // Windows doesn't need it; a value set already wins
        }

        set(WindowsTargetingProperty, "true");
        return true;
    }

    /// <summary>
    /// The global property MSBuild imports a file from right after the project's package targets and
    /// <c>Directory.Build.targets</c> (Microsoft.Common.targets), so a target defined there replaces a package's.
    /// </summary>
    public const string CustomAfterDirectoryBuildTargets = "CustomAfterDirectoryBuildTargets";

    /// <summary>
    /// What a WinUI 3 project's design-time build imports off Windows (M44, ADR 0074): an empty
    /// <c>DesignTimeMarkupCompilation</c>. WinUI's XAML compiler is a .NET Framework program that the SDK runs as an
    /// executable, which fails on Linux and macOS and with it the whole design-time build; without it the project loads
    /// with its references (IntelliSense, WinUI's XAML language service), just without the compiler's generated code.
    /// </summary>
    internal const string WinUIDesignTimeTargetsText = """
        <Project>
          <!-- Written by Codalus IDE's build host (M44): WinUI's XAML compiler runs only on Windows. -->
          <Target Name="DesignTimeMarkupCompilation" />
        </Project>
        """;

    /// <summary>The file of <see cref="WinUIDesignTimeTargetsText"/> (written once), or null on Windows, where the compiler runs.</summary>
    public static string? WinUIDesignTimeTargets() => WinUIDesignTimeTargets(OperatingSystem.IsWindows(), Path.Combine(Path.GetTempPath(), "codalus-buildhost"));

    internal static string? WinUIDesignTimeTargets(bool windows, string directory)
    {
        if (windows)
        {
            return null;
        }

        var path = Path.Combine(directory, "Codalus.WinUI.DesignTime.targets");
        lock (s_writeGate)
        {
            try
            {
                if (!File.Exists(path) || File.ReadAllText(path) != WinUIDesignTimeTargetsText)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(path, WinUIDesignTimeTargetsText);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // the design-time build then fails as before, saying why
            }
        }

        return path;
    }

    private static readonly object s_writeGate = new();

    /// <summary>
    /// WPF's and WinUI's XAML: unlike Avalonia's and .NET MAUI's targets, theirs don't make the project's <c>Page</c>
    /// and <c>ApplicationDefinition</c> items additional files, so the C# host wouldn't know which project a
    /// <c>.xaml</c> file belongs to. The design-time build adds them to the compiler's command line as
    /// <c>/additionalfile:</c> (the real build's command line stays the SDK's).
    /// </summary>
    internal static string[] WithWpfXaml(string[] arguments, IEnumerable<string> xamlFiles)
    {
        var present = arguments
            .Where(a => a.StartsWith("/additionalfile:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a["/additionalfile:".Length..].Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = xamlFiles
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && present.Add(f))
            .Select(f => $"/additionalfile:{f}")
            .ToList();
        return added.Count == 0 ? arguments : [.. arguments, .. added];
    }
}
