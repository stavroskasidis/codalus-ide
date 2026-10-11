using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Containers;

/// <summary>A <c>FROM image AS name</c> stage of a Dockerfile.</summary>
public sealed record DockerfileStage(string Image, string? Name);

/// <summary>What Daedalus Studio reads from a Dockerfile: its stages and the ports it exposes.</summary>
public sealed record DockerfileInfo(IReadOnlyList<DockerfileStage> Stages, IReadOnlyList<int> ExposedPorts)
{
    /// <summary>The stage a fast-mode container runs (<c>base</c>, as the .NET templates name it), or null.</summary>
    public DockerfileStage? BaseStage => Stages.FirstOrDefault(s => string.Equals(s.Name, "base", StringComparison.OrdinalIgnoreCase));

    public static DockerfileInfo Parse(string text)
    {
        List<DockerfileStage> stages = [];
        List<int> ports = [];
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in JoinContinuations(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (words[0].ToUpperInvariant())
            {
                case "ARG" when words.Length > 1 && stages.Count == 0:
                    var equals = words[1].IndexOf('=', StringComparison.Ordinal);
                    if (equals > 0)
                    {
                        arguments[words[1][..equals]] = words[1][(equals + 1)..].Trim('"');
                    }

                    break;
                case "FROM" when words.Length > 1:
                    var image = words.Skip(1).First(w => !w.StartsWith("--", StringComparison.Ordinal));
                    var asAt = Array.FindIndex(words, w => w.Equals("AS", StringComparison.OrdinalIgnoreCase));
                    stages.Add(new DockerfileStage(Substitute(image, arguments), asAt > 0 && asAt + 1 < words.Length ? words[asAt + 1] : null));
                    break;
                case "EXPOSE":
                    foreach (var word in words.Skip(1))
                    {
                        if (int.TryParse(word.Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 && !ports.Contains(port))
                        {
                            ports.Add(port);
                        }
                    }

                    break;
            }
        }

        return new DockerfileInfo(stages, ports);
    }

    private static IEnumerable<string> JoinContinuations(string text)
    {
        var current = new StringBuilder();
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.EndsWith('\\'))
            {
                current.Append(line[..^1]).Append(' ');
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }
    }

    private static string Substitute(string text, Dictionary<string, string> arguments)
    {
        foreach (var (name, value) in arguments)
        {
            text = text.Replace("${" + name + "}", value, StringComparison.Ordinal).Replace("$" + name, value, StringComparison.Ordinal);
        }

        return text;
    }
}

/// <summary>A project as Add Docker Support sees it.</summary>
public sealed record DockerProject(string Path, string TargetFramework, bool IsWeb, IReadOnlyList<string> ProjectReferences);

/// <summary>
/// Container support for projects (M17a, ADR 0042): Add Docker Support's files, which follow the official .NET
/// container templates (a multi-stage Dockerfile on the <c>mcr.microsoft.com/dotnet</c> images, built from the solution
/// folder, with a <c>.dockerignore</c> there and a "Container (Dockerfile)" launch profile), and the files the
/// Dockerfile of a project is looked for in.
/// </summary>
public static class DockerSupport
{
    public const string ContainerProfileName = "Container (Dockerfile)";

    private static readonly string[] s_dockerfileNames = ["Dockerfile", "Containerfile"];

