using System.Diagnostics;

namespace Daedalus.Platform;

/// <summary>Hands work to the OS shell (default browser, file manager).</summary>
public static class ShellIntegration
{
    public static void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https" or "file" or "mailto"))
        {
            throw new ArgumentException($"Refusing to open a '{url.Scheme}' URL.", nameof(url));
        }

        Start(PlatformInfo.Current switch
        {
            PlatformKind.Windows => new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true },
            PlatformKind.MacOS => Args("open", url.AbsoluteUri),
            _ => Args("xdg-open", url.AbsoluteUri),
        });
    }

    /// <summary>Shows a file or folder in Explorer, Finder or the default Linux file manager.</summary>
    public static void RevealInFileManager(string path)
    {
        var full = Path.GetFullPath(path);
        Start(PlatformInfo.Current switch
        {
            PlatformKind.Windows => Args("explorer.exe", $"/select,{full}"),
            PlatformKind.MacOS => Args("open", "-R", full),
            _ => Args("xdg-open", Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full),
        });
    }

    private static ProcessStartInfo Args(string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
    }

    private static void Start(ProcessStartInfo psi)
    {
        using var _ = Process.Start(psi);
    }
}
