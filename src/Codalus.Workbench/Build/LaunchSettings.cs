using System.Text;
using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Build;

/// <summary>
/// A profile of <c>Properties/launchSettings.json</c>. The <c>Project</c>, <c>Executable</c>, <c>WSL2</c> (a WSL
/// distribution, <see cref="DistributionName"/>; M14c) and <c>DebugRoslynComponent</c> (the compiler on
/// <see cref="TargetProject"/> with this analyzer or generator; M14c) commands run.
/// </summary>
public sealed record LaunchProfile(
    string Name,
    string CommandName,
    string? CommandLineArgs = null,
    string? WorkingDirectory = null,
    string? ExecutablePath = null,
    string? ApplicationUrl = null,
    bool LaunchBrowser = false,
    string? LaunchUrl = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? InspectUri = null,
    string? DistributionName = null,
    string? TargetProject = null)
{
    public const string Wsl = "WSL2";
    public const string RoslynComponent = "DebugRoslynComponent";

    /// <summary>The "Container (Dockerfile)" profile (M17a, ADR 0042): the project runs in a container built from its Dockerfile.</summary>
    public const string Docker = "Docker";

    public bool IsRunnable => CommandName is "Project" or "Executable" or Wsl or RoslynComponent or Docker or MsixPackage;

    /// <summary>A packaged Windows app (M44): MAUI's and WinUI's templates name the packaged run so; the project's WindowsPackageType decides how it starts.</summary>
    public const string MsixPackage = "MsixPackage";

    /// <summary>A container profile's host port for the container's HTTP port (<c>httpPort</c>); null: a free one.</summary>
    public int? HttpPort { get; init; }

    /// <summary>A container profile's host port for the container's HTTPS port (<c>sslPort</c>).</summary>
    public int? SslPort { get; init; }

    /// <summary>A container profile's <c>publishAllPorts</c>: every exposed port is published, not just HTTP and HTTPS.</summary>
    public bool PublishAllPorts { get; init; }

    /// <summary>A container profile's <c>containerRunArguments</c>: more arguments for <c>docker run</c>.</summary>
    public string? ContainerRunArguments { get; init; }
}

/// <summary>Reads launch profiles (the format <c>dotnet run</c> and other IDEs use).</summary>
public static class LaunchSettings
{
    /// <summary>The profiles of a <c>launchSettings.json</c> text, in file order. Unknown properties are ignored.</summary>
    public static IReadOnlyList<LaunchProfile> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<LaunchProfile>();
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = profile.Value;
            Dictionary<string, string>? environment = null;
            if (value.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object)
            {
                environment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var variable in variables.EnumerateObject())
                {
                    environment[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString() ?? string.Empty : variable.Value.GetRawText();
                }
            }

