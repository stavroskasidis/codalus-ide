using Codalus.Projects.Protocol;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// WinForms and WPF projects (M40a, ADR 0071, D31): they open, edit and build on every OS, and run, debug, test and
/// Hot Reload only on Windows. On Linux and macOS, Start and Start Without Debugging say so instead of failing.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>
    /// When <paramref name="project"/> runs only on Windows and this isn't Windows (and the window isn't remote), says
    /// so and returns true: the caller doesn't build or start it.
    /// </summary>
    public bool WarnIfWindowsOnly(string project)
    {
        if (WindowsOnlyReason(project) is not { } reason)
        {
            return false;
        }

        Workbench.Notifications.Warning(reason);
        return true;
    }

    /// <summary>
    /// When <paramref name="project"/> builds only on Windows (a WinUI 3 project, M44) and this isn't Windows, says so and
    /// returns true: the caller doesn't build it (the build would fail in WinUI's XAML compiler).
    /// </summary>
    public bool WarnIfBuildsOnlyOnWindows(string? project)
    {
        if (Remote is not null || OperatingSystem.IsWindows() || project is null || !Solutions.Projects.TryGetValue(project, out var info) || !WindowsDesktopProjects.BuildsOnlyOnWindows(info))
        {
            return false;
        }

        Workbench.Notifications.Warning(WindowsOnlyMessage(info));
        return true;
    }

    /// <summary>Why <paramref name="project"/> can't run here (a WPF or Windows Forms app off Windows), or null.</summary>
    public string? WindowsOnlyReason(string project) =>
        Remote is null && Solutions.Projects.TryGetValue(project, out var info) && WindowsDesktopProjects.CannotRunHere(info) ? WindowsOnlyMessage(info) : null;

    internal static string WindowsOnlyMessage(ProjectInfo info) => WindowsDesktopProjects.BuildsOnlyOnWindows(info)
        ? Localizer.Format("msg.windowsDesktop.winUIWindowsOnly", "{0} is a WinUI 3 project: it builds, runs and debugs only on Windows (WinUI's XAML compiler is a Windows program). On this OS it opens and edits, with XAML IntelliSense.", info.Name)
        : NetFrameworkProjects.IsNetFramework(info)
        ? Localizer.Format("msg.netFramework.windowsOnly", "{0} targets .NET Framework ({1}): it runs, debugs and tests only on Windows (running it on Mono isn't supported). On this OS it opens, edits and builds.", info.Name, string.Join(", ", info.TargetFrameworks))
        : WindowsDesktopProjects.Kind(info) is { } kind
        ? Localizer.Format("msg.windowsDesktop.windowsOnly", "{0} is a {1} project: it runs, debugs and tests only on Windows. On this OS it opens, edits and builds.", info.Name, kind)
        : Localizer.Format("msg.windowsDesktop.windowsOnlyFramework", "{0} targets Windows ({1}): it runs, debugs and tests only on Windows. On this OS it opens, edits and builds.", info.Name, string.Join(", ", info.TargetFrameworks));
}
