using System.Diagnostics;

namespace Codalus.Workbench.Setup;

/// <summary>
/// The user's login shell's <c>PATH</c>. An app started from the macOS Finder or Dock gets only the system's minimal
/// <c>PATH</c> (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>), without <c>/usr/local/share/dotnet</c>, Homebrew or the user's
/// own folders, which only login shells add (<c>path_helper</c>, <c>~/.zprofile</c>). On Linux, an app started from the
/// desktop (the AppImage, the packages' menu entry) gets the session's environment, which often lacks what the shell's
/// startup files add, such as a <c>~/.dotnet</c> from dotnet-install. So, like other IDEs, the shell asks the login
/// shell once at startup and puts its folders first.
/// </summary>
public static class LoginShellPath
{
    private const string Marker = "__CODALUS_PATH__";

    /// <summary>
    /// Whether to ask: on macOS and Linux, when the app wasn't started from a terminal (which already has the user's
    /// PATH), unless <c>CODALUS_NO_SHELL_PATH</c> is set.
    /// </summary>
    public static bool ShouldImport(Func<string, string?> environment) =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && environment("TERM") is null && environment("CODALUS_NO_SHELL_PATH") is null;

    /// <summary>
    /// Runs <paramref name="shell"/> as an interactive login shell and reads its <c>PATH</c>; null when it fails or takes
    /// longer than <paramref name="timeout"/> (a slow profile mustn't hold the IDE's start).
    /// </summary>
    public static string? Read(string shell, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var argument in new[] { "-ilc", $"printf '{Marker}%s{Marker}' \"$PATH\"" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var text = output.GetAwaiter().GetResult();
            var first = text.IndexOf(Marker, StringComparison.Ordinal);
            var last = text.LastIndexOf(Marker, StringComparison.Ordinal);
            return first >= 0 && last > first ? text[(first + Marker.Length)..last] : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The login shell's folders first, then the current ones it lacks, each once.</summary>
    public static string Merge(string? current, string login)
    {
        var folders = new List<string>();
        foreach (var folder in login.Split(':').Concat((current ?? string.Empty).Split(':')))
        {
            if (folder.Length > 0 && !folders.Contains(folder, StringComparer.Ordinal))
            {
                folders.Add(folder);
            }
        }

        return string.Join(':', folders);
    }

    /// <summary>Asks the login shell (<c>SHELL</c>, else zsh on macOS and bash on Linux) and sets this process's <c>PATH</c>; returns whether it did.</summary>
    public static bool Import()
    {
        if (!ShouldImport(Environment.GetEnvironmentVariable))
        {
            return false;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } configured && File.Exists(configured) ? configured
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        if (Read(shell, TimeSpan.FromSeconds(5)) is not { Length: > 0 } login)
        {
            return false;
        }

        Environment.SetEnvironmentVariable("PATH", Merge(Environment.GetEnvironmentVariable("PATH"), login));
        return true;
    }
}
