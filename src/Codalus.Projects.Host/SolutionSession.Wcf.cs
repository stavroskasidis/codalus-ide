using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codalus.Projects.Host.Packages;
using Codalus.Projects.Host.ProjectManagement;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// WCF service references (M42, ADR 0069): the user's own <c>dotnet-svcutil</c>, a local or global .NET tool, run as a
/// child process in the project's folder, only in trusted solutions (it builds the project to reuse its types). The
/// reference is written where and how Visual Studio writes it (<see cref="WcfConnectedServices"/>).
/// </summary>
internal sealed partial class SolutionSession
{
    public const string SvcutilTool = "dotnet-svcutil";

    /// <summary>A stand-in for <c>dotnet-svcutil</c> (an executable, or a <c>.dll</c> run with dotnet), for tests.</summary>
    public const string SvcutilVariable = "CODALUS_SVCUTIL";

    private static readonly TimeSpan s_svcutilTimeout = TimeSpan.FromMinutes(10);

    /// <summary>One run at a time: each can build the project, and two builds of one project collide.</summary>
    private readonly SemaphoreSlim _svcutilGate = new(1, 1);

    private static string? SvcutilStandIn => Environment.GetEnvironmentVariable(SvcutilVariable) is { Length: > 0 } path ? path : null;

    public async Task<WcfToolStatusResult> WcfStatusAsync(WcfToolStatusParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var manifest = DotnetTools.FindManifest(directory);
        var local = manifest is null ? null : DotnetTools.ReadManifest(manifest).FirstOrDefault(t => t.Id.Equals(SvcutilTool, StringComparison.OrdinalIgnoreCase));

        // "--help" prints the banner with the version and exits with 0; a missing tool makes dotnet fail.
        var (exitCode, output, _) = await RunSvcutilAsync(directory, ["--help"], stream: false, cancellationToken).ConfigureAwait(false);
        var available = exitCode == 0 && output.Contains("Svcutil", StringComparison.OrdinalIgnoreCase);
        var version = available ? SvcutilVersion(output) : null;
        var source = !available ? null : SvcutilStandIn is not null ? SvcutilVariable : local is not null ? "local" : "global";
        string? message = available ? null
            : local is not null ? "dotnet-svcutil is in the tool manifest but isn't restored. Restore the .NET tools (dotnet tool restore)."
            : "dotnet-svcutil isn't installed. Install it as a local tool (dotnet tool install dotnet-svcutil) or globally.";
        return new WcfToolStatusResult(available, version, source, manifest, local is not null, message);
    }

    /// <summary>The version in svcutil's banner: <c>[Microsoft.Tools.ServiceModel.Svcutil, Version 8.0.0]</c>.</summary>
    internal static string? SvcutilVersion(string output) => SvcutilBanner().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    public async Task<WcfToolStatusResult> WcfInstallAsync(WcfToolInstallParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var status = await WcfStatusAsync(new WcfToolStatusParams(project), cancellationToken).ConfigureAwait(false);
        if (status.ToolAvailable)
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

            var version = parameters.Version ?? await PickVersionAsync(SvcutilTool, _ => true, "8.0.0", cancellationToken).ConfigureAwait(false);
            result = await tools.RunActionAsync(new ToolCommandParams(ToolActions.Install, SvcutilTool, version), cancellationToken).ConfigureAwait(false);
        }

