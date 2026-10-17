using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Projects;

/// <summary>A packaged app's identity, from the <c>AppxManifest.xml</c> its build writes.</summary>
/// <param name="ApplicationIds">The <c>Application</c> elements' ids (<c>App</c>), in order.</param>
public sealed record AppxIdentity(string Name, string Publisher, string Version, string? Architecture, IReadOnlyList<string> ApplicationIds)
{
    /// <summary>The package family name: the name and the publisher's id (<c>Contoso.App_8wekyb3d8bbwe</c>).</summary>
    public string FamilyName => Name + "_" + WindowsApps.PublisherId(Publisher);

    /// <summary>The application user model id an activation names: the family name, <c>!</c>, the application's id.</summary>
    public string? Aumid => ApplicationIds.Count == 0 ? null : FamilyName + "!" + ApplicationIds[0];
}

/// <summary>A framework package a packaged app depends on: its <c>.msix</c> and the identity read from it (null when unreadable).</summary>
public sealed record FrameworkPackage(string Path, AppxIdentity? Identity);

/// <summary>An installed Windows App SDK runtime (a framework package): its channel (<c>1.8</c>), version and architecture.</summary>
public sealed record WindowsAppRuntime(string Channel, string Version, string? Architecture);

/// <summary>
/// Windows App SDK apps (M44, ADR 0074, D31): WinUI 3 projects and .NET MAUI's Windows target. Unpackaged apps
/// (<c>WindowsPackageType=None</c>) run and debug as programs. A packaged (MSIX) app's build writes its layout with an
/// <c>AppxManifest.xml</c>: it is registered with <c>Add-AppxPackage -Register</c> (Developer Mode), started through
/// <c>IApplicationActivationManager</c> (the debug companion's <c>--activate-app</c>), and debugged by attaching
/// netcoredbg to the process. Everything here is pure, so the tests run on every OS; the shell runs the commands.
/// </summary>
public static class WindowsApps
{
    /// <summary>The Settings page with Developer Mode (the <c>ms-settings:</c> URI's page).</summary>
    public const string DeveloperSettingsPage = "developers";

    /// <summary>Where Developer Mode is recorded: <c>HKLM\…\AppModelUnlock</c>'s <c>AllowDevelopmentWithoutDevLicense</c> = 1.</summary>
    public const string DeveloperModeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";

    public const string DeveloperModeValue = "AllowDevelopmentWithoutDevLicense";

    /// <summary>The Windows App SDK's download page (its runtime installers).</summary>
    public const string RuntimeDownloadPage = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

    public const string DeveloperModeToolId = "windows-developer-mode";

    public const string RuntimeToolId = "windows-app-runtime";

    /// <summary>Whether the project is a Windows App SDK app: a WinUI 3 project, or a project with a <c>-windows10.0.x</c> framework that uses .NET MAUI.</summary>
    public static bool IsWindowsAppSdk(ProjectInfo project) =>
        project.UsesWinUI || (project.UsesMaui && project.TargetFrameworks.Any(f => WindowsDesktopProjects.IsWindowsFramework(f)));

