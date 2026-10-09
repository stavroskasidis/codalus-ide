using System.Diagnostics;

namespace DaedalusStudio.Benchmarks.Harness;

/// <summary>What to open in a launched target.</summary>
internal sealed record LaunchRequest(string WorkDir, string? File = null, string? Solution = null, string? Bench = null, string? ExitOn = null, string? EventsFile = null, IReadOnlyList<string>? ExtraArgs = null);

/// <summary>A program under measurement: one of the prototypes or a reference IDE.</summary>
internal sealed record Target(
    string Name,
    bool IsPrototype,
    string Executable,
    string ProcessName,
    Func<Target, LaunchRequest, ProcessStartInfo> CreateStartInfo,
    string? InstallDirectory,
    string? Notes = null,
    Func<string, string, bool>? IsMainWindow = null,
    IReadOnlyList<string>? HelperDirectories = null,
    IReadOnlySet<string>? SupportedScenarios = null)
{
    /// <summary>Prototypes emit bench events, so they are measured both instrumented and black-box.</summary>
    public bool Instrumented => IsPrototype;

    public bool Available => File.Exists(Executable);
}

internal static class TargetCatalog
{
    public static IReadOnlyList<Target> All(string repoRoot) =>
    [
        Shell(Path.Combine(repoRoot, "artifacts", "shell")),
        VsCode(),
        Rider(),
        VisualStudio(),
    ];

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// The product shell (<c>dotnet publish src/DaedalusStudio.Shell -o artifacts/shell</c>), with a fresh user-data
    /// directory per launch, so every start is a first start with default settings and layout. Since M3 it is also the
    /// editor under measurement (the M0 spike's results remain under results/ for reference).
    /// </summary>
    private static Target Shell(string dir) => new(
        "shell",
        IsPrototype: true,
        Path.Combine(dir, Exe("daedalus")),
        Exe("daedalus"),
        static (target, request) =>
        {
            var psi = Base(target.Executable, request.WorkDir);
            var userData = Path.Combine(request.WorkDir, "user-data");
            Add(psi, "--user-data-dir", userData);
            Add(psi, "--exit-on", request.ExitOn);
            Add(psi, "--open", request.File);
            Add(psi, "--bench", request.Bench);
            if (request.Solution is { } solution)
            {
                // The harness copied and restored the fixture: trust its folder (as the user would once) and
                // skip restore-on-open, which would only repeat the harness's restore.
                Directory.CreateDirectory(Path.Combine(userData, "state"));
                Directory.CreateDirectory(Path.Combine(userData, "config"));
                File.WriteAllText(Path.Combine(userData, "state", "trusted-folders.json"),
                    "{ \"folders\": [" + System.Text.Json.JsonSerializer.Serialize(request.WorkDir, Model.ResultsJsonContext.Default.String) + "] }");
                File.WriteAllText(Path.Combine(userData, "config", "settings.json"), "{ \"projects.restoreOnOpen\": false }");
                psi.ArgumentList.Add(solution);
            }

            if (request.EventsFile is not null)
            {
                psi.Environment["DAEDALUS_BENCH_EVENTS"] = request.EventsFile;
            }

            // Use the published hosts next to the shell.
            psi.Environment.Remove("DAEDALUS_PLUGINHOST");
            psi.Environment.Remove("DAEDALUS_BUILDHOST");
            psi.Environment.Remove("DAEDALUS_LSHOST");
            psi.Environment.Remove("DAEDALUS_USER_DATA_DIR");
            return psi;
        },
        dir,
        "Fresh user data per launch: the built-in Welcome plugin starts its plugin host after the window is interactive.",
        HelperDirectories: ["pluginhost", "buildhost", "lshost", "replhost", "plugins"]);

