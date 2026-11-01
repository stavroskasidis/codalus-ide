using System.Diagnostics;
using Codalus.Platform;

namespace Codalus.Workbench.Setup;

/// <summary>
/// The user's login shell's <c>PATH</c>. An app started from the macOS Finder or Dock gets only the system's minimal
/// <c>PATH</c> (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>), without <c>/usr/local/share/dotnet</c>, Homebrew or the user's
/// own folders, which only login shells add (<c>path_helper</c>, <c>~/.zprofile</c>). On Linux, an app started from the
/// desktop (the AppImage, the packages' menu entry) gets the session's environment, which often lacks what the shell's
/// startup files add, such as a <c>~/.dotnet</c> from dotnet-install. So, like other IDEs, the shell asks the login
/// shell once at startup and puts its folders first, and takes the network settings its startup files export.
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
    /// What else the login shell's environment brings: proxies, certificate bundles, the SSH agent and NuGet's
    /// credential providers, which users set in <c>~/.zshrc</c> or <c>~/.zprofile</c>. Without them, git and NuGet
    /// started from a Finder launch can't reach a server that the same commands reach from a terminal (a corporate
    /// proxy: "Failed to connect to … port 443"). The login shell's value wins: it is what the terminal has.
    /// </summary>
    public static readonly IReadOnlyList<string> NetworkVariables =
    [
        "http_proxy", "https_proxy", "all_proxy", "no_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "CURL_CA_BUNDLE", "GIT_SSL_CAINFO", "GIT_SSL_CAPATH", "NODE_EXTRA_CA_CERTS",
        "SSH_AUTH_SOCK", "GIT_SSH", "GIT_SSH_COMMAND",
        "NUGET_PACKAGES", "NUGET_PLUGIN_PATHS", "NUGET_CREDENTIALPROVIDERS_PATH", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS",
    ];

    /// <summary>
    /// Runs <paramref name="shell"/> as an interactive login shell and reads its <c>PATH</c>; null when it fails or takes
    /// longer than <paramref name="timeout"/> (a slow profile mustn't hold the IDE's start).
    /// </summary>
    public static string? Read(string shell, TimeSpan timeout) => ReadEnvironment(shell, timeout)?.GetValueOrDefault("PATH");

    /// <summary>
    /// Runs <paramref name="shell"/> as an interactive login shell and reads the environment it exports (through
    /// <c>env</c>, which any shell, fish included, can run); null when it fails or takes longer than <paramref name="timeout"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadEnvironment(string shell, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var argument in new[] { "-ilc", $"printf '{Marker}'; env; printf '{Marker}'" })
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
                ProcessTree.Kill(process);
                return null;
            }

            // A background job the profile started can hold the output open after the shell exited: its end would never
            // come, and the IDE's start waited for it (M56: the environment's gate does). What it printed then can't be
            // told complete, as for a shell that took too long.
            if (Task.WaitAny([output], TimeSpan.FromSeconds(1)) < 0)
            {
                return null;
            }

            var text = output.GetAwaiter().GetResult();
            var first = text.IndexOf(Marker, StringComparison.Ordinal);
            var last = text.LastIndexOf(Marker, StringComparison.Ordinal);
            return first >= 0 && last > first ? ParseEnvironment(text[(first + Marker.Length)..last]) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary><c>env</c>'s <c>NAME=value</c> lines (a line of a value that spans lines is skipped unless it looks like one).</summary>
    public static IReadOnlyDictionary<string, string> ParseEnvironment(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && line[..equals].All(c => c == '_' || char.IsAsciiLetterOrDigit(c)) && !char.IsAsciiDigit(line[0]))
            {
                variables[line[..equals]] = line[(equals + 1)..].TrimEnd('\r');
            }
        }

        return variables;
    }

    /// <summary>The variables <see cref="Import"/> takes from the login shell's environment: <c>PATH</c> and the network ones it sets.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Imported(IReadOnlyDictionary<string, string> login) =>
        login.Where(variable => variable.Value.Length > 0 && (variable.Key == "PATH" || NetworkVariables.Contains(variable.Key, StringComparer.Ordinal)));

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

    /// <summary>
    /// Asks the login shell (<c>SHELL</c>, else zsh on macOS and bash on Linux) and sets this process's <c>PATH</c> and
    /// <see cref="NetworkVariables"/>, which every host and tool the IDE starts inherits; returns whether it did.
    /// </summary>
    public static bool Import()
    {
        if (!ShouldImport(Environment.GetEnvironmentVariable))
        {
            return false;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } configured && File.Exists(configured) ? configured
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        if (ReadEnvironment(shell, TimeSpan.FromSeconds(5)) is not { } login)
        {
            return false;
        }

        foreach (var (name, value) in Imported(login))
        {
            Environment.SetEnvironmentVariable(name, name == "PATH" ? Merge(Environment.GetEnvironmentVariable("PATH"), value) : value);
        }

        return login.ContainsKey("PATH");
    }
}