        await _events.WcfOutputAsync(new ScaffoldOutputParams(result.Succeeded ? "info" : "error", result.Output.Trim())).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{SvcutilTool} could not be installed; see the Service References output.");
        }

        return await WcfStatusAsync(new WcfToolStatusParams(project), cancellationToken).ConfigureAwait(false);
    }

    public WcfReferencesResult WcfReferencesOf(WcfReferencesParams parameters) =>
        new(WcfConnectedServices.List(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(parameters.Project))!));

    public async Task<WcfReferenceRunResult> AddWcfReferenceAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        if (!WcfConnectedServices.IsValidName(parameters.Name))
        {
            throw new ArgumentException($"'{parameters.Name}' isn't a valid reference name: use letters, digits, '.', '_' and '-', starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(parameters.Input))
        {
            throw new ArgumentException("Enter the service's address (its metadata URL) or a WSDL file.");
        }

        var folder = WcfConnectedServices.FolderOf(directory, parameters.Name);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new InvalidOperationException($"{WcfConnectedServices.ServicesFolder}/{parameters.Name} already exists; pick another name, or update that reference.");
        }

        var input = parameters.Input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            input = System.IO.Path.GetFullPath(uri is { IsFile: true } ? uri.LocalPath : System.IO.Path.Combine(directory, input));
            if (!File.Exists(input))
            {
                throw new FileNotFoundException($"{input} doesn't exist.", input);
            }
        }

        var args = WcfConnectedServices.AddArguments(parameters with { Input = input }, project);
        return await RunReferenceAsync(project, folder, args, parameters with { Input = input }, isUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WcfReferenceRunResult> UpdateWcfReferenceAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var folder = WcfConnectedServices.List(directory).FirstOrDefault(r => r.Name.Equals(parameters.Name, StringComparison.OrdinalIgnoreCase))?.Folder
                     ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(project)} has no WCF reference named {parameters.Name}.");
        return await RunReferenceAsync(project, folder, null, parameters, isUpdate: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WcfReferencesResult> RemoveWcfReferenceAsync(WcfReferenceRemoveParams parameters)
    {
        RequireTrusted("Service references");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var reference = WcfConnectedServices.List(directory).FirstOrDefault(r => r.Name.Equals(parameters.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(project)} has no WCF reference named {parameters.Name}.");
        Trash.Move(reference.Folder);
        await _events.LogAsync("info", $"Removed the WCF reference {reference.Name} ({reference.Folder}).").ConfigureAwait(false);
        await ReevaluateAsync([project]).ConfigureAwait(false);
        return WcfReferencesOf(new WcfReferencesParams(project));
    }

    /// <summary>
    /// Runs svcutil for a new reference (<paramref name="addArguments"/>) or an update (its options written into the
    /// reference's file first, then <c>--update</c>), then keeps Visual Studio's layout: svcutil's own options file becomes
    /// <c>ConnectedService.json</c>. A project file svcutil changed (the System.ServiceModel packages) is evaluated and restored.
    /// </summary>
    private async Task<WcfReferenceRunResult> RunReferenceAsync(string project, string folder, List<string>? addArguments, WcfReferenceParams parameters, bool isUpdate, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(project)!;
        await _svcutilGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Stamps(directory);
            List<string> args;
            if (isUpdate)
            {
                var existing = WcfConnectedServices.Existing(folder) ?? throw new InvalidOperationException($"{folder} has no options file.");
                var options = WcfConnectedServices.Apply(existing.Options, parameters, null);
                if (System.IO.Path.GetFileName(existing.File) == WcfConnectedServices.ConnectedServiceFile)
                {
                    WcfConnectedServices.Write(existing.File, options);
                }
                else
                {
                    // svcutil's own layout stays svcutil's: its file with the options changed.
                    var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(existing.File))!.AsObject();
                    root["options"] = options;
                    File.WriteAllText(existing.File, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                }

                args = WcfConnectedServices.UpdateArguments(System.IO.Path.GetRelativePath(directory, folder), project);
            }
            else
            {
                args = addArguments!;
            }

            var (exitCode, output, _) = await RunSvcutilAsync(directory, args, stream: true, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"dotnet-svcutil failed: {SvcutilFailure(output)}");
            }

            // svcutil writes its own options file; a reference made here keeps Visual Studio's layout.
            if (System.IO.Path.GetDirectoryName(folder) is { } parent && System.IO.Path.GetFileName(parent) == WcfConnectedServices.ServicesFolder)
            {
                WcfConnectedServices.ConvertParams(folder);
                var file = System.IO.Path.Combine(folder, WcfConnectedServices.ConnectedServiceFile);
                if (!File.Exists(file) && File.Exists(System.IO.Path.Combine(folder, WcfConnectedServices.CodeFile)))
                {
                    WcfConnectedServices.Write(file, WcfConnectedServices.Apply(null, parameters, DotnetFrameworkOf(project)));
                }
            }

            var after = Stamps(directory);
            var created = after.Keys.Where(f => !before.ContainsKey(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var modified = after.Where(f => before.TryGetValue(f.Key, out var stamp) && stamp != f.Value).Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var reference = WcfConnectedServices.List(directory).FirstOrDefault(r => s_pathComparer.Equals(r.Folder, folder))
                            ?? throw new InvalidOperationException($"dotnet-svcutil wrote no reference in {folder}: {SvcutilFailure(output)}");
            if (modified.Concat(created).Any(f => s_pathComparer.Equals(f, project) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)))
            {
                await ReevaluateAsync([project]).ConfigureAwait(false);
                StartRestore(project);
            }

            return new WcfReferenceRunResult(reference, created, modified, output);
        }
        finally
        {
            _svcutilGate.Release();
        }
    }

    /// <summary>The project's first target framework (<c>net10.0</c>), for a reference file written without svcutil's.</summary>
    private string? DotnetFrameworkOf(string project) =>
        _evaluated.TryGetValue(project, out var evaluated) ? evaluated.Info.TargetFrameworks.FirstOrDefault() : null;

    /// <summary>svcutil's error: its "Error:" lines, else the output's last lines.</summary>
    internal static string SvcutilFailure(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Where(l => l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) || l.Contains(": error ", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (errors.Length > 0)
        {
            return string.Join(" ", errors.Take(3));
        }

        var tail = lines.Where(l => !l.StartsWith("If you would like more help", StringComparison.Ordinal)).TakeLast(2).ToArray();
        return tail.Length > 0 ? string.Join(" ", tail) : "no output.";
    }

    /// <summary>
    /// Runs svcutil: the stand-in when <see cref="SvcutilVariable"/> names one, else <c>dotnet svcutil</c> (the local
    /// tool from the manifest, or the global one on the PATH), with its telemetry off and the CLI in English.
    /// </summary>
    private async Task<(int ExitCode, string Output, int Errors)> RunSvcutilAsync(string directory, IReadOnlyList<string> args, bool stream, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi;
        string display;
        if (SvcutilStandIn is { } standIn)
        {
            var isDll = standIn.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            psi = isDll ? DotnetProcess.StartInfo(_dotnet, directory, [standIn, .. args]) : DotnetProcess.StartInfo(standIn, directory, args);
            display = "dotnet-svcutil (" + SvcutilVariable + ")";
        }
        else
        {
            psi = DotnetProcess.StartInfo(_dotnet, directory, ["svcutil", .. args]);
            display = "dotnet svcutil";
        }

        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_SVCUTIL_TELEMETRY_OPTOUT"] = "1";
        DotnetProcess.NoLingeringBuildProcesses(psi); // it builds the project to reuse its types
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        if (stream)
        {
            await _events.WcfOutputAsync(new ScaffoldOutputParams("info", "> " + display + " " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)))).ConfigureAwait(false);
        }

        Process? process;
        try
        {
            process = DotnetProcess.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message, 1);
        }

        using var started = process ?? throw new InvalidOperationException("dotnet-svcutil did not start.");
        var text = new StringBuilder();
        var stdout = PumpSvcutilAsync(started.StandardOutput, "info", text, stream);
        var stderr = PumpSvcutilAsync(started.StandardError, "error", text, stream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(stream ? s_svcutilTimeout : TimeSpan.FromMinutes(2));
        await DotnetProcess.WaitOrKillAsync(started, timeout.Token).ConfigureAwait(false);
        await DotnetProcess.DrainAsync(stdout, stderr).ConfigureAwait(false);
        lock (text)
        {
            return (started.ExitCode, text.ToString(), stderr.IsCompletedSuccessfully ? stderr.Result : 0);
        }
    }

    private async Task<int> PumpSvcutilAsync(StreamReader reader, string level, StringBuilder text, bool stream)
    {
        var count = 0;
        try
        {
            await foreach (var line in Lines(reader).ConfigureAwait(false))
            {
                count += line.Trim().Length > 0 ? 1 : 0;
                lock (text)
                {
                    text.AppendLine(line);
                }

                if (stream)
                {
                    await _events.WcfOutputAsync(new ScaffoldOutputParams(level, line)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closed after the process exited, while a build it ran still held the pipe.
        }

        return count;
    }

    [GeneratedRegex(@"Svcutil,\s*Version\s+([0-9][0-9A-Za-z.\-+]*)")]
    private static partial Regex SvcutilBanner();
}
