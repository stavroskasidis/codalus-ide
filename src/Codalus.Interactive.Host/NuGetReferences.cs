using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Codalus.Interactive.Host;

/// <summary>
/// Resolves <c>#r "nuget: Id, Version"</c> (ADR 0015). It restores a throwaway project with the SDK's <c>dotnet restore</c>,
/// so the package sources, source mapping and credentials of the <c>nuget.config</c> around the working directory apply,
/// as they do for the package manager (ADR 0012), then references the runtime assemblies of the package and its
/// dependencies from <c>project.assets.json</c>. Results are cached for the life of the host.
/// </summary>
internal sealed class NuGetReferences(string workingDirectory)
{
    private readonly ConcurrentDictionary<string, Resolution> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>Raised with progress and failure messages, for the interactive window.</summary>
    public event Action<string>? Message;

    /// <summary>The assemblies of a package reference, with one metadata reference each (shared, so compilations dedupe them).</summary>
    public sealed record Resolution(string[] Assemblies, string? Error)
    {
        public PortableExecutableReference[] References { get; } = [.. Assemblies.Select(p => MetadataReference.CreateFromFile(p))];
    }

    /// <summary>Parses <c>Id</c>, <c>Id, Version</c> or <c>Id/Version</c>. No version means the latest stable one.</summary>
    public static (string Id, string Version)? Parse(string specification)
    {
        var text = specification.Trim();
        var separator = text.IndexOfAny([',', '/']);
        var id = (separator < 0 ? text : text[..separator]).Trim();
        var version = separator < 0 ? "*" : text[(separator + 1)..].Trim();
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')) || version.Length == 0 || version.Any(c => c is '"' or '<' or '>' or '&'))
        {
            return null;
        }

        return (id, version);
    }

    public Resolution Resolve(string specification) => _cache.GetOrAdd(specification.Trim(), ResolveCore);

    /// <summary>A resolution made earlier, without restoring anything.</summary>
    public Resolution? Cached(string specification) => _cache.TryGetValue(specification.Trim(), out var resolution) ? resolution : null;

    private Resolution ResolveCore(string specification)
    {
        if (Parse(specification) is not var (id, version))
        {
            return new([], $"'{specification}' is not a package reference. Use #r \"nuget: Id, Version\".");
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}/{version}/{WorkingDirectory}")))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "codalus-replhost", "nuget", key);
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "references.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{id}" Version="{version}" />
              </ItemGroup>
            </Project>
            """);

        Message?.Invoke($"Restoring {id} {(version == "*" ? "(latest)" : version)}...");
        var start = new ProcessStartInfo(DotnetPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        foreach (var argument in new[] { "restore", project, "--nologo", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false" })
        {
            start.ArgumentList.Add(argument);
        }

        if (FindNuGetConfig(WorkingDirectory) is { } config)
        {
            start.ArgumentList.Add("--configfile");
            start.ArgumentList.Add(config);
        }

        // The host's own environment must not leak into the SDK it starts.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            return new([], $"Restoring {id} timed out.");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var errors = output.ToString().Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(5);
            return new([], $"Restoring {id} failed:\n{string.Join('\n', errors)}");
        }

        return ReadAssets(Path.Combine(directory, "obj", "project.assets.json"));
    }

    /// <summary>The runtime assemblies of every package in the restore graph.</summary>
    internal static Resolution ReadAssets(string assetsFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
        var root = document.RootElement;
        var folders = root.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
        var libraries = root.GetProperty("libraries");
        var assemblies = new List<string>();
        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            foreach (var package in target.Value.EnumerateObject())
            {
                if (!package.Value.TryGetProperty("type", out var type) || type.GetString() != "package" ||
                    !package.Value.TryGetProperty("runtime", out var runtime) ||
                    !libraries.TryGetProperty(package.Name, out var library) || library.GetProperty("path").GetString() is not { } path)
                {
                    continue;
                }

                foreach (var asset in runtime.EnumerateObject())
                {
                    if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // "_._" marks an empty folder
                    }

                    var file = folders.Select(f => Path.GetFullPath(Path.Combine(f, path, asset.Name))).FirstOrDefault(File.Exists);
                    if (file is not null)
                    {
                        assemblies.Add(file);
                    }
                }
            }

            break; // one target framework
        }

        return new([.. assemblies.Distinct(StringComparer.OrdinalIgnoreCase)], null);
    }

    private static string? FindNuGetConfig(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var match = current.Exists ? current.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)) : null;
            if (match is not null)
            {
                return match.FullName;
            }
        }

        return null;
    }

    private static string DotnetPath() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)!)!)!)!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") is var root && File.Exists(root)
                ? root
                : "dotnet";

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }
}
