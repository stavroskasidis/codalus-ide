using System.Diagnostics;
using System.Text;
using DaedalusStudio.Projects.Host.Packages;
using DaedalusStudio.Projects.Host.ProjectManagement;
using DaedalusStudio.Projects.Protocol;
using NuGet.Versioning;

namespace DaedalusStudio.Projects.Host;

/// <summary>
/// Scaffolding and service references (M15c, ADR 0041). Scaffolding runs the user's own
/// <c>dotnet aspnet-codegenerator</c> as a child process in the project's folder, only in trusted solutions; service
/// references are project edits (<c>OpenApiReference</c>, <c>Protobuf</c>) plus the packages that generate the code.
/// </summary>
internal sealed partial class SolutionSession
{
    public const string CodeGeneratorTool = "dotnet-aspnet-codegenerator";
    public const string CodeGeneratorDesignPackage = "Microsoft.VisualStudio.Web.CodeGeneration.Design";

    /// <summary>The Entity Framework scaffolders need it (not only <c>Microsoft.EntityFrameworkCore.Design</c>).</summary>
    public const string EfToolsPackage = "Microsoft.EntityFrameworkCore.Tools";

    /// <summary>The generators <c>scaffold/run</c> accepts.</summary>
    internal static readonly string[] Generators = ["controller", "view", "razorpage", "area", "identity", "minimalapi", "blazor", "blazor-identity"];

    private static readonly TimeSpan s_scaffoldTimeout = TimeSpan.FromMinutes(10);

    /// <summary>One generator at a time: each builds the project, and two builds of one project collide.</summary>
    private readonly SemaphoreSlim _scaffoldGate = new(1, 1);

    // ------------------------------------------------------------------ scaffolding

    public async Task<ScaffoldStatusResult> ScaffoldStatusAsync(ScaffoldStatusParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Scaffolding");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var manifest = DotnetTools.FindManifest(directory);
        var local = manifest is null ? null : DotnetTools.ReadManifest(manifest).FirstOrDefault(t => t.Id.Equals(CodeGeneratorTool, StringComparison.OrdinalIgnoreCase));

        // "--help" prints the tool's usage without building; a missing tool makes dotnet fail.
        var (exitCode, text) = await RunCliAsync(directory, ["aspnet-codegenerator", "--help"], cancellationToken).ConfigureAwait(false);
        var available = exitCode == 0 && text.Contains("aspnet-codegenerator", StringComparison.OrdinalIgnoreCase);
        string? version = null;
        string? source = null;
        if (available)
        {
            if (local is not null)
            {
                (version, source) = (local.Version, "local");
            }
            else
            {
                var (_, global) = await RunCliAsync(directory, ["tool", "list", "--global"], cancellationToken).ConfigureAwait(false);
                version = DotnetTools.ParseList(global).FirstOrDefault(t => t.Id.Equals(CodeGeneratorTool, StringComparison.OrdinalIgnoreCase))?.Version;
                source = "global";
            }
        }

        var packages = DeclaredPackages(project);
        var design = packages.GetValueOrDefault(CodeGeneratorDesignPackage);
        var providers = EfProviderPackages.Where(packages.ContainsKey).ToArray();
        var efTools = packages.GetValueOrDefault(EfToolsPackage);
        var dotnetVersion = DotnetVersionOf(project);
        string? message = !available
            ? local is not null
                ? "dotnet-aspnet-codegenerator is in the tool manifest but isn't restored. Restore the .NET tools (dotnet tool restore)."
                : "dotnet-aspnet-codegenerator isn't installed. Install it as a local tool (dotnet tool install dotnet-aspnet-codegenerator) or globally."
            : design is null
                ? $"The project doesn't reference {CodeGeneratorDesignPackage}, which the code generator needs."
                : null;
        return new ScaffoldStatusResult(available, version, source, manifest, local is not null, design, dotnetVersion, providers, efTools, message);
    }

