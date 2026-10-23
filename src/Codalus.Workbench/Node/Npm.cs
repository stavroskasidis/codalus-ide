using System.Diagnostics;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Node;

/// <summary>A <c>package.json</c>: its scripts, whether it has dependencies, and its Task Runner bindings.</summary>
/// <param name="Bindings">
/// The scripts bound to IDE events, from <c>"-vs-binding"</c> (the format the Task Runner Explorer of other IDEs
/// writes): <c>BeforeBuild</c>, <c>AfterBuild</c>, <c>Clean</c> and <c>ProjectOpen</c>.
/// </param>
public sealed record NpmPackage(string Path, string? Name, IReadOnlyList<NpmScript> Scripts, bool HasDependencies, IReadOnlyDictionary<string, IReadOnlyList<string>> Bindings)
{
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>Whether its packages were installed (there is a <c>node_modules</c> folder).</summary>
    public bool IsRestored => Directory.Exists(System.IO.Path.Combine(Folder, "node_modules"));

    /// <summary>The scripts bound to <paramref name="binding"/>, in order.</summary>
    public IReadOnlyList<string> BoundTo(string binding) => Bindings.GetValueOrDefault(binding) ?? [];
}

public sealed record NpmScript(string Name, string Command);

/// <summary>
/// The npm that runs scripts and restores: <see cref="Program"/> with <see cref="Prefix"/> before npm's own arguments,
/// and the environment it needs.
/// </summary>
public sealed record NpmCommand(string Program, IReadOnlyList<string> Prefix, IReadOnlyDictionary<string, string> Environment, string Description)
{
    public IReadOnlyList<string> Arguments(params string[] npm) => [.. Prefix, .. npm];
}

/// <summary>
/// npm in solutions (ADR 0027): finding <c>package.json</c> files, their scripts, and the npm to run them with: the
/// user's (on <c>PATH</c>) unless they choose the built-in Node.js (ADR 0024), which is also the fallback.
/// </summary>
public static class Npm
{
    /// <summary>Which npm runs scripts and restores: <c>auto</c> (the user's, else the built-in Node.js) or <c>builtIn</c>.</summary>
    public const string RuntimeSetting = "npm.runtime";

    /// <summary>Restoring a solution's npm packages when it opens without them: <c>ask</c>, <c>always</c> or <c>never</c>.</summary>
    public const string RestoreOnOpenSetting = "npm.restoreOnOpen";

    public const string BeforeBuild = "BeforeBuild";
    public const string AfterBuild = "AfterBuild";
    public const string Clean = "Clean";
    public const string ProjectOpen = "ProjectOpen";

    /// <summary>Folders never searched for packages: installed packages, build output, tools' state.</summary>
    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".git", ".vs", ".vscode", ".idea", ".codalus", "TestResults", "artifacts" };

    /// <summary>The <c>package.json</c> files in <paramref name="roots"/> and below (to <paramref name="depth"/> levels), each once, by path.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> roots, int depth = 4)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string folder, int level)
        {
            if (!visited.Add(Path.GetFullPath(folder)))
            {
                return;
            }

            var file = Path.Combine(folder, "package.json");
            if (File.Exists(file))
            {
                found.Add(Path.GetFullPath(file));
            }

            if (level >= depth)
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!s_skipped.Contains(name) && !name.StartsWith('.'))
                {
                    Visit(child, level + 1);
                }
            }
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            Visit(root, 0);
        }

        return [.. found];
    }

    /// <summary>Reads a <c>package.json</c>; null when it can't be read or isn't a JSON object.</summary>
    public static NpmPackage? Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var scripts = new List<NpmScript>();
            if (root.TryGetProperty("scripts", out var section) && section.ValueKind == JsonValueKind.Object)
            {
                foreach (var script in section.EnumerateObject())
                {
                    if (script.Value.ValueKind == JsonValueKind.String)
                    {
                        scripts.Add(new NpmScript(script.Name, script.Value.GetString()!));
                    }
                }
            }

            var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("-vs-binding", out var binding) && binding.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in binding.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Array))
                {
                    bindings[entry.Name] = [.. entry.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)];
                }
            }

            static bool HasAny(JsonElement root, string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any();
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return new NpmPackage(Path.GetFullPath(path), name, scripts, HasAny(root, "dependencies") || HasAny(root, "devDependencies"), bindings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The user's npm on <c>PATH</c>, or null.</summary>
    public static NpmCommand? UserNpm(string? path = null)
    {
        var folders = (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var empty = new Dictionary<string, string>();
        foreach (var folder in folders)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (File.Exists(Path.Combine(folder, "npm")))
                {
                    return new NpmCommand(Path.Combine(folder, "npm"), [], empty, Path.Combine(folder, "npm"));
                }

                continue;
            }

            // A version manager's executable shim (Volta).
            if (File.Exists(Path.Combine(folder, "npm.exe")))
            {
                return new NpmCommand(Path.Combine(folder, "npm.exe"), [], empty, Path.Combine(folder, "npm.exe"));
            }

            var cmd = Path.Combine(folder, "npm.cmd");
            if (!File.Exists(cmd))
            {
                continue;
            }

            // The Node.js installer's layout (and nvm-windows', fnm's): run npm's script on that node, without cmd.exe.
            var node = Path.Combine(folder, "node.exe");
            var cli = Path.Combine(folder, "node_modules", "npm", "bin", "npm-cli.js");
            return File.Exists(node) && File.Exists(cli)
                ? new NpmCommand(node, [cli], empty, cmd)
                : new NpmCommand(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", cmd], empty, cmd);
        }

        return null;
    }

    /// <summary>The built-in Node.js's npm (ADR 0024); it must be installed first.</summary>
    public static NpmCommand BuiltIn(NodeRuntime node)
    {
        // Scripts that run node find this one.
        var bin = Path.GetDirectoryName(node.NodePath)!;
        var environment = new Dictionary<string, string>
        {
            [OperatingSystem.IsWindows() ? "Path" : "PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        return new NpmCommand(node.NodePath, [node.NpmCli], environment, $"npm of the built-in Node.js {NodeRuntime.Version}");
    }

    /// <summary>
    /// Runs npm with <paramref name="arguments"/> in <paramref name="folder"/>, its output line by line to
    /// <paramref name="output"/>. Returns the exit code; cancelling kills npm.
    /// </summary>
    public static async Task<int> RunAsync(NpmCommand npm, IReadOnlyList<string> arguments, string folder, Action<string> output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(npm.Program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        };
        foreach (var argument in npm.Arguments([.. arguments]))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in npm.Environment)
        {
            start.Environment[name] = value;
        }

        start.Environment["npm_config_update_notifier"] = "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{npm.Description} did not start.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                output(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                output(line);
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
            ProcessTree.Kill(process);
            throw;
        }

        return process.ExitCode;
    }
}