    /// <summary>The image tag of a target framework (<c>net10.0</c> → <c>10.0</c>); null when it has no .NET image.</summary>
    public static string? ImageVersion(string targetFramework)
    {
        var framework = targetFramework.Split('-')[0];
        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) || framework.Length < 4 || !char.IsAsciiDigit(framework[3]))
        {
            return null;
        }

        var version = framework[3..];
        if (version.StartsWith("coreapp", StringComparison.OrdinalIgnoreCase))
        {
            version = version["coreapp".Length..];
        }

        return version.Contains('.', StringComparison.Ordinal) && double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 2
            ? version
            : null;
    }

    /// <summary>The project's Dockerfile: <c>Dockerfile</c> (or <c>Containerfile</c>) in the project's folder, or null.</summary>
    public static string? FindDockerfile(string projectPath)
    {
        var folder = Path.GetDirectoryName(projectPath)!;
        return s_dockerfileNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The build context of a project's Dockerfile: the folder its <c>COPY</c> lines are relative to. Add Docker Support
    /// puts it at the solution's folder (the templates' choice), so it is the closest folder above the project with a
    /// <c>.dockerignore</c>, else the project's own folder.
    /// </summary>
    public static string BuildContext(string projectPath, string? solutionFolder)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, ".dockerignore")))
            {
                return current;
            }

            if (solutionFolder is not null && string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(solutionFolder), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return folder;
    }

    /// <summary>A Dockerfile for <paramref name="project"/>, built from <paramref name="context"/> (the solution's folder).</summary>
    public static string Dockerfile(DockerProject project, string context)
    {
        var version = ImageVersion(project.TargetFramework) ?? throw new InvalidOperationException($"There's no .NET container image for {project.TargetFramework}.");
        var name = Path.GetFileNameWithoutExtension(project.Path);
        var file = Path.GetFileName(project.Path);
        var relative = Relative(context, project.Path);
        var folder = Relative(context, Path.GetDirectoryName(project.Path)!);
        var runtime = project.IsWeb ? "aspnet" : "runtime";
        var modern = double.Parse(version, CultureInfo.InvariantCulture) >= 8;
        var text = new StringBuilder();
        text.Append("# See https://aka.ms/customizecontainer to learn how to customize your container and how the IDE uses this Dockerfile to build images faster.\n\n");
        text.Append("# The base stage: the image the app runs on. Fast mode (Debug) runs the build output on it.\n");
        text.Append(CultureInfo.InvariantCulture, $"FROM mcr.microsoft.com/dotnet/{runtime}:{version} AS base\n");
        if (modern)
        {
            text.Append("USER $APP_UID\n");
        }

        text.Append("WORKDIR /app\n");
        if (project.IsWeb)
        {
            text.Append(modern ? "EXPOSE 8080\nEXPOSE 8081\n" : "EXPOSE 80\nEXPOSE 443\n");
        }

        text.Append("\n# The build stage: builds the project.\n");
        text.Append(CultureInfo.InvariantCulture, $"FROM mcr.microsoft.com/dotnet/sdk:{version} AS build\n");
        text.Append("ARG BUILD_CONFIGURATION=Release\n");
        text.Append("WORKDIR /src\n");
        foreach (var path in project.ProjectReferences.Prepend(project.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var copied = Relative(context, path);
            var into = Relative(context, Path.GetDirectoryName(path)!);
            text.Append(CultureInfo.InvariantCulture, $"COPY [\"{copied}\", \"{(into.Length == 0 ? "." : into)}/\"]\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"RUN dotnet restore \"./{relative}\"\n");
        text.Append("COPY . .\n");
        text.Append("WORKDIR \"/src").Append(folder.Length == 0 ? string.Empty : "/" + folder).Append("\"\n");
        text.Append(CultureInfo.InvariantCulture, $"RUN dotnet build \"./{file}\" -c $BUILD_CONFIGURATION -o /app/build\n");
        text.Append("\n# The publish stage: publishes the project.\n");
        text.Append("FROM build AS publish\n");
        text.Append("ARG BUILD_CONFIGURATION=Release\n");
        text.Append(CultureInfo.InvariantCulture, $"RUN dotnet publish \"./{file}\" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false\n");
        text.Append("\n# The final stage: the published app on the base image (Release, and fast mode turned off).\n");
        text.Append("FROM base AS final\n");
        text.Append("WORKDIR /app\n");
        text.Append("COPY --from=publish /app/publish .\n");
        text.Append(CultureInfo.InvariantCulture, $"ENTRYPOINT [\"dotnet\", \"{name}.dll\"]\n");
        return text.ToString();
    }

    /// <summary>The <c>.dockerignore</c> the .NET container templates write.</summary>
    public const string DockerIgnore = """
        **/.classpath
        **/.dockerignore
        **/.env
        **/.git
        **/.gitignore
        **/.project
        **/.settings
        **/.toolstarget
        **/.vs
        **/.vscode
        **/.daedalus
        **/*.*proj.user
        **/*.dbmdl
        **/*.jfm
        **/azds.yaml
        **/bin
        **/charts
        **/docker-compose*
        **/Dockerfile*
        **/node_modules
        **/npm-debug.log
        **/obj
        **/secrets.dev.yaml
        **/values.dev.yaml
        LICENSE
        README.md
        !**/.gitignore
        !.git/HEAD
        !.git/config
        !.git/packed-refs
        !.git/refs/heads/**

        """;

    /// <summary>
    /// <paramref name="existing"/> launch settings (or none) with the "Container (Dockerfile)" profile added (kept when
    /// it is already there): the container's HTTP port published, the browser opened on it.
    /// </summary>
    public static string AddContainerProfile(string? existing, bool isWeb)
    {
        var profiles = LaunchSettingsEditor.Read(existing);
        if (profiles.Any(p => p.Get("commandName") == LaunchProfile.Docker))
        {
            return existing ?? string.Empty;
        }

        var properties = new JsonObject { ["commandName"] = LaunchProfile.Docker };
        if (isWeb)
        {
            properties["launchBrowser"] = true;
            properties["launchUrl"] = "{Scheme}://{ServiceHost}:{ServicePort}";
            properties["environmentVariables"] = new JsonObject { ["ASPNETCORE_HTTP_PORTS"] = "8080" };
            properties["publishAllPorts"] = true;
            properties["useSSL"] = false;
        }

        profiles.Add(new EditableLaunchProfile(ContainerProfileName, properties));
        return LaunchSettingsEditor.Write(existing, profiles);
    }

    private static string Relative(string from, string to) => Path.GetRelativePath(from, to).Replace('\\', '/') is "." ? string.Empty : Path.GetRelativePath(from, to).Replace('\\', '/');

    /// <summary>The name of a project's development container: stable per project, so a new run replaces the last one.</summary>
    public static string ContainerName(string projectPath)
    {
        var name = new string([.. Path.GetFileNameWithoutExtension(projectPath).ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')]);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToLowerInvariant())))[..8].ToLowerInvariant();
        return $"daedalus-{(name.Length == 0 ? "app" : name)}-{hash}";
    }
}
