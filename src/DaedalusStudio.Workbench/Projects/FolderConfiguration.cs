using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Projects;

/// <summary>
/// A task of <c>.daedalus/tasks.json</c> (folder mode, ADR 0033), in VS Code's shape: <see cref="Type"/> <c>process</c>
/// runs <see cref="Command"/> with <see cref="Args"/>, <c>shell</c> runs them through the system shell. <see cref="Group"/>
/// is <c>build</c>, <c>test</c> or null; <see cref="IsDefault"/> marks the group's default task.
/// </summary>
public sealed record FolderTask(string Label, string Type, string Command, IReadOnlyList<string> Args, string? Cwd, IReadOnlyDictionary<string, string> Env, string? Group, bool IsDefault);

/// <summary>
/// A launch configuration of <c>.daedalus/launch.json</c>: <see cref="Type"/> <c>project</c> starts a project (as a startup
/// project, with its launch profile) and <c>program</c> an executable or a <c>.dll</c> (run with <c>dotnet</c>).
/// <see cref="PreLaunchTask"/> names a task to run first.
/// </summary>
public sealed record LaunchConfiguration(string Name, string Type, string? Project, string? Program, IReadOnlyList<string> Args, string? Cwd, IReadOnlyDictionary<string, string> Env, string? PreLaunchTask);

/// <summary>What <c>${…}</c> variables expand to: the workspace folder, the active file, and its line and selection.</summary>
public sealed record FolderVariables(string WorkspaceFolder, string? File = null, int? Line = null, string? SelectedText = null);

/// <summary>Reads folder mode's <c>.daedalus/tasks.json</c> and <c>.daedalus/launch.json</c> (JSON with comments) and expands their variables.</summary>
public static partial class FolderConfiguration
{
    public const string TasksFile = "tasks.json";
    public const string LaunchFile = "launch.json";

    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string TasksPath(string folder) => Path.Combine(folder, ".daedalus", TasksFile);

    public static string LaunchPath(string folder) => Path.Combine(folder, ".daedalus", LaunchFile);

    /// <summary>The folder's tasks (none without the file), and a message when the file can't be read.</summary>
    public static (IReadOnlyList<FolderTask> Tasks, string? Error) ReadTasks(string folder)
    {
        var path = TasksPath(folder);
        if (!File.Exists(path))
        {
            return ([], null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), s_jsonc);
            var tasks = new List<FolderTask>();
            if (document.RootElement.TryGetProperty("tasks", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var task in array.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
                {
                    var label = String(task, "label") ?? String(task, "taskName");
                    var command = String(task, "command");
                    if (label is null || command is null)
                    {
                        continue;
                    }

                    var (group, isDefault) = task.TryGetProperty("group", out var g) switch
                    {
                        true when g.ValueKind == JsonValueKind.String => (g.GetString(), false),
                        true when g.ValueKind == JsonValueKind.Object => (String(g, "kind"), g.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True),
                        _ => ((string?)null, false),
                    };
                    var options = task.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
                    tasks.Add(new FolderTask(
                        label,
                        String(task, "type") ?? "process",
                        command,
                        Strings(task, "args"),
                        options.ValueKind == JsonValueKind.Object ? String(options, "cwd") : null,
                        options.ValueKind == JsonValueKind.Object ? Environment(options) : new Dictionary<string, string>(),
                        group,
                        isDefault));
                }
            }

            return (tasks, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"{path}: {ex.Message}");
        }
    }

