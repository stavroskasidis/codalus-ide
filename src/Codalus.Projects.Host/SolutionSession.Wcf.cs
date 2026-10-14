using System.Diagnostics;
using System.Text;
using Codalus.Projects.Host.Packages;
using Codalus.Projects.Host.ProjectManagement;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// WCF service references (M42, ADR 0066): the user's own <c>dotnet-svcutil</c> as a child process in the project's
/// folder, like the code generator (ADR 0041), only in trusted solutions. It reads the metadata, generates the client,
/// adds the packages and records its options; we add Visual Studio's <c>ConnectedService.json</c>.
/// </summary>
internal sealed partial class SolutionSession
{
    private static readonly TimeSpan s_svcutilTimeout = TimeSpan.FromMinutes(5);

    public async Task<WcfToolResult> WcfToolAsync(WcfToolParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var status = await WcfToolStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        if (status.Available || !parameters.Install)
        {
            return status;
        }

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

            result = await tools.RunActionAsync(new ToolCommandParams(ToolActions.Install, WcfReferences.Tool, parameters.Version), cancellationToken).ConfigureAwait(false);
        }

        await _events.ScaffoldOutputAsync(new ScaffoldOutputParams(result.Succeeded ? "info" : "error", result.Output.Trim(), ServiceReferenceKinds.Wcf)).ConfigureAwait(false);
        return result.Succeeded
            ? await WcfToolStatusAsync(directory, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException($"{WcfReferences.Tool} could not be installed; see the Service References output.");
    }

    private async Task<WcfToolResult> WcfToolStatusAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = DotnetTools.FindManifest(directory);
        var local = manifest is null ? null : DotnetTools.ReadManifest(manifest).FirstOrDefault(t => t.Id.Equals(WcfReferences.Tool, StringComparison.OrdinalIgnoreCase));