            result.Add(new LaunchProfile(
                profile.Name,
                String(value, "commandName") ?? "Project",
                String(value, "commandLineArgs"),
                String(value, "workingDirectory"),
                String(value, "executablePath"),
                String(value, "applicationUrl"),
                value.TryGetProperty("launchBrowser", out var browser) && browser.ValueKind == JsonValueKind.True,
                String(value, "launchUrl"),
                environment,
                String(value, "inspectUri"),
                value.TryGetProperty("distributionName", out var distribution) && distribution.ValueKind == JsonValueKind.String ? distribution.GetString() : null,
                String(value, "targetProject"))
            {
                HttpPort = Int(value, "httpPort"),
                SslPort = Int(value, "sslPort"),
                PublishAllPorts = value.TryGetProperty("publishAllPorts", out var all) && all.ValueKind == JsonValueKind.True,
                ContainerRunArguments = String(value, "containerRunArguments") ?? String(value, "dockerfileRunArguments"),
            });
        }

        return result;
    }

    /// <summary>Reads a launch settings file; a missing or malformed file has no profiles (<paramref name="error"/> says why).</summary>
    public static IReadOnlyList<LaunchProfile> Read(string? path, out string? error)
    {
        error = null;
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            return [];
        }
    }

    /// <summary>
    /// <see cref="Read"/> through the file system that holds <paramref name="path"/>: a remote window's files are the
    /// server's (M21), under a root that doesn't exist here.
    /// </summary>
    public static async Task<(IReadOnlyList<LaunchProfile> Profiles, string? Error)> ReadAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || FileSystems.For(path) is not { IsRemote: true } provider)
        {
            var local = Read(path, out var error);
            return (local, error);
        }

        try
        {
            if (!(await provider.StatAsync(path, cancellationToken).ConfigureAwait(false)).Exists)
            {
                return ([], null);
            }

            return (Parse(Encoding.UTF8.GetString(await provider.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"{path}: {ex.Message}");
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is > 0 and < 65536 ? number : null;
}

/// <summary>What to start for a run: a process and, when the profile asks for it, a browser URL.</summary>
public sealed record LaunchPlan(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? BrowserUrl)
{
    /// <summary>The program runs as x64 on an Arm64 machine (M33, <see cref="RuntimeSelection"/>): it needs the x64 debugger.</summary>
    public bool RunsAsX64 { get; init; }

    /// <summary>What Codalus IDE changed about the launch and why (M33, <see cref="RuntimeSelection"/>), for the program's output.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// The arguments exactly (a WSL command, a compiler response file, M14c); when null, <see cref="Arguments"/> is
    /// split like a command line.
    /// </summary>
    public IReadOnlyList<string>? ArgumentList { get; init; }

    /// <summary>The program in a WSL distribution, for the <c>WSL2</c> launch profile (M14c); null otherwise.</summary>
    public WslLaunch? Wsl { get; init; }

    /// <summary>The arguments as the program receives them.</summary>
    public IReadOnlyList<string> SplitArguments() => ArgumentList ?? Debugging.CommandLine.Split(Arguments);
}

/// <summary>
/// A program in a WSL distribution (the <c>WSL2</c> launch profile, M14c): the Linux <c>dotnet</c> runs the Windows
/// build's output through <c>/mnt/…</c>. <see cref="Distribution"/> empty is the default distribution. The plan's
/// environment is applied inside the distribution.
/// </summary>
public sealed record WslLaunch(string Distribution, string Program, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>
    /// <paramref name="plan"/> as a Windows command (<c>wsl.exe -d … --cd … --exec sh -lc '…'</c>) for runs without the
    /// debugger; a login shell, so the distribution's own <c>PATH</c> finds <c>dotnet</c>.
    /// </summary>
    public static LaunchPlan Apply(LaunchPlan plan)
    {
        if (plan.Wsl is not { } wsl)
        {
            return plan;
        }

        var script = new StringBuilder("exec env");
        foreach (var (name, value) in plan.Environment)
        {
            script.Append(' ').Append(ShellQuote(name + "=" + value));
        }

        script.Append(' ').Append(ShellQuote(wsl.Program));
        foreach (var argument in wsl.Arguments)
        {
            script.Append(' ').Append(ShellQuote(argument));
        }

        List<string> arguments = [];
        if (wsl.Distribution.Length > 0)
        {
            arguments.AddRange(["-d", wsl.Distribution]);
        }

        arguments.AddRange(["--cd", wsl.WorkingDirectory, "--exec", "sh", "-lc", script.ToString()]);
        return plan with
        {
            FileName = "wsl.exe",
            ArgumentList = arguments,
            Arguments = string.Join(' ', arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)),
        };
    }

    /// <summary>Quotes for <c>sh</c>: single quotes, with embedded ones as <c>'\''</c>.</summary>
    public static string ShellQuote(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or '=' or ':' or ',' or '+' or '@')
            ? text
            : "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The Linux path of a Windows path in WSL: <c>D:\src\App</c> is <c>/mnt/d/src/App</c> under the automount root,
    /// and <c>\\wsl$\Ubuntu\home\me</c> (or <c>\\wsl.localhost\…</c>) is <c>/home/me</c>.
    /// </summary>
    public static string ToLinuxPath(string windowsPath, string mountRoot = "/mnt/")
    {
        var path = windowsPath.Replace('/', '\\');
        foreach (var prefix in new[] { @"\\wsl$\", @"\\wsl.localhost\" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('\\', StringComparison.Ordinal);
                return slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
            }
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            var root = mountRoot.EndsWith('/') ? mountRoot : mountRoot + "/";
            return root + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/').TrimEnd('/');
        }

        return windowsPath.Replace('\\', '/');
    }
}

/// <summary>Turns a project's run information and a launch profile into a <see cref="LaunchPlan"/> (ADR 0006).</summary>
public static class LaunchPlanner
{
    /// <summary>
    /// <c>Project</c> profiles (and no profile) start the SDK's <c>RunCommand</c> with <c>RunArguments</c>, then the
    /// profile's arguments; <c>Executable</c> profiles start <c>executablePath</c>. The working directory defaults to the
    /// project folder; <c>applicationUrl</c> becomes <c>ASPNETCORE_URLS</c> unless the profile sets it.
    /// </summary>
    public static LaunchPlan Plan(RunInfo info, LaunchProfile? profile)
    {
        var projectDirectory = Path.GetDirectoryName(info.Path)!;
        if (profile is { CommandName: LaunchProfile.Wsl })
        {
            return PlanWsl(info, profile, projectDirectory);
        }

        if (profile is { CommandName: LaunchProfile.RoslynComponent })
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' runs the compiler on its target project: plan it with RoslynComponents.");
        }

        string fileName;
        var arguments = new StringBuilder();
        if (profile is { CommandName: "Executable" })
        {
            fileName = Expand(profile.ExecutablePath, projectDirectory, searchPath: true)
                       ?? throw new InvalidOperationException($"The launch profile '{profile.Name}' has no executablePath.");
        }
        else if (profile is { IsRunnable: false })
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' uses the '{profile.CommandName}' command, which Codalus IDE can't run. Use a 'Project' or 'Executable' profile.");
        }
        else if (info.RunCommand is { } command)
        {
            fileName = command;
            arguments.Append(info.RunArguments);
        }
        else if (info.TargetPath is { } target && !target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = target;
        }
        else
        {
            throw new InvalidOperationException($"{Path.GetFileName(info.Path)} can't be started: it is not an executable project (OutputType {info.OutputType ?? "Library"}).");
        }

        if (profile?.CommandLineArgs is { } args)
        {
            if (arguments.Length > 0)
            {
                arguments.Append(' ');
            }

            arguments.Append(args);
        }

        var workingDirectory = Expand(profile?.WorkingDirectory, projectDirectory) ?? info.RunWorkingDirectory ?? projectDirectory;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in profile?.EnvironmentVariables ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        if (profile is not null)
        {
            environment.TryAdd("DOTNET_LAUNCH_PROFILE", profile.Name);
        }

        string? browser = null;
        if (profile?.ApplicationUrl is { } urls)
        {
            environment.TryAdd("ASPNETCORE_URLS", urls);
        }

        if (profile is { LaunchBrowser: true })
        {
            browser = BrowserUrl(profile.ApplicationUrl, profile.LaunchUrl);
        }

        return new LaunchPlan(fileName, arguments.ToString(), workingDirectory, environment, browser);
    }

    /// <summary>
    /// The <c>WSL2</c> profile (M14c): the project's build output (<c>TargetPath</c>, a portable <c>.dll</c>) runs on
    /// the distribution's <c>dotnet</c>, from the Linux path of the working directory, with the profile's arguments
    /// and environment.
    /// </summary>
    private static LaunchPlan PlanWsl(RunInfo info, LaunchProfile profile, string projectDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' runs the program in WSL, which needs Windows.");
        }

        if (info.TargetPath is not { } target || !target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{Path.GetFileName(info.Path)} can't run in WSL: its output isn't a .NET assembly (.dll).");
        }

        var workingDirectory = Expand(profile.WorkingDirectory, projectDirectory) ?? info.RunWorkingDirectory ?? projectDirectory;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in profile.EnvironmentVariables ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        environment.TryAdd("DOTNET_LAUNCH_PROFILE", profile.Name);
        if (profile.ApplicationUrl is { } urls)
        {
            environment.TryAdd("ASPNETCORE_URLS", urls);
        }

        List<string> arguments = ["exec", WslLaunch.ToLinuxPath(target)];
        if (profile.CommandLineArgs is { } args)
        {
            arguments.AddRange(Debugging.CommandLine.Split(args));
        }

        var wsl = new WslLaunch(profile.DistributionName ?? string.Empty, "dotnet", arguments, WslLaunch.ToLinuxPath(workingDirectory));
        var plan = new LaunchPlan("wsl.exe", string.Empty, workingDirectory, environment, profile.LaunchBrowser ? BrowserUrl(profile.ApplicationUrl, profile.LaunchUrl) : null) { Wsl = wsl };
        return WslLaunch.Apply(plan);
    }

    /// <summary>
    /// The browser debugging target of a Blazor WebAssembly app (ADR 0026): the URL to open and the debugging proxy's
    /// <c>inspectUri</c> template, when the project runs on the WebAssembly dev server and the profile has one. Null
    /// for everything else, which debugs with netcoredbg.
    /// </summary>
    public static (string Url, string InspectUri)? BrowserDebugTarget(RunInfo info, LaunchProfile? profile) =>
        profile is { InspectUri: { } inspectUri, ApplicationUrl: { } urls }
            && info.RunArguments?.Contains("blazor-devserver", StringComparison.OrdinalIgnoreCase) == true
            && BrowserUrl(urls, profile.LaunchUrl) is { } url
            ? (url, inspectUri)
            : null;

    /// <summary>
    /// The page of a web app to debug with its server in one session (ADR 0027): with the WebAssembly debugging
    /// proxy's <c>inspectUri</c> when the profile has one (a Blazor Web App with WebAssembly components, whose page
    /// opens even when the profile doesn't launch a browser), else, with <paramref name="javaScript"/>, the page a
    /// profile that launches a browser opens. Null otherwise: the server debugs alone.
    /// </summary>
    public static (string Url, string? InspectUri)? PageDebugTarget(LaunchProfile? profile, bool javaScript)
    {
        if (profile is not { ApplicationUrl: { } urls } || BrowserUrl(urls, profile.LaunchUrl) is not { } url)
        {
            return null;
        }

        if (profile.InspectUri is { } inspectUri)
        {
            return (url, inspectUri);
        }

        // TODO(issue): .NET 11's Blazor template has no inspectUri: the IDE is expected to start the WebAssembly debugging
        // proxy (BrowserDebugHost) itself; until then its components' breakpoints don't bind (the page's scripts do).
        return javaScript && profile.LaunchBrowser ? (url, null) : null;
    }

    /// <summary>The first application URL with <paramref name="launchUrl"/> appended (an absolute launch URL wins).</summary>
    public static string? BrowserUrl(string? applicationUrl, string? launchUrl)
    {
        if (launchUrl is not null && Uri.TryCreate(launchUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.ToString();
        }

        var first = applicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null || !Uri.TryCreate(first.Replace("*", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var root))
        {
            return null;
        }

        return launchUrl is null ? root.ToString() : new Uri(root, launchUrl).ToString();
    }

    /// <summary>
    /// Expands <c>%VARIABLES%</c> and makes a relative path relative to the project folder; with
    /// <paramref name="searchPath"/>, a bare program name (<c>dotnet</c>) is left for the OS to find on the PATH.
    /// </summary>
    private static string? Expand(string? path, string projectDirectory, bool searchPath = false)
    {
        if (path is null)
        {
            return null;
        }

        var expanded = System.Environment.ExpandEnvironmentVariables(path);
        if (searchPath && expanded.IndexOfAny(['/', '\\']) < 0)
        {
            return expanded;
        }

        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(projectDirectory, expanded));
    }
}