    /// <summary>
    /// VS Code with a throwaway profile: no extensions (C# tooling is not used; the editor runs on TextMate
    /// grammars), solid caret, no startup editor, telemetry and updates off.
    /// </summary>
    private static Target VsCode()
    {
        var exe = Environment.GetEnvironmentVariable("DAEDALUS_BENCH_VSCODE") ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe")
            : OperatingSystem.IsMacOS() ? "/Applications/Visual Studio Code.app/Contents/MacOS/Electron" : "/usr/share/code/code");
        return new Target(
            "vscode",
            IsPrototype: false,
            exe,
            Path.GetFileName(exe),
            static (target, request) =>
            {
                var profile = Path.Combine(request.WorkDir, "vscode-profile");
                var settings = Path.Combine(profile, "User");
                Directory.CreateDirectory(settings);
                File.WriteAllText(Path.Combine(settings, "settings.json"), """
                    {
                      "editor.cursorBlinking": "solid",
                      "workbench.startupEditor": "none",
                      "workbench.tips.enabled": false,
                      "telemetry.telemetryLevel": "off",
                      "update.mode": "none",
                      "extensions.autoCheckUpdates": false,
                      "window.restoreWindows": "none",
                      "security.workspace.trust.enabled": false
                    }
                    """);
                var psi = Base(target.Executable, request.WorkDir);
                foreach (var arg in new[]
                         {
                             "--user-data-dir", profile, "--extensions-dir", Path.Combine(request.WorkDir, "vscode-extensions"),
                             "--disable-extensions", "--skip-welcome", "--skip-release-notes", "--disable-telemetry", "--new-window",
                         })
                {
                    psi.ArgumentList.Add(arg);
                }

                foreach (var extra in request.ExtraArgs ?? [])
                {
                    psi.ArgumentList.Add(extra);
                }

                Add(psi, null, request.File ?? request.Solution);
                return psi;
            },
            Path.GetDirectoryName(exe),
            "No extensions: TextMate highlighting only, no C# language service.");
    }

    /// <summary>Newest installed Rider. Uses the user's own configuration (a license is required).</summary>
    private static Target Rider()
    {
        var exe = Environment.GetEnvironmentVariable("DAEDALUS_BENCH_RIDER") ?? (OperatingSystem.IsWindows()
            ? Directory.EnumerateDirectories(@"C:\Program Files\JetBrains", "JetBrains Rider *")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin", "rider64.exe"))
                .FirstOrDefault(File.Exists) ?? string.Empty
            : string.Empty);
        return new Target(
            "rider",
            IsPrototype: false,
            exe,
            "rider64.exe",
            static (target, request) =>
            {
                var psi = Base(target.Executable, request.WorkDir);
                Add(psi, null, request.Solution ?? request.File);
                return psi;
            },
            exe.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(exe)) : null,
            "User's configuration; 'Reopen projects on startup' must be off (see benchmarks/README.md).",
            // The IDE frame (welcome screen or project), not the splash or a 'Start Failed' dialog.
            static (title, className) => className == "SunAwtFrame" && !title.Contains("Start Failed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Newest Visual Studio, in an isolated experimental instance (/rootsuffix) so user settings are untouched.</summary>
    private static Target VisualStudio()
    {
        var exe = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            var install = File.Exists(vswhere) ? Platform.Machine.Run(vswhere, "-latest -property installationPath") : null;
            if (install is not null)
            {
                exe = Path.Combine(install, "Common7", "IDE", "devenv.exe");
            }
        }

        return new Target(
            "vs",
            IsPrototype: false,
            exe,
            "devenv.exe",
            static (target, request) =>
            {
                var psi = Base(target.Executable, request.WorkDir);
                psi.ArgumentList.Add("/rootsuffix");
                psi.ArgumentList.Add("DaedalusBench");
                if (request.Solution is not null)
                {
                    psi.ArgumentList.Add(request.Solution);
                }
                else if (request.File is not null)
                {
                    psi.ArgumentList.Add("/edit");
                    psi.ArgumentList.Add(request.File);
                }

                return psi;
            },
            exe.Length > 0 ? Path.GetDirectoryName(exe) : null,
            "Experimental instance (/rootsuffix DaedalusBench); first launch needs a one-time manual setup.",
            static (_, className) => className != "VSSplash");
    }

    private static ProcessStartInfo Base(string exe, string workDir) => new(exe)
    {
        UseShellExecute = false,
        WorkingDirectory = workDir,
    };

    private static void Add(ProcessStartInfo psi, string? flag, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (flag is not null)
        {
            psi.ArgumentList.Add(flag);
        }

        psi.ArgumentList.Add(value);
    }
}
