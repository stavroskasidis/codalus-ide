using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Projects;

/// <summary>
/// WinForms and WPF projects, and others that target Windows (M40a, ADR 0071, D31): they open, edit and build on
/// every OS, but run, debug, test and Hot Reload only on Windows, so elsewhere the IDE says so instead of failing.
/// </summary>
public static class WindowsDesktopProjects
{
    /// <summary>A target framework that targets Windows: <c>net8.0-windows</c>, <c>net10.0-windows10.0.19041.0</c>.</summary>
    public static bool IsWindowsFramework(string? targetFramework) =>
        targetFramework is { } tfm && tfm.IndexOf('-', StringComparison.Ordinal) is var dash and > 0
        && tfm[(dash + 1)..].StartsWith("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the project's programs run only on Windows: a WPF or Windows Forms project, one whose every target
    /// framework targets Windows, or a .NET Framework project (M43a: the desktop CLR; Mono is a later decision).
    /// </summary>
    public static bool RunsOnlyOnWindows(ProjectInfo project) =>
        project.UsesWpf || project.UsesWindowsForms || project.UsesWinUI || (project.TargetFrameworks.Length > 0 && project.TargetFrameworks.All(IsWindowsFramework))
        || NetFrameworkProjects.IsNetFramework(project);

    /// <summary>
    /// Whether the project builds only on Windows: a WinUI 3 project (M44), whose XAML compiler is a Windows program.
    /// WPF and Windows Forms build everywhere with <c>EnableWindowsTargeting</c>.
    /// </summary>
    public static bool BuildsOnlyOnWindows(ProjectInfo project) => project.UsesWinUI;

    /// <summary>What the project is, for messages: "WPF", "Windows Forms", "WinUI 3", or null for another Windows project.</summary>
    public static string? Kind(ProjectInfo project) =>
        project.UsesWpf && project.UsesWindowsForms ? "WPF and Windows Forms"
        : project.UsesWpf ? "WPF"
        : project.UsesWindowsForms ? "Windows Forms"
        : project.UsesWinUI ? "WinUI 3"
        : null;

    /// <summary>Whether a project can't run on this machine: it runs only on Windows, and this isn't Windows.</summary>
    public static bool CannotRunHere(ProjectInfo project) => CannotRunOn(project, OperatingSystem.IsWindows());

    public static bool CannotRunOn(ProjectInfo project, bool windows) => !windows && RunsOnlyOnWindows(project);
}