    /// <summary>The folder's launch configurations (none without the file), and a message when the file can't be read.</summary>
    public static (IReadOnlyList<LaunchConfiguration> Configurations, string? Error) ReadLaunch(string folder)
    {
        var path = LaunchPath(folder);
        if (!File.Exists(path))
        {
            return ([], null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), s_jsonc);
            var configurations = new List<LaunchConfiguration>();
            if (document.RootElement.TryGetProperty("configurations", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    var name = String(item, "name");
                    var type = String(item, "type") ?? (String(item, "project") is not null ? "project" : "program");
                    if (name is null || (type == "project" ? String(item, "project") : String(item, "program")) is null)
                    {
                        continue;
                    }

                    configurations.Add(new LaunchConfiguration(name, type, String(item, "project"), String(item, "program"), Strings(item, "args"), String(item, "cwd"), Environment(item), String(item, "preLaunchTask")));
                }
            }

            return (configurations, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Expands <c>${workspaceFolder}</c>, <c>${workspaceFolderBasename}</c>, <c>${file}</c>, <c>${fileDirname}</c>,
    /// <c>${fileBasename}</c>, <c>${fileBasenameNoExtension}</c>, <c>${fileExtname}</c>, <c>${relativeFile}</c>,
    /// <c>${lineNumber}</c>, <c>${selectedText}</c>, <c>${pathSeparator}</c> and <c>${env:NAME}</c>. Unknown variables stay.
    /// </summary>
    public static string Expand(string text, FolderVariables variables) => Variable().Replace(text, match =>
    {
        var name = match.Groups["name"].Value;
        var file = variables.File;
        return name switch
        {
            "workspaceFolder" or "workspaceRoot" => variables.WorkspaceFolder,
            "workspaceFolderBasename" => Path.GetFileName(Path.TrimEndingDirectorySeparator(variables.WorkspaceFolder)),
            "file" => file ?? string.Empty,
            "fileDirname" => file is null ? string.Empty : Path.GetDirectoryName(file) ?? string.Empty,
            "fileBasename" => file is null ? string.Empty : Path.GetFileName(file),
            "fileBasenameNoExtension" => file is null ? string.Empty : Path.GetFileNameWithoutExtension(file),
            "fileExtname" => file is null ? string.Empty : Path.GetExtension(file),
            "relativeFile" => file is null ? string.Empty : Path.GetRelativePath(variables.WorkspaceFolder, file),
            "lineNumber" => variables.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            "selectedText" => variables.SelectedText ?? string.Empty,
            "pathSeparator" => Path.DirectorySeparatorChar.ToString(),
            _ when name.StartsWith("env:", StringComparison.Ordinal) => System.Environment.GetEnvironmentVariable(name[4..]) ?? string.Empty,
            _ => match.Value,
        };
    });

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z]+(:[A-Za-z0-9_]+)?)\}")]
    private static partial Regex Variable();

    /// <summary>The starting <c>tasks.json</c> (Configure Tasks).</summary>
    public const string TasksTemplate = """
        {
          // Tasks of this folder (Tasks → Run Task…). Build runs the default build task.
          // "type": "process" runs the command; "shell" runs it through the system shell.
          // Variables: ${workspaceFolder}, ${file}, ${fileDirname}, ${env:NAME}…
          "version": "2.0.0",
          "tasks": [
            {
              "label": "build",
              "type": "process",
              "command": "dotnet",
              "args": ["build"],
              "options": { "cwd": "${workspaceFolder}" },
              "group": { "kind": "build", "isDefault": true }
            }
          ]
        }

        """;

    /// <summary>The starting <c>launch.json</c> (Configure Launch).</summary>
    public const string LaunchTemplate = """
        {
          // How Start (F5) and Start Without Debugging (Ctrl+F5) run this folder.
          // "type": "project" starts a project; "program" an executable or a .dll (run with dotnet).
          "version": "0.2.0",
          "configurations": [
            {
              "name": "Program",
              "type": "program",
              "program": "${workspaceFolder}/bin/Debug/net10.0/App.dll",
              "args": [],
              "cwd": "${workspaceFolder}",
              "preLaunchTask": "build"
            }
          ]
        }

        """;

    /// <summary>
    /// Runs a task with its variables expanded, its output in <paramref name="channel"/>, and returns its exit code.
    /// <c>shell</c> tasks go through <c>cmd /c</c> on Windows and <c>/bin/sh -c</c> elsewhere.
    /// </summary>
    public static async Task<int> RunTaskAsync(FolderTask task, FolderVariables variables, Output.OutputChannel channel, CancellationToken cancellationToken)
    {
        var command = Expand(task.Command, variables);
        var args = task.Args.Select(a => Expand(a, variables)).ToList();
        System.Diagnostics.ProcessStartInfo start;
        if (task.Type == "shell")
        {
            var line = string.Join(' ', args.Prepend(command).Select(a => a.Contains(' ', StringComparison.Ordinal) && !a.StartsWith('"') ? $"\"{a}\"" : a));
            start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", line } }
                : new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", line } };
        }
        else
        {
            start = new System.Diagnostics.ProcessStartInfo(command);
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        start.WorkingDirectory = task.Cwd is { Length: > 0 } cwd ? Expand(cwd, variables) : variables.WorkspaceFolder;
        foreach (var (name, value) in task.Env)
        {
            start.Environment[name] = Expand(value, variables);
        }

        channel.AppendLine($"> {command} {string.Join(' ', args)}".TrimEnd() + $"   (task '{task.Label}')");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"{command} did not start.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                channel.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                channel.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        process.WaitForExit();
        channel.AppendLine($"The task '{task.Label}' exited with code {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        return process.ExitCode;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(a => a.ValueKind is JsonValueKind.String or JsonValueKind.Number).Select(a => a.ToString())]
            : [];

    private static Dictionary<string, string> Environment(JsonElement element) =>
        element.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
            ? env.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
