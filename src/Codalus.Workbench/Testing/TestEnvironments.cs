using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Codalus.Platform;
using Codalus.Workbench.Build;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Workbench.Testing;

/// <summary>
/// A remote test environment of <c>testenvironments.json</c> (M21b, ADR 0055), in the format other IDEs read:
/// <c>wsl</c> (<see cref="WslDistribution"/>), <c>docker</c> (<see cref="DockerImage"/> or <see cref="DockerFile"/>)
/// or <c>ssh</c> (<see cref="RemoteUri"/>).
/// </summary>
public sealed record TestEnvironment(string Name, string Type)
{
    public const string Wsl = "wsl";
    public const string Docker = "docker";
    public const string Ssh = "ssh";

    public string? WslDistribution { get; init; }

    public string? DockerImage { get; init; }

    /// <summary>A Dockerfile, relative to the file's folder.</summary>
    public string? DockerFile { get; init; }

    /// <summary><c>ssh://user@host:port</c>.</summary>
    public string? RemoteUri { get; init; }

    /// <summary>Why it can't be used (an unknown type, a missing property), or null.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Running a solution's tests in another environment (M21b, ADR 0055): the test projects are built here, their output
/// runs there with <c>dotnet test &lt;assembly&gt;</c>, and the TRX results come back onto the Test Explorer's tree.
/// <list type="bullet">
/// <item>WSL: the output through <c>/mnt/&lt;drive&gt;</c>, the results written next to the solution.</item>
/// <item>Docker: a container of the image (or the Dockerfile's), kept running, with the solution's folder mounted at
/// <c>/workspaces/&lt;folder&gt;</c>.</item>
/// <item>SSH: each project's output copied to <c>~/.codalus-tests/&lt;solution&gt;/</c> there, the results read back.</item>
/// </list>
/// </summary>
public static class TestEnvironments
{
    public const string FileName = "testenvironments.json";

    /// <summary><c>testenvironments.json</c> in the solution's folder or a folder above it, up to the repository's root.</summary>
    public static string? Find(string solutionFolder)
    {
        for (var folder = new DirectoryInfo(solutionFolder); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, FileName);
            if (File.Exists(file))
            {
                return file;
            }

            if (Directory.Exists(Path.Combine(folder.FullName, ".git")) || File.Exists(Path.Combine(folder.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Reads the environments (JSON with comments); environments it can't use carry their <see cref="TestEnvironment.Problem"/>.</summary>
    public static IReadOnlyList<TestEnvironment> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("environments", out var environments) || environments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<TestEnvironment>();
        foreach (var element in environments.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var type = (String(element, "type") ?? string.Empty).ToLowerInvariant();
            var environment = new TestEnvironment(String(element, "name") ?? type, type)
            {
                WslDistribution = String(element, "wslDistribution"),
                DockerImage = String(element, "dockerImage"),
                DockerFile = String(element, "dockerFile"),
                RemoteUri = String(element, "remoteUri"),
            };
            result.Add(environment with
            {
                Problem = type switch
                {
                    TestEnvironment.Wsl when !OperatingSystem.IsWindows() => "WSL environments run from Windows only.",
                    TestEnvironment.Wsl => null,
                    TestEnvironment.Docker when environment.DockerImage is null && environment.DockerFile is null => "It names neither a dockerImage nor a dockerFile.",
                    TestEnvironment.Docker => null,
                    TestEnvironment.Ssh when SshTarget.Parse(environment.RemoteUri) is null => "Its remoteUri isn't an ssh://user@host:port address.",
                    TestEnvironment.Ssh => null,
                    _ => $"The type '{type}' isn't one of wsl, docker and ssh.",
                },
            });
        }

        return result;
    }

    public static IReadOnlyList<TestEnvironment> Load(string file) => Parse(File.ReadAllText(file));

    /// <summary>
    /// A <c>dotnet test --filter</c> expression for tests by their fully qualified names (null: all), with the
    /// characters the filter syntax reserves escaped.
    /// </summary>
    public static string? Filter(IReadOnlyCollection<string>? fullyQualifiedNames)
    {
        if (fullyQualifiedNames is null or { Count: 0 })
        {
            return null;
        }

        static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                {
                    escaped.Append('\\');
                }

                escaped.Append(c);
            }

            return escaped.ToString();
        }

        // Theories' cases share their method's name: one condition per method.
        return string.Join('|', fullyQualifiedNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => "FullyQualifiedName=" + Escape(n)));
    }