    /// <summary>The project's own package references (id → version), from a fresh evaluation: restored or not.</summary>
    private Dictionary<string, string?> DeclaredPackages(string project)
    {
        using var evaluation = EvaluateForPackages(project);
        var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in evaluation.Inner.SelectMany(p => p.Project.GetItems("PackageReference")))
        {
            if (!string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase))
            {
                var version = item.GetMetadataValue("Version") is { Length: > 0 } v ? v : item.GetMetadataValue("VersionOverride");
                if (version.Length == 0)
                {
                    version = evaluation.Outer.GetItems("PackageVersion").FirstOrDefault(p => p.EvaluatedInclude.Equals(item.EvaluatedInclude, StringComparison.OrdinalIgnoreCase))?.GetMetadataValue("Version") ?? string.Empty;
                }

                packages.TryAdd(item.EvaluatedInclude, version.Length > 0 ? version : null);
            }
        }

        return packages;
    }

    /// <summary>The project's .NET version (<c>10.0</c> for <c>net10.0</c>), from its first target framework.</summary>
    private string? DotnetVersionOf(string project)
    {
        if (!_evaluated.TryGetValue(project, out var evaluated) || evaluated.Info.TargetFrameworks.FirstOrDefault() is not { } framework)
        {
            return null;
        }

        var digits = framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? framework[3..].Split('-')[0] : string.Empty;
        return Version.TryParse(digits, out var parsed) && parsed.Major >= 5 ? $"{parsed.Major}.{Math.Max(0, parsed.Minor)}" : null;
    }

    public async Task<ScaffoldStatusResult> ScaffoldInstallAsync(ScaffoldInstallParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Scaffolding");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var status = await ScaffoldStatusAsync(new ScaffoldStatusParams(project), cancellationToken).ConfigureAwait(false);
        var major = status.DotnetVersion is { } dotnet ? int.Parse(dotnet.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var version = parameters.Version
                      ?? status.DesignPackageVersion
                      ?? await PickVersionAsync(CodeGeneratorDesignPackage, v => major is null || v.Major == major, major is { } m ? $"{m}.0.0" : "10.0.2", cancellationToken).ConfigureAwait(false);
        if (!status.ToolAvailable)
        {
            var tools = Tools;
            (bool Succeeded, string Output) result;
            if (status.ManifestListsTool)
            {
                result = await tools.RunActionAsync(new ToolCommandParams(ToolActions.Restore), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (status.Manifest is null)
                {
                    await tools.RunActionAsync(new ToolCommandParams(ToolActions.CreateManifest), cancellationToken).ConfigureAwait(false);
                }

                result = await tools.RunActionAsync(new ToolCommandParams(ToolActions.Install, CodeGeneratorTool, version), cancellationToken).ConfigureAwait(false);
            }

            await _events.ScaffoldOutputAsync(new ScaffoldOutputParams(result.Succeeded ? "info" : "error", result.Output)).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"{CodeGeneratorTool} could not be installed; see the Scaffolding output.");
            }
        }

        var packages = DeclaredPackages(project);
        var efCore = packages.FirstOrDefault(p => p.Key.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) && p.Value is not null).Value;
        var addTools = status.EfToolsVersion is null && (status.EfProviders.Length > 0 || efCore is not null);
        if (status.DesignPackageVersion is null || addTools)
        {
            if (status.DesignPackageVersion is null)
            {
                await InstallPackageAsync(new PackageInstallParams([new PackageEditTarget(project)], CodeGeneratorDesignPackage, version, Restore: false), cancellationToken).ConfigureAwait(false);
            }

            if (addTools)
            {
                var toolsVersion = efCore ?? await PickVersionAsync(EfToolsPackage, v => major is null || v.Major == major, version, cancellationToken).ConfigureAwait(false);
                await InstallPackageAsync(new PackageInstallParams([new PackageEditTarget(project)], EfToolsPackage, toolsVersion, Restore: false), cancellationToken).ConfigureAwait(false);
                ProjectManagement.ServiceReferences.MarkPrivate(project, EfToolsPackage);
            }

            await ReevaluateAsync([project]).ConfigureAwait(false);
            var (exitCode, text) = await RunCliAsync(System.IO.Path.GetDirectoryName(project)!, ["restore", project, "--nologo"], cancellationToken).ConfigureAwait(false);
            await _events.ScaffoldOutputAsync(new ScaffoldOutputParams(exitCode == 0 ? "info" : "error", text.Trim())).ConfigureAwait(false);
        }

        return await ScaffoldStatusAsync(new ScaffoldStatusParams(project), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScaffoldRunResult> ScaffoldRunAsync(ScaffoldRunParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Scaffolding");
        if (!Generators.Contains(parameters.Generator, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{parameters.Generator}' isn't a code generator; use one of {string.Join(", ", Generators)}.");
        }

        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        await _scaffoldGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Stamps(directory);
            var args = new List<string> { "aspnet-codegenerator", parameters.Generator };
            args.AddRange(parameters.Arguments);
            var (exitCode, output, errors) = await RunScaffolderAsync(directory, args, cancellationToken).ConfigureAwait(false);
            var after = Stamps(directory);
            var created = after.Keys.Where(f => !before.ContainsKey(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var modified = after.Where(f => before.TryGetValue(f.Key, out var stamp) && stamp != f.Value).Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // The generator exits with 0 after some of its errors (a missing package): an error and nothing written is a failure.
            if (exitCode != 0 || (errors > 0 && created.Length == 0 && modified.Length == 0))
            {
                throw new InvalidOperationException($"The code generator failed: {ScaffoldFailure(output)}");
            }

            if (modified.Any(f => s_pathComparer.Equals(f, project)) || created.Concat(modified).Any(f => f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)))
            {
                await ReevaluateAsync([project]).ConfigureAwait(false);
            }

            return new ScaffoldRunResult(created, modified, output);
        }
        finally
        {
            _scaffoldGate.Release();
        }
    }

    /// <summary>The generator's message: the line before its stack trace, a failed build, or the output's last lines.</summary>
    internal static string ScaffoldFailure(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var trace = Array.FindIndex(lines, l => l.StartsWith("at ", StringComparison.Ordinal));
        if (trace > 0)
        {
            return lines[trace - 1];
        }

        if (lines.Any(l => l.StartsWith("Build Failed", StringComparison.OrdinalIgnoreCase)))
        {
            return "the project's build failed; see the Scaffolding output.";
        }

        var tail = lines.Where(l => !l.StartsWith("RunTime ", StringComparison.Ordinal)).TakeLast(2).ToArray();
        return tail.Length > 0 ? string.Join(" ", tail) : "no output.";
    }

    private async Task<(int ExitCode, string Output, int Errors)> RunScaffolderAsync(string directory, List<string> args, CancellationToken cancellationToken)
    {
        var psi = DotnetProcess.StartInfo(_dotnet, directory, args);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1"; // the generator's build leaves no MSBuild nodes behind
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        await _events.ScaffoldOutputAsync(new ScaffoldOutputParams("info", "> dotnet " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)))).ConfigureAwait(false);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var text = new StringBuilder();
        var stdout = PumpScaffoldAsync(process.StandardOutput, "info", text);
        var stderr = PumpScaffoldAsync(process.StandardError, "error", text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(s_scaffoldTimeout);
        await DotnetProcess.WaitOrKillAsync(process, timeout.Token).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (process.ExitCode, text.ToString(), await stderr.ConfigureAwait(false));
    }

    /// <summary>Copies the lines to the output; returns how many there were.</summary>
    private async Task<int> PumpScaffoldAsync(StreamReader reader, string level, StringBuilder text)
    {
        var count = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            count += line.Trim().Length > 0 ? 1 : 0;
            lock (text)
            {
                text.AppendLine(line);
            }

            await _events.ScaffoldOutputAsync(new ScaffoldOutputParams(level, line)).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Each file under the project's folder (without bin and obj) and when it was written.</summary>
    private static Dictionary<string, DateTime> Stamps(string folder) =>
        Snapshot(folder).ToDictionary(f => f, File.GetLastWriteTimeUtc, s_pathComparer);

    /// <summary>
    /// The newest stable version of a package that <paramref name="accept"/> takes: from the global packages folder and
    /// local sources first (it restores offline), then from the configured sources, else <paramref name="fallback"/>.
    /// </summary>
    private async Task<string> PickVersionAsync(string id, Func<NuGetVersion, bool> accept, string fallback, CancellationToken cancellationToken)
    {
        foreach (var offline in (bool[])[true, false])
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(offline ? 10 : 30));
            try
            {
                var details = await NuGet.DetailsAsync(new PackageDetailsParams(id, Prerelease: false, Offline: offline), limit.Token).ConfigureAwait(false);
                var best = details.Versions
                    .Select(v => NuGetVersion.TryParse(v.Version, out var parsed) ? parsed : null)
                    .OfType<NuGetVersion>()
                    .Where(v => !v.IsPrerelease && accept(v))
                    .Max();
                if (best is not null)
                {
                    return best.ToNormalizedString();
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                await _events.LogAsync("info", $"No {(offline ? "local" : "online")} version of {id}: {ex.Message}").ConfigureAwait(false);
            }
        }

        return fallback;
    }

    // ------------------------------------------------------------------ service references

    public ServiceReferencesResult ServiceReferencesOf(ServiceReferencesParams parameters)
    {
        var project = System.IO.Path.GetFullPath(parameters.Project);
        using var evaluation = EvaluateForPackages(project);
        var references = ProjectManagement.ServiceReferences.Read(evaluation.Outer);
        var packages = DeclaredPackages(project)
            .Where(r => ProjectManagement.ServiceReferences.AllPackages.Contains(r.Key, StringComparer.OrdinalIgnoreCase))
            .Select(r => new ServiceReferencePackage(r.Key, r.Value))
            .ToArray();
        return new ServiceReferencesResult(references, packages);
    }

    public async Task<ServiceReferenceEditResult> AddServiceReferenceAsync(ServiceReferenceParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        if (!File.Exists(parameters.File))
        {
            throw new FileNotFoundException($"{parameters.File} doesn't exist.", parameters.File);
        }

        var changed = new List<string>();
        var added = new List<string>();
        var referenced = DeclaredPackages(project).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, fallback, isPrivate) in ProjectManagement.ServiceReferences.PackagesFor(parameters.Kind, parameters.GrpcServices))
        {
            if (referenced.Contains(id) || (id is "Google.Protobuf" or "Grpc.Tools" && referenced.Contains("Grpc.AspNetCore")))
            {
                continue;
            }

            var version = parameters.Versions?.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Version
                          ?? await PickVersionAsync(id, _ => true, fallback, cancellationToken).ConfigureAwait(false);
            var edit = await InstallPackageAsync(new PackageInstallParams([new PackageEditTarget(project)], id, version, Restore: false), cancellationToken).ConfigureAwait(false);
            changed.AddRange(edit.UpdatedFiles);
            if (isPrivate && ProjectManagement.ServiceReferences.MarkPrivate(project, id))
            {
                changed.Add(project);
            }

            added.Add(id + " " + version);
        }

        if (ProjectManagement.ServiceReferences.Set(project, parameters, create: true))
        {
            changed.Add(project);
        }

        return await FinishServiceReferenceEditAsync(project, changed, added, parameters.Restore).ConfigureAwait(false);
    }

    public async Task<ServiceReferenceEditResult> UpdateServiceReferenceAsync(ServiceReferenceParams parameters)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var changed = ProjectManagement.ServiceReferences.Set(project, parameters, create: false) ? new List<string> { project } : [];
        return await FinishServiceReferenceEditAsync(project, changed, [], restore: false).ConfigureAwait(false);
    }

    public async Task<ServiceReferenceEditResult> RemoveServiceReferenceAsync(ServiceReferenceRemoveParams parameters)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var changed = ProjectManagement.ServiceReferences.Remove(project, parameters.Kind, parameters.File) ? new List<string> { project } : [];
        if (parameters.DeleteFile && File.Exists(parameters.File))
        {
            Trash.Move(System.IO.Path.GetFullPath(parameters.File));
        }

        return await FinishServiceReferenceEditAsync(project, changed, [], restore: false).ConfigureAwait(false);
    }

    private async Task<ServiceReferenceEditResult> FinishServiceReferenceEditAsync(string project, List<string> changed, List<string> added, bool restore)
    {
        var files = changed.Distinct(s_pathComparer).ToArray();
        if (files.Length > 0)
        {
            await _events.LogAsync("info", $"Updated {string.Join(", ", files.Select(System.IO.Path.GetFileName))}.").ConfigureAwait(false);
            await ReevaluateAsync([project]).ConfigureAwait(false);
            if (restore)
            {
                StartRestore(project);
            }
        }

        return new ServiceReferenceEditResult(files, [.. added]);
    }
}
