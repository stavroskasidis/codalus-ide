using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host.Packages;

/// <summary>
/// .NET tools: the local tool manifest (<c>.config/dotnet-tools.json</c>, found the way the CLI finds it: from the
/// solution folder upwards) and global tools, changed through <c>dotnet tool</c>.
/// </summary>
internal sealed class DotnetTools(string dotnet, string directory)
{
    public async Task<ToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        var manifest = FindManifest(directory);
        var local = manifest is null ? [] : ReadManifest(manifest);
        var (ok, output) = await RunAsync(["tool", "list", "--global"], cancellationToken).ConfigureAwait(false);
        return new ToolsResult(manifest, local, ok ? ParseList(output) : []);
    }

    public Task<(bool Succeeded, string Output)> RunActionAsync(ToolCommandParams parameters, CancellationToken cancellationToken)
    {
        List<string> args = parameters.Action switch
        {
            ToolActions.CreateManifest => ["new", "tool-manifest"],
            ToolActions.Restore => ["tool", "restore"],
            ToolActions.Install or ToolActions.Update or ToolActions.Uninstall => ["tool", parameters.Action, Require(parameters.Id)],
            _ => throw new ArgumentException($"Unknown tool action '{parameters.Action}'."),
        };
        if (parameters.Action is ToolActions.Install or ToolActions.Update && parameters.Version is { Length: > 0 } version)
        {
            args.AddRange(["--version", version]);
        }

        if (parameters.Global && parameters.Action is ToolActions.Install or ToolActions.Update or ToolActions.Uninstall)
        {
            args.Add("--global");
        }

        return RunAsync(args, cancellationToken);
    }

    private static string Require(string? id) => string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("A tool id is required.") : id.Trim();

    internal static string? FindManifest(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, ".config", "dotnet-tools.json"), Path.Combine(dir.FullName, "dotnet-tools.json") })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // The CLI stops at a manifest with "isRoot": true; it also stops at the file system root.
        }

        return null;
    }

    internal static DotnetToolInfo[] ReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("tools", out var tools))
            {
                return [];
            }

            return [.. tools.EnumerateObject().Select(tool => new DotnetToolInfo(
                tool.Name,
                tool.Value.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty,
                tool.Value.TryGetProperty("commands", out var commands) ? [.. commands.EnumerateArray().Select(c => c.GetString() ?? string.Empty)] : []))];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses <c>dotnet tool list</c>: a header, a dashed rule, then "id  version  commands" rows (column widths vary).
    /// </summary>
    internal static DotnetToolInfo[] ParseList(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var rule = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));
        if (rule < 0)
        {
            return [];
        }

        return [.. lines.Skip(rule + 1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new DotnetToolInfo(parts[0], parts[1], parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))];
    }

    private async Task<(bool Succeeded, string Output)> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = DotnetProcess.StartInfo(dotnet, directory, args);
        using var process = DotnetProcess.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = new StringBuilder();
        var stdout = Copy(process.StandardOutput, output);
        var stderr = Copy(process.StandardError, output);
        await DotnetProcess.WaitOrKillAsync(process, cancellationToken).ConfigureAwait(false);

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (process.ExitCode == 0, output.ToString().Trim());
    }

    private static async Task Copy(StreamReader reader, StringBuilder into)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (into)
            {
                into.AppendLine(line);
            }
        }
    }
}

/// <summary>Starting the SDK's <c>dotnet</c> from the build host.</summary>
internal static class DotnetProcess
{
    /// <summary>The SDK whose CLI runs restores and tools in the solution's folder: the one <c>global.json</c> resolves.</summary>
    public static Version? SdkVersion { get; set; }

    /// <summary>The system's ICU major version on Linux (<c>libicuuc.so.74</c> → 74), found once.</summary>
    private static readonly Lazy<int?> s_icu = new(SystemIcuMajor);

    public static ProcessStartInfo StartInfo(string dotnet, string workingDirectory, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        // The CLI of an SDK before 5.0 runs on .NET Core 2.x or 3.x, which stops at start on a Linux distribution whose
        // ICU is newer than the ones it knows (M33, ADR 0030): it gets the system's ICU, unless the choice is made.
        if (OperatingSystem.IsLinux() && SdkVersion is { Major: < 5 } && s_icu.Value is { } icu
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLR_ICU_VERSION_OVERRIDE"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")))
        {
            psi.Environment["CLR_ICU_VERSION_OVERRIDE"] = icu.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // MSBuildLocator pointed this process at the SDK's MSBuild through environment variables; the child
        // dotnet must resolve its own.
        foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            psi.Environment.Remove(variable);
        }

        return psi;
    }

    /// <summary>The system's ICU major version on Linux, or null.</summary>
    public static int? IcuMajor => s_icu.Value;

    private static int? SystemIcuMajor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        int? newest = null;
        foreach (var folder in new[] { "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/aarch64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib", "/usr/local/lib" })
        {
            try
            {
                foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "libicuuc.so.?*") : [])
                {
                    if (Path.GetFileName(file).Length > "libicuuc.so.".Length && int.TryParse(Path.GetFileName(file)["libicuuc.so.".Length..].Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
                        && major > (newest ?? 0))
                    {
                        newest = major;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest;
    }

    /// <summary>
    /// Starts a <c>dotnet</c> child with an empty input. Inheriting the build host's would hand it the shell's requests
    /// (this host's JSON-RPC input): one that read it would take them, and one that waited on it (a prompt) would never
    /// end. It gets end of file instead.
    /// </summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        var process = Process.Start(psi);
        if (process is not null && psi.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        return process;
    }

    /// <summary>
    /// Waits for the output of a process that has exited. A build it ran can leave processes behind (an MSBuild node, the
    /// compiler server) that inherited its pipes and keep them open for minutes: what arrived within a few seconds of
    /// the exit is all of it, and the readers are left to end when those processes do.
    /// </summary>
    public static async Task DrainAsync(params Task[] readers)
    {
        var all = Task.WhenAll(readers);
        await all.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (all.IsCompleted)
        {
            await all.ConfigureAwait(false);
        }
    }

    /// <summary>The variables of a <c>dotnet</c> child that builds: nothing it starts outlives it holding its pipes.</summary>
    public static void NoLingeringBuildProcesses(ProcessStartInfo psi)
    {
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["UseSharedCompilation"] = "false";
    }

    /// <summary>
    /// Waits for <paramref name="process"/>; when cancelled (the solution closes, the host shuts down) it kills the process
    /// tree first, so no <c>dotnet</c> child outlives the session.
    /// </summary>
    public static async Task WaitOrKillAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // Already gone.
            }

            throw;
        }
    }
}