    /// <summary>Whether a <c>WindowsPackageType</c> means a packaged (MSIX) app; <c>None</c>, <c>Sparse</c> and unset run as programs.</summary>
    public static bool IsPackaged(string? windowsPackageType) => string.Equals(windowsPackageType?.Trim(), "MSIX", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ the package's identity

    /// <summary>
    /// The publisher id of a package family name: the first 8 bytes of the SHA-256 of the publisher (UTF-16LE), as 13
    /// characters of Crockford's base 32 (<c>CN=Microsoft Corporation, …</c> → <c>8wekyb3d8bbwe</c>).
    /// </summary>
    public static string PublisherId(string publisher)
    {
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (var i = 0; i < 8; i++)
        {
            bits = (bits << 8) | hash[i];
        }

        // 64 bits and a trailing zero bit: 13 groups of 5, the last one the low 4 bits and the zero.
        var result = new char[13];
        for (var i = 0; i < 13; i++)
        {
            var shift = 59 - (i * 5);
            var group = shift >= 0 ? (bits >> shift) & 0x1F : (bits << -shift) & 0x1F;
            result[i] = Alphabet[(int)group];
        }

        return new string(result);
    }

    /// <summary>The identity in an <c>AppxManifest.xml</c> (or a <c>Package.appxmanifest</c>), or null when it has none.</summary>
    public static AppxIdentity? ReadManifest(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var identity = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
        if (identity?.Attribute("Name")?.Value is not { Length: > 0 } name || identity.Attribute("Publisher")?.Value is not { Length: > 0 } publisher)
        {
            return null;
        }

        var applications = document.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Applications")?.Elements()
            .Where(e => e.Name.LocalName == "Application")
            .Select(e => e.Attribute("Id")?.Value)
            .OfType<string>()
            .ToList() ?? [];
        return new AppxIdentity(name, publisher, identity.Attribute("Version")?.Value ?? "1.0.0.0", identity.Attribute("ProcessorArchitecture")?.Value, applications);
    }

    /// <summary>
    /// The manifest to register after a build: the layout a deployment made (<c>AppX\AppxManifest.xml</c> next to the
    /// output), else the one the build wrote (<see cref="RunInfo.AppxManifest"/>, <c>FinalAppxManifestName</c>), else
    /// <c>AppxManifest.xml</c> next to the output; null when none exists.
    /// </summary>
    public static string? LayoutManifest(RunInfo info, Func<string, bool> exists)
    {
        var candidates = new List<string>();
        if (info.TargetPath is { } target && Path.GetDirectoryName(target) is { } output)
        {
            candidates.Add(Path.Combine(output, "AppX", "AppxManifest.xml"));
        }

        if (info.AppxManifest is { } built)
        {
            candidates.Add(built);
        }

        if (info.TargetPath is { } target2 && Path.GetDirectoryName(target2) is { } output2)
        {
            candidates.Add(Path.Combine(output2, "AppxManifest.xml"));
        }

        return candidates.FirstOrDefault(exists);
    }

    // ------------------------------------------------------------------ registering and starting

    /// <summary>
    /// The Windows App SDK runtime's framework packages a packaged app depends on, for <paramref name="architecture"/>
    /// (<c>x64</c>, <c>x86</c>, <c>arm64</c>), from the restored runtime package (<c>Microsoft.WindowsAppSDK.Runtime</c>
    /// in 1.8 and later, <c>Microsoft.WindowsAppSDK</c> before): its <c>tools\MSIX\win10-&lt;arch&gt;\Microsoft.WindowsAppRuntime.&lt;x.y&gt;.msix</c>.
    /// Registering installs them when they're missing, as a deployment from Visual Studio does.
    /// </summary>
    public static IReadOnlyList<string> FrameworkPackages(ProjectInfo project, string packagesRoot, string architecture, Func<string, IEnumerable<string>> listFiles)
    {
        var result = new List<string>();
        foreach (var id in new[] { "Microsoft.WindowsAppSDK.Runtime", "Microsoft.WindowsAppSDK" })
        {
            foreach (var version in project.Packages.Where(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Version).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant(), "tools", "MSIX", "win10-" + architecture.ToLowerInvariant());
                result.AddRange(listFiles(folder)
                    .Where(f => Path.GetFileName(f) is var file
                        && file.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase)
                        && file.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                        && !file.Contains(".Main.", StringComparison.OrdinalIgnoreCase)
                        && !file.Contains(".Singleton.", StringComparison.OrdinalIgnoreCase)
                        && !file.Contains(".DDLM.", StringComparison.OrdinalIgnoreCase)));
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The folder of the NuGet packages: <c>NUGET_PACKAGES</c>, else <c>~/.nuget/packages</c>.</summary>
    public static string PackagesRoot(Func<string, string?> environment, string home) =>
        environment("NUGET_PACKAGES") is { Length: > 0 } configured ? configured : Path.Combine(home, ".nuget", "packages");

    /// <summary>The architecture a package is for: the manifest's, else this machine's (<c>x64</c>, <c>arm64</c>, <c>x86</c>).</summary>
    public static string Architecture(AppxIdentity identity) =>
        identity.Architecture is { Length: > 0 } declared && !declared.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            ? declared.ToLowerInvariant()
            : RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                _ => "x64",
            };

    /// <summary>The identity in an <c>.msix</c> package's <c>AppxManifest.xml</c>, or null when the file isn't one.</summary>
    public static AppxIdentity? ReadPackageIdentity(string msix)
    {
        try
        {
            using var zip = ZipFile.OpenRead(msix);
            if (zip.GetEntry("AppxManifest.xml") is not { } entry)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open());
            return ReadManifest(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The arguments of <c>powershell.exe</c> that register a build's layout (Developer Mode), after installing the
    /// framework packages it depends on: <c>Add-AppxPackage -Path &lt;framework msix&gt;</c> for each that isn't installed
    /// at its version or a higher one for its architecture (<c>Get-AppxPackage</c>; when its identity is unknown it is
    /// installed, one there at a higher version, 0x80073D06, being fine), then <c>Add-AppxPackage -Register
    /// &lt;manifest&gt; -ForceApplicationShutdown -ForceUpdateFromAnyVersion</c>, failing on the first other error. A
    /// framework already there isn't added again: while an app that uses it runs (the runtime's Singleton, started by
    /// the app's last run), Windows refuses that with 0x80073D02 ("resources it modifies are currently in use"). The
    /// registration can't take them as <c>-DependencyPath</c>: with <c>-Register</c> every path given is registered as a
    /// layout, and a package file is rejected ("the manifest is not in the package root", 0x80073CF9).
    /// </summary>
    public static IReadOnlyList<string> RegisterArguments(string manifest, IReadOnlyList<FrameworkPackage> dependencies)
    {
        var command = new StringBuilder("$ErrorActionPreference = 'Stop'; ");
        foreach (var framework in dependencies)
        {
            var install = "try { Add-AppxPackage -Path " + PowerShellQuote(framework.Path) + " } catch { if ($_.Exception.Message -notmatch '0x80073D06') { throw } }";
            if (framework.Identity is { } identity)
            {
                command.Append("if (-not (Get-AppxPackage -Name ").Append(PowerShellQuote(identity.Name)).Append(" | Where-Object { ");
                if (identity.Architecture is { Length: > 0 } architecture)
                {
                    command.Append("\"$($_.Architecture)\" -eq ").Append(PowerShellQuote(architecture)).Append(" -and ");
                }

                command.Append("[version]$_.Version -ge [version]").Append(PowerShellQuote(identity.Version)).Append(" })) { ").Append(install).Append(" }; ");
            }
            else
            {
                command.Append(install).Append("; ");
            }
        }

        command.Append("Add-AppxPackage -Register ").Append(PowerShellQuote(manifest)).Append(" -ForceApplicationShutdown -ForceUpdateFromAnyVersion");
        return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command.ToString()];
    }

    /// <summary>The arguments of <c>powershell.exe</c> that list the installed Windows App SDK runtimes as JSON.</summary>
    public static IReadOnlyList<string> RuntimeListArguments() =>
        ["-NoProfile", "-NonInteractive", "-Command", "Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' | Select-Object Name, Version, Architecture | ConvertTo-Json -Compress"];

    /// <summary>A string in single quotes for PowerShell (a quote inside is doubled).</summary>
    public static string PowerShellQuote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The debug companion's arguments that start a packaged app through <c>IApplicationActivationManager</c>; it
    /// prints the process id.
    /// </summary>
    public static IReadOnlyList<string> ActivateArguments(string aumid, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? ["--activate-app", aumid] : ["--activate-app", aumid, "--arguments", arguments];

    /// <summary>The process id the companion's <c>--activate-app</c> printed (its last line that is a number), or null.</summary>
    public static int? ParseProcessId(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()
            .Select(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null)
            .FirstOrDefault(id => id is not null);

    /// <summary>A hint for a failed registration, from PowerShell's output (Developer Mode, a missing dependency), or null.</summary>
    public static string? RegisterFailureHint(string output) =>
        output.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) || output.Contains("developer mode", StringComparison.OrdinalIgnoreCase) || output.Contains("developer license", StringComparison.OrdinalIgnoreCase)
            ? "Registering a build's layout needs Developer Mode (Settings → System → For developers)."
            : output.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase)
            ? "A package it depends on is missing: install the Windows App SDK runtime its Microsoft.WindowsAppSDK version needs."
            : null;

    // ------------------------------------------------------------------ the environment

    /// <summary>Developer Mode from the registry value (a DWORD 1 is on), or null when it can't be read.</summary>
    public static bool? DeveloperMode(object? value) => value switch
    {
        int number => number == 1,
        long number => number == 1,
        null => false, // no value: off
        _ => null,
    };

    /// <summary>The runtimes in <c>Get-AppxPackage … | ConvertTo-Json</c>'s output (one object, or an array).</summary>
    public static IReadOnlyList<WindowsAppRuntime> ParseRuntimes(string json)
    {
        var result = new List<WindowsAppRuntime>();
        try
        {
            using var document = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json);
            IEnumerable<JsonElement> packages = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray() : [document.RootElement];
            foreach (var package in packages)
            {
                if (package.ValueKind != JsonValueKind.Object || !package.TryGetProperty("Name", out var name) || name.GetString() is not { } text)
                {
                    continue;
                }

                const string Prefix = "Microsoft.WindowsAppRuntime.";
                if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || text[Prefix.Length..] is not { Length: > 0 } channel || !char.IsAsciiDigit(channel[0]))
                {
                    continue;
                }

                var version = package.TryGetProperty("Version", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
                var architecture = package.TryGetProperty("Architecture", out var a) ? a.ValueKind switch
                {
                    JsonValueKind.String => a.GetString(),
                    JsonValueKind.Number => a.GetInt32() switch { 0 => "x86", 5 => "arm", 9 => "x64", 11 => "neutral", 12 => "arm64", _ => null },
                    _ => null,
                } : null;
                result.Add(new WindowsAppRuntime(channel, version ?? string.Empty, architecture));
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    /// <summary>The runtime channel a project's <c>Microsoft.WindowsAppSDK</c> package needs (<c>1.8</c>), or null.</summary>
    public static string? RuntimeChannel(ProjectInfo project) =>
        project.Packages.FirstOrDefault(p => string.Equals(p.Name, "Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase))?.Version.Split('.') is [var major, var minor, ..]
            ? $"{major}.{minor}"
            : null;

    /// <summary>
    /// Check Environment's rows for the solution's Windows App SDK apps (on Windows): Developer Mode, when one is
    /// packaged; the Windows App SDK runtime of each channel they need.
    /// </summary>
    public static IReadOnlyList<ToolStatus> ToolRows(IReadOnlyList<ProjectInfo> projects, bool? developerMode, IReadOnlyList<WindowsAppRuntime>? runtimes)
    {
        var apps = projects.Where(IsWindowsAppSdk).ToList();
        if (apps.Count == 0)
        {
            return [];
        }

        var rows = new List<ToolStatus>();
        if (apps.Any(p => IsPackaged(p.WindowsPackageType)))
        {
            rows.Add(new ToolStatus(DeveloperModeToolId, "Developer Mode", false, developerMode == true ? ToolState.Found : ToolState.Missing, "Running and debugging the solution's packaged (MSIX) apps")
            {
                Version = developerMode switch { true => "on", false => "off", _ => null },
                Detail = developerMode == true ? null : "Registering a build's layout needs it: Settings → System → For developers.",
            });
        }

        foreach (var channel in apps.Select(RuntimeChannel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var installed = runtimes?.Where(r => channel is null || r.Channel == channel).OrderByDescending(r => r.Version, StringComparer.Ordinal).FirstOrDefault();
            rows.Add(new ToolStatus(RuntimeToolId + (channel is null ? string.Empty : ":" + channel), channel is null ? "Windows App SDK runtime" : $"Windows App SDK runtime {channel}", false,
                installed is not null ? ToolState.Found : ToolState.Missing, "Running the solution's Windows App SDK apps that aren't self-contained (WindowsAppSDKSelfContained)")
            {
                Version = installed?.Version,
                Detail = runtimes is null ? "The installed runtimes couldn't be listed (PowerShell's Get-AppxPackage)." : null,
            });
        }

        return rows;
    }

    /// <summary>The install plan of one of <see cref="ToolRows"/>' rows: the Settings page, winget's runtime package, or the download page.</summary>
    public static InstallPlan? PlanFor(ToolStatus tool, bool hasWinget)
    {
        if (tool.Id == DeveloperModeToolId)
        {
            return new InstallPlan(tool.Id, InstallKind.OpenPage, "Settings → System → For developers, where Developer Mode is turned on", "ms-settings:" + DeveloperSettingsPage);
        }

        if (!tool.Id.StartsWith(RuntimeToolId, StringComparison.Ordinal))
        {
            return null;
        }

        var channel = tool.Id.Length > RuntimeToolId.Length + 1 ? tool.Id[(RuntimeToolId.Length + 1)..] : null;
        if (channel is null || !hasWinget)
        {
            return new InstallPlan(tool.Id, InstallKind.OpenPage, "The Windows App SDK's download page", RuntimeDownloadPage);
        }

        string[] arguments = ["install", "--id", "Microsoft.WindowsAppRuntime." + channel, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
        return new InstallPlan(tool.Id, InstallKind.Run, $"The Windows App SDK runtime {channel} with winget", "winget " + string.Join(' ', arguments), "winget", arguments);
    }
}
