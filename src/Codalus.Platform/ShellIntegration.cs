using System.Diagnostics;

namespace Codalus.Platform;

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

    /// <summary>
    /// Opens a page of Windows' Settings app (<c>ms-settings:developers</c>, M44). Only a page name (letters, digits,
    /// hyphens) is accepted, and only on Windows.
    /// </summary>
    public static void OpenWindowsSettings(string page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Length == 0 || !page.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException($"'{page}' isn't a Settings page.", nameof(page));
        }

        if (PlatformInfo.Current != PlatformKind.Windows)
        {
            throw new PlatformNotSupportedException("Windows' Settings open only on Windows.");
        }

        Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true });
    }

    /// <summary>Shows a file or folder in Explorer, Finder or the default Linux file manager.</summary>
    public static void RevealInFileManager(string path) => Start(RevealStartInfo(Path.GetFullPath(path), PlatformInfo.Current));

    internal static ProcessStartInfo RevealStartInfo(string full, PlatformKind platform) => platform switch
    {
        // Explorer reads its own command line and knows the switch only unquoted: ArgumentList would quote all of
        // "/select,<path>" when the path has a space, and Explorer would open Documents instead. A Windows path can't
        // contain a quote, so quoting the path alone is safe.
        PlatformKind.Windows => new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false },
        PlatformKind.MacOS => Args("open", "-R", full),
        _ => Args("xdg-open", Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full),
    };

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
