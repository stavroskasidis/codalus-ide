namespace Codalus.Projects.Host;

/// <summary>WinForms and WPF projects in the build host (M40a, ADR 0069). No MSBuild types, so the tests compile it in.</summary>
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
    /// WPF's XAML: unlike Avalonia's and .NET MAUI's targets, WPF's don't make the project's <c>Page</c> and
    /// <c>ApplicationDefinition</c> items additional files, so the C# host wouldn't know which project a <c>.xaml</c>
    /// file belongs to. The design-time build adds them to the compiler's command line as <c>/additionalfile:</c> (the
    /// real build's command line stays the SDK's).
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