        // "--help" prints the usage and exits; a missing tool makes dotnet fail.
        var (exitCode, text) = await RunCliAsync(directory, ["svcutil", "--help"], cancellationToken).ConfigureAwait(false);
        var available = exitCode == 0 && text.Contains("svcutil", StringComparison.OrdinalIgnoreCase);
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
                version = DotnetTools.ParseList(global).FirstOrDefault(t => t.Id.Equals(WcfReferences.Tool, StringComparison.OrdinalIgnoreCase))?.Version;
                source = "global";
            }
        }

        var message = available
            ? null
            : local is not null
                ? "dotnet-svcutil is in the tool manifest but isn't restored. Restore the .NET tools (dotnet tool restore)."
                : "dotnet-svcutil isn't installed. Install it as a local tool (dotnet tool install dotnet-svcutil) or globally.";
        return new WcfToolResult(available, version, source, manifest, local is not null, message);
    }

    public async Task<WcfReferenceResult> AddWcfReferenceAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        WcfReferences.RequireName(parameters.Name);
        var folder = WcfReferences.FolderOf(directory, parameters.Name);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new InvalidOperationException($"{System.IO.Path.Combine(WcfReferences.ConnectedServicesFolder, parameters.Name)} exists already: choose another name, or update that reference.");
        }

        var input = RequireInput(parameters.Input, directory);
        return await GenerateWcfAsync(project, folder, WcfReferences.AddArguments(project, parameters.Name, input, Namespace(parameters), parameters.Options), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WcfReferenceResult> UpdateWcfReferenceAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        WcfReferences.RequireName(parameters.Name);
        var folder = WcfReferences.FolderOf(directory, parameters.Name);
        if (WcfReferences.ReadFolder(directory, folder) is not { } existing)
        {
            throw new InvalidOperationException($"{parameters.Name} isn't a WCF service reference of {System.IO.Path.GetFileName(project)}.");
        }

        if (parameters.Input is null && parameters.Namespace is null && parameters.Options is null)
        {
            // As recorded: the tool's own update, which reads the folder's options.
            return await GenerateWcfAsync(project, folder, ["--update", existing.Include, "--projectFile", project, "--noLogo"], cancellationToken).ConfigureAwait(false);
        }

        // New options or inputs: generate it again from scratch into the same folder.
        var input = RequireInput(parameters.Input ?? existing.Input, directory);
        var ns = parameters.Namespace is { Length: > 0 } given ? given : existing.Namespace ?? Namespace(parameters);
        var args = WcfReferences.AddArguments(project, parameters.Name, input, ns, parameters.Options ?? existing.Wcf);
        foreach (var file in new[] { WcfReferences.ParamsFile, WcfReferences.ConnectedServiceFile, WcfReferences.OutputFile })
        {
            File.Delete(System.IO.Path.Combine(folder, file));
        }

        return await GenerateWcfAsync(project, folder, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceReferenceEditResult> RemoveWcfReferenceAsync(WcfRemoveParams parameters)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        WcfReferences.RequireName(parameters.Name);
        var folder = WcfReferences.FolderOf(System.IO.Path.GetDirectoryName(project)!, parameters.Name);
        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException($"{parameters.Name} isn't a WCF service reference of {System.IO.Path.GetFileName(project)}.");
        }

        Trash.Move(folder);
        await ReevaluateAsync([project]).ConfigureAwait(false);
        return new ServiceReferenceEditResult([folder], []);
    }

    /// <summary>The default namespace: the project's root namespace and the reference's name.</summary>
    private string Namespace(WcfReferenceParams parameters)
    {
        if (parameters.Namespace is { Length: > 0 } ns)
        {
            return ns.Trim();
        }

        var project = System.IO.Path.GetFullPath(parameters.Project);
        using var evaluation = EvaluateForPackages(project);
        var root = evaluation.Outer.GetPropertyValue("RootNamespace") is { Length: > 0 } r
            ? r
            : System.IO.Path.GetFileNameWithoutExtension(project).Replace(' ', '_').Replace('-', '_');
        return root + "." + parameters.Name;
    }

    /// <summary>An address as it is, or a file's full path (relative to the project's folder).</summary>
    private static string RequireInput(string? input, string directory)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Give the service's metadata address or a WSDL file.");
        }

        input = input.Trim();
        if (WcfReferences.IsUrl(input))
        {
            return input;
        }

        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, input));
        return File.Exists(path) ? path : throw new FileNotFoundException($"{path} doesn't exist.", path);
    }

    private async Task<WcfReferenceResult> GenerateWcfAsync(string project, string folder, List<string> args, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(project)!;
        await _scaffoldGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Stamps(directory);
            var (exitCode, output) = await RunSvcutilAsync(directory, args, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(System.IO.Path.Combine(folder, WcfReferences.OutputFile)))
            {
                throw new InvalidOperationException($"dotnet-svcutil failed: {SvcutilFailure(output)}");
            }

            WcfReferences.WriteConnectedService(folder);
            var after = Stamps(directory);
            var created = after.Keys.Where(f => !before.ContainsKey(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var modified = after.Where(f => before.TryGetValue(f.Key, out var stamp) && stamp != f.Value).Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (modified.Any(f => s_pathComparer.Equals(f, project)))
            {
                // The tool added the System.ServiceModel packages.
                await ReevaluateAsync([project]).ConfigureAwait(false);
                StartRestore(project);
            }

            return new WcfReferenceResult(folder, created, modified, output);
        }
        finally
        {
            _scaffoldGate.Release();
        }
    }

    /// <summary>The tool's error lines, else its output's last lines.</summary>
    internal static string SvcutilFailure(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Where(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase)).ToArray();
        var chosen = errors.Length > 0 ? errors : lines.Where(l => !l.StartsWith("If you would like more help", StringComparison.Ordinal)).TakeLast(2).ToArray();
        return chosen.Length > 0 ? string.Join(" ", chosen) : "no output.";
    }

    private async Task<(int ExitCode, string Output)> RunSvcutilAsync(string directory, List<string> args, CancellationToken cancellationToken)
    {
        args.Insert(0, "svcutil");
        var psi = DotnetProcess.StartInfo(_dotnet, directory, args);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_SVCUTIL_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1"; // the tool's builds leave no MSBuild nodes behind
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        await _events.ScaffoldOutputAsync(new ScaffoldOutputParams("info", "> dotnet " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)), ServiceReferenceKinds.Wcf)).ConfigureAwait(false);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var text = new StringBuilder();
        var stdout = PumpSvcutilAsync(process.StandardOutput, "info", text);
        var stderr = PumpSvcutilAsync(process.StandardError, "error", text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(s_svcutilTimeout);
        await DotnetProcess.WaitOrKillAsync(process, timeout.Token).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (process.ExitCode, text.ToString());
    }

    private async Task PumpSvcutilAsync(StreamReader reader, string level, StringBuilder text)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (text)
            {
                text.AppendLine(line);
            }

            await _events.ScaffoldOutputAsync(new ScaffoldOutputParams(level, line, ServiceReferenceKinds.Wcf)).ConfigureAwait(false);
        }
    }
}
