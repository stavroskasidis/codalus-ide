using System.Text;
using System.Text.Json;
using Daedalus.Projects.Protocol;

namespace Daedalus.Workbench.Build;

/// <summary>A profile of <c>Properties/launchSettings.json</c>. Only the <c>Project</c> and <c>Executable</c> commands run.</summary>
public sealed record LaunchProfile(
    string Name,
    string CommandName,
    string? CommandLineArgs = null,
    string? WorkingDirectory = null,
    string? ExecutablePath = null,
    string? ApplicationUrl = null,
    bool LaunchBrowser = false,
    string? LaunchUrl = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null)
{
    public bool IsRunnable => CommandName is "Project" or "Executable";
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
                environment));
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

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>What to start for a run: a process and, when the profile asks for it, a browser URL.</summary>
public sealed record LaunchPlan(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? BrowserUrl);

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
        string fileName;
        var arguments = new StringBuilder();
        if (profile is { CommandName: "Executable" })
        {
            fileName = Expand(profile.ExecutablePath, projectDirectory, searchPath: true)
                       ?? throw new InvalidOperationException($"The launch profile '{profile.Name}' has no executablePath.");
        }
        else if (profile is { IsRunnable: false })
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' uses the '{profile.CommandName}' command, which Daedalus Studio can't run. Use a 'Project' or 'Executable' profile.");
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