    /// <summary>A word for a POSIX shell: single-quoted.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The container a docker environment runs in: per solution folder and environment, kept for the next run.</summary>
    public static string ContainerName(string solutionFolder, TestEnvironment environment)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(solutionFolder)).ToLowerInvariant() + "\n" + environment.Name)))[..10];
        return "codalus-tests-" + hash;
    }

    /// <summary>Where the solution's folder is in a docker environment's container.</summary>
    public static string ContainerFolder(string solutionFolder) =>
        "/workspaces/" + new string([.. Path.GetFileName(Path.TrimEndingDirectorySeparator(solutionFolder)).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]);

    /// <summary>A path under the solution's folder as the container sees it; null for one outside it.</summary>
    public static string? ToContainerPath(string solutionFolder, string path)
    {
        var relative = Path.GetRelativePath(solutionFolder, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null
            : ContainerFolder(solutionFolder) + (relative == "." ? string.Empty : "/" + relative.Replace('\\', '/'));
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>A test project to run in an environment: its file, its built assembly here, and the tests to run (null: all).</summary>
public sealed record EnvironmentTestProject(string Path, string TargetPath, IReadOnlyCollection<string>? FullyQualifiedNames);

/// <summary>Runs test projects in a <see cref="TestEnvironment"/> and returns their TRX results (ADR 0055).</summary>
public sealed class EnvironmentTestRunner(TestEnvironment environment, string solutionFolder, Action<string> log)
{
    /// <summary>The container CLI for docker environments (the settings' docker or podman).</summary>
    public ContainerEngine? Containers { get; init; }

    /// <summary>The ssh program for ssh environments (tests use a stand-in).</summary>
    public string SshProgram { get; init; } = "ssh";

    /// <summary>The folder for copies on an ssh target (tests); by default <c>~/.codalus-tests</c> there.</summary>
    public string? RemoteFolder { get; init; }

    private string? _remoteFolder;

    /// <summary>Runs every project, one after the other; a project whose run fails to start is reported and skipped.</summary>
    public async Task<IReadOnlyList<XDocument>> RunAsync(IReadOnlyList<EnvironmentTestProject> projects, CancellationToken cancellationToken)
    {
        if (environment.Problem is { } problem)
        {
            throw new InvalidOperationException($"{environment.Name}: {problem}");
        }

        var target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        var run = Guid.NewGuid().ToString("N")[..12];
        var results = new List<XDocument>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileNameWithoutExtension(project.Path);
            var trx = $"{name}-{run}.trx";
            string assembly;
            string resultsFolder;
            string? localResults = null;
            switch (environment.Type)
            {
                case TestEnvironment.Wsl:
                    assembly = WslLaunch.ToLinuxPath(project.TargetPath);
                    localResults = System.IO.Path.Combine(solutionFolder, "TestResults", "codalus");
                    resultsFolder = WslLaunch.ToLinuxPath(localResults);
                    break;
                case TestEnvironment.Docker:
                    assembly = TestEnvironments.ToContainerPath(solutionFolder, project.TargetPath)
                        ?? throw new InvalidOperationException($"{name}'s output ({project.TargetPath}) isn't under the solution's folder, which is all the container sees.");
                    localResults = System.IO.Path.Combine(solutionFolder, "TestResults", "codalus");
                    resultsFolder = TestEnvironments.ToContainerPath(solutionFolder, localResults)!;
                    break;
                default:
                    var remote = await CopyOutputAsync(target, project, name, cancellationToken).ConfigureAwait(false);
                    assembly = remote + "/" + System.IO.Path.GetFileName(project.TargetPath);
                    resultsFolder = remote + "/TestResults";
                    break;
            }

            List<string> arguments = ["test", assembly, "--logger", "trx;LogFileName=" + trx, "--results-directory", resultsFolder];
            if (TestEnvironments.Filter(project.FullyQualifiedNames) is { } filter)
            {
                arguments.AddRange(["--filter", filter]);
            }

            log($"{environment.Name}: dotnet {string.Join(' ', arguments)}");
            // From /tmp: the solution's global.json pins this machine's SDK, which the environment may not have (the
            // test host still runs in the assembly's folder).
            var script = Prelude + "\nmkdir -p " + TestEnvironments.Quote(resultsFolder) + " && cd /tmp && dotnet " + string.Join(' ', arguments.Select(TestEnvironments.Quote));
            var exitCode = await RunAsync(target, script, cancellationToken).ConfigureAwait(false);
            log($"{environment.Name}: {name} exited with {exitCode}.");

            var text = localResults is not null
                ? File.Exists(System.IO.Path.Combine(localResults, trx)) ? await File.ReadAllTextAsync(System.IO.Path.Combine(localResults, trx), cancellationToken).ConfigureAwait(false) : null
                : await ReadRemoteAsync(target, resultsFolder + "/" + trx, cancellationToken).ConfigureAwait(false);
            if (localResults is not null)
            {
                TryDelete(System.IO.Path.Combine(localResults, trx));
            }

            if (text is null)
            {
                log($"{environment.Name}: {name} wrote no results (does the environment have .NET for its framework?).");
                continue;
            }

            results.Add(XDocument.Parse(text));
        }

        return results;
    }

    /// <summary><c>~/.profile</c>, then <c>~/.dotnet</c> first when it's there (a user's own install is the newer one).</summary>
    private const string Prelude = """
        [ -f "$HOME/.profile" ] && . "$HOME/.profile" >/dev/null 2>&1 </dev/null
        if [ -x "$HOME/.dotnet/dotnet" ]; then PATH="$HOME/.dotnet:$PATH"; export PATH; fi
        export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
        """;

    private async Task<RemoteTarget> TargetAsync(CancellationToken cancellationToken)
    {
        switch (environment.Type)
        {
            case TestEnvironment.Wsl:
                return new WslTarget(environment.WslDistribution ?? string.Empty);
            case TestEnvironment.Ssh:
                return SshTarget.Parse(environment.RemoteUri, SshProgram) ?? throw new InvalidOperationException($"{environment.Name}: the remoteUri isn't an ssh address.");
            default:
                var engine = Containers ?? throw new InvalidOperationException("No container tool (docker or podman) is set up.");
                var config = new DevContainerConfig(TestEnvironments.FileName, environment.Name)
                {
                    Image = environment.DockerImage,
                    Dockerfile = environment.DockerFile is { } file ? System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionFolder, file)) : null,
                    BuildContext = environment.DockerFile is { } context ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionFolder, context))) : null,
                    WorkspaceFolder = TestEnvironments.ContainerFolder(solutionFolder),
                };
                var name = await DevContainers.UpAsync(engine, config, solutionFolder, log, cancellationToken, TestEnvironments.ContainerName(solutionFolder, environment)).ConfigureAwait(false);
                return new ContainerTarget(name, engine.Tool);
        }
    }

    /// <summary>Copies a project's output folder to the ssh target, replacing the last copy; returns the folder there.</summary>
    private async Task<string> CopyOutputAsync(RemoteTarget target, EnvironmentTestProject project, string name, CancellationToken cancellationToken)
    {
        if (_remoteFolder is null && RemoteFolder is { } given)
        {
            _remoteFolder = given;
        }
        else if (_remoteFolder is null)
        {
            // Absolute: the commands quote their paths, so $HOME is resolved first.
            var home = await RemoteShell.RunAsync(target, "echo \"$HOME\"", cancellationToken).ConfigureAwait(false);
            var folder = home.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (folder is null || !folder.StartsWith('/'))
            {
                throw new InvalidOperationException($"Couldn't reach {environment.Name}: {RemoteShell.Trim(home.Error + home.Output)}");
            }

            _remoteFolder = folder + "/.codalus-tests";
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(solutionFolder).ToLowerInvariant())))[..10];
        var remote = $"{_remoteFolder}/{key}/{name}";
        var archive = new MemoryStream();
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            await TarFile.CreateFromDirectoryAsync(System.IO.Path.GetDirectoryName(project.TargetPath)!, gzip, includeBaseDirectory: false, cancellationToken).ConfigureAwait(false);
        }

        archive.Position = 0;
        log($"{environment.Name}: copying {name}'s output ({archive.Length / 1024} KB)…");
        var copied = await RemoteShell.RunAsync(target, $"d=\"{remote}\"; rm -rf \"$d\" && mkdir -p \"$d\" && tar -xzf - -C \"$d\" && echo copied", cancellationToken, archive).ConfigureAwait(false);
        if (!copied.Output.Contains("copied", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Couldn't copy {name}'s output to {environment.Name}: {RemoteShell.Trim(copied.Error + copied.Output)}");
        }

        return remote;
    }

    private static async Task<string?> ReadRemoteAsync(RemoteTarget target, string path, CancellationToken cancellationToken)
    {
        var read = await RemoteShell.RunAsync(target, $"f=\"{path}\"; [ -f \"$f\" ] && cat \"$f\" && rm -f \"$f\"", cancellationToken).ConfigureAwait(false);
        return read.Succeeded && read.Output.Length > 0 ? read.Output : null;
    }

    /// <summary>Runs the script there, its output to the log as it comes; returns the exit code.</summary>
    private async Task<int> RunAsync(RemoteTarget target, string script, CancellationToken cancellationToken)
    {
        var (program, arguments) = target.Shell(script);
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in RemoteShell.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} didn't start.");
        process.StandardInput.Close();
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
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

        void Line(string? line)
        {
            if (line is not null)
            {
                log(line);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
