using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Velopack;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The installers' hooks (M31, ADR 0028). Velopack's setup and update programs start the app with <c>--veloapp-*</c>
/// arguments on Windows; <see cref="VelopackApp.Run"/> handles those and exits. An app that Velopack didn't install
/// (a development build, the zip, the tar.gz, the .deb or .rpm) carries on at once.
/// </summary>
internal static partial class Installation
{
    public static void Run()
    {
        var app = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            app = WithWindowsHooks(app);
        }

        // A downloaded update is applied when M32's update service says so, never by a `codalus merge` call.
        app.SetAutoApplyOnStartup(false).Run();
    }

    /// <summary>Velopack's install, update and uninstall callbacks only exist on Windows.</summary>
    [SupportedOSPlatform("windows")]
    private static VelopackApp WithWindowsHooks(VelopackApp app) =>
        app.OnAfterInstallFastCallback(_ => AddCommand())
            .OnAfterUpdateFastCallback(_ => AddCommand())
            .OnBeforeUninstallFastCallback(_ => RemoveCommand());

    // ------------------------------------------------------------------ the `codalus` command on Windows

    /// <summary>The folder the installer put on the user's PATH, next to Velopack's <c>current\</c>.</summary>
    internal const string CommandFolder = "bin";

    /// <summary>
    /// <c>codalus.cmd</c> runs the installed app and waits for it, so <c>codalus --wait</c> and <c>codalus merge</c>
    /// work from a terminal and from git. Velopack's own launcher (the root <c>codalus.exe</c>) returns at once.
    /// </summary>
    internal const string CommandScript = "@echo off\r\n\"%~dp0..\\current\\codalus.exe\" %*\r\nexit /b %ERRORLEVEL%\r\n";

    [SupportedOSPlatform("windows")]
    private static void AddCommand()
    {
        if (InstallRoot() is not { } root)
        {
            return;
        }

        var folder = Path.Combine(root, CommandFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "codalus.cmd"), CommandScript);
        UpdateUserPath(path => WithEntry(path, folder));
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveCommand()
    {
        if (InstallRoot() is not { } root)
        {
            return;
        }

        UpdateUserPath(path => WithoutEntry(path, Path.Combine(root, CommandFolder)));
    }

    /// <summary>The install folder: the parent of <c>current\</c>, where this executable runs from.</summary>
    private static string? InstallRoot() =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) is { } root
        && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;

    internal static string WithEntry(string path, string folder) =>
        Entries(path).Any(e => SameFolder(e, folder)) ? path : string.Join(';', Entries(path).Append(folder));

    internal static string WithoutEntry(string path, string folder) =>
        string.Join(';', Entries(path).Where(e => !SameFolder(e, folder)));

    private static string[] Entries(string path) => path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Windows paths: either separator, any case (written out, so the tests compare the same way on every OS).</summary>
    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites the user's PATH in the registry, keeping its value kind: <c>Environment.SetEnvironmentVariable</c> would
    /// store an expandable PATH (<c>%USERPROFILE%\…</c>) as a plain string and break its other entries.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void UpdateUserPath(Func<string, string> change)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        var kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
        var updated = change(current);
        if (updated == current)
        {
            return;
        }

        key.SetValue("Path", updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);

        // New terminals and Explorer pick the change up without signing out.
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 2000, out _);
    }

    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
