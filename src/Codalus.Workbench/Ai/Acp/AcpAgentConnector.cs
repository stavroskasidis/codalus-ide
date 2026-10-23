using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>Whether a program an agent needs is there.</summary>
public enum AcpRequirementState
{
    Found,
    Missing,
    TooOld,
}

/// <summary>A program an agent needs (Node.js, npx, its own CLI), as the Connect dialog's row shows it.</summary>
/// <param name="Id"><c>node</c>, <c>npx</c> or <c>cli</c>.</param>
/// <param name="Name">The program's name (<c>Node.js</c>, <c>npx</c>, <c>pi</c>).</param>
/// <param name="State">Found, missing or too old.</param>
/// <param name="Version">Its version, when found.</param>
/// <param name="Path">Where it was found.</param>
/// <param name="Needed">The version the agent needs, or null for any.</param>
public sealed record AcpRequirement(string Id, string Name, AcpRequirementState State, string? Version, string? Path, string? Needed);

/// <summary>What an agent needs and has (<see cref="AcpAgentConnector.CheckAsync"/>), and how to install what's missing.</summary>
/// <param name="Rows">One row per program.</param>
/// <param name="Install">The ways to install the first missing one, through the user's own package managers, or a page.</param>
/// <param name="InstallTitle">What they install ("Install Node.js 22 or later"), or null.</param>
/// <param name="InstallNote">Why a package manager isn't offered ("Ubuntu's nodejs package is 18…"), or null.</param>
public sealed record AcpRequirements(IReadOnlyList<AcpRequirement> Rows, IReadOnlyList<AcpInstallOption> Install, string? InstallTitle, string? InstallNote)
{
    public bool Met => Rows.All(r => r.State == AcpRequirementState.Found);

    public AcpRequirement? Row(string id) => Rows.FirstOrDefault(r => r.Id == id);
}

/// <summary>A step of the connection test, as its checklist shows it.</summary>
public enum AcpTestStep
{
    Requirements,
    Download,
    Start,
    SignIn,
    Check,
    Done,
}

public enum AcpStepState
{
    Running,
    Done,
    Failed,
    Skipped,
}

/// <summary>A step of the test changed: it runs, ended or failed, with its line (npm's last <c>http fetch</c>, the agent's version…).</summary>
public sealed record AcpTestProgress(AcpTestStep Step, AcpStepState State, string? Detail);

/// <summary>How the test ended (ADR 0083).</summary>
public enum AcpTestOutcome
{
    /// <summary>It started, answered and the sign-in was confirmed: written to the settings.</summary>
    Connected,

    /// <summary>It started and created a session, but didn't say whether it's signed in: Save Anyway is offered.</summary>
    NotConfirmed,

    /// <summary>It said it isn't signed in.</summary>
    NeedsSignIn,

    /// <summary>A step failed (a download, the start, the sign-in): nothing is written.</summary>
    Failed,
}

/// <summary>The connection test's result, in plain words, with what it found.</summary>
public sealed record AcpTestResult(AcpTestOutcome Outcome, string Message)
{
    /// <summary>The step that failed, for <see cref="AcpTestOutcome.Failed"/>.</summary>
    public AcpTestStep? FailedStep { get; init; }

    /// <summary>The agent as it named itself in <c>initialize</c> (<c>claude-agent-acp 0.94.0</c>), and its protocol.</summary>
    public string? Agent { get; init; }

    public int? Protocol { get; init; }

    /// <summary>The session's modes, the current one first.</summary>
    public IReadOnlyList<string> Modes { get; init; } = [];

    /// <summary>Who the agent says is signed in (Claude's report: "Claude Max · …"), or null.</summary>
    public string? SignedInAs { get; init; }

    /// <summary>The agent can sign out (<c>logout</c>).</summary>
    public bool CanLogout { get; init; }

    /// <summary>The ways the agent offered to sign in, by name.</summary>
    public IReadOnlyList<string> Offered { get; init; } = [];

    /// <summary>Lines of the tool's own output for a failure (npm's errors), secrets redacted.</summary>
    public IReadOnlyList<string> Output { get; init; } = [];
}

/// <summary>What the Connect dialog asks the connector to test and write.</summary>
/// <param name="Template">The agent.</param>
/// <param name="SignIn">The way it signs in.</param>
public sealed record AcpConnectRequest(AcpAgentTemplate Template, AcpSignInOption SignIn)
{
    /// <summary>The key typed for a key option; held in memory until the entry is written, never logged.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The dialog's "Turn on AI assistance" box is checked (unsaved: written only with the entry, D43).</summary>
    public bool Consent { get; init; }

    /// <summary>Codalus's own Node.js runs the agent (D-node, the user's opt-in).</summary>
    public bool UseCodalusNode { get; init; }

    /// <summary>The key never prints (a record would).</summary>
    public override string ToString() => $"{Template.Id} ({SignIn.Id})";
}

/// <summary>What the connector needs from the shell: a command run in the IDE's terminal (a Terminal install, a sign-in).</summary>
public interface IAcpConnectHost
{
    /// <summary>
    /// Runs <paramref name="launch"/> in a new terminal titled <paramref name="title"/> and waits for it to end: its exit
    /// code, or null when it couldn't run there (a remote window's terminal). Cancelling kills it.
    /// </summary>
    Task<int?> RunInTerminalAsync(string title, string commandLine, ProcessLaunchInfo launch, string? hint, CancellationToken cancellationToken);
}

/// <summary>
/// Connects an agent from a template (ADR 0083, D43): checks what it needs, installs what's missing through the user's own
/// package managers, downloads its package, starts it in an empty folder with every request refused, signs it in, checks
/// the sign-in, and only then writes its <c>ai.agents</c> entry (and <c>ai.enabled</c> when the dialog's box turned AI on)
/// to the user's settings. Disconnect undoes it. Nothing here runs in a remote window; nothing is written while a test runs;
/// no prompt is ever sent. Each step is a line of the AI Output channel, with names, never values.
/// </summary>
public sealed partial class AcpAgentConnector(SettingsService settings, AiService ai, AcpAgents agents)
{
    /// <summary>How long a started agent that reports who is signed in has to say it (its own check takes up to 5 s).</summary>
    public static TimeSpan AuthStatusWait { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The PATH programs are looked for on (the PATH's form); null: this process's. Tests and the smoke step set stand-ins.</summary>
    public string? SearchPath { get; set; }

    /// <summary>The OS, for the install options.</summary>
    public PlatformKind Platform { get; set; } = PlatformInfo.Current;

    /// <summary><c>/etc/os-release</c>'s ID and ID_LIKE (Linux).</summary>
    public string? Distribution { get; set; } = OperatingSystem.IsLinux() ? InstallPlans.LinuxDistribution() : null;

    /// <summary>How long one <c>--version</c> (or <c>apt-cache policy</c>) may take.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the download step may take (a slow network: generous, and Cancel stops it).</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the agent has to answer <c>initialize</c> once its package is in npm's cache.</summary>
    public TimeSpan StartTimeout { get; set; } = AcpClient.DefaultStartTimeout;

    /// <summary>Codalus's own Node.js (D-node), or null when the shell has none.</summary>
    public Node.NodeRuntime? CodalusNode { get; set; }

    /// <summary>Reads the registry's PATH after a winget install (Windows); tests give their own.</summary>
    public Func<IReadOnlyList<string>> ReadWindowsPath { get; set; } = WindowsPath.ReadRegistry;

    /// <summary>The folder the test runs agents in (the dialog names it).</summary>
    public string TestFolder => agents.ConnectTestDirectory;

    /// <summary>The user's home folder (npm's global folder check).</summary>
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void Log(string agent, string line) => ai.LogAgent(agent, "connect: " + line);

    /// <summary>Why nothing can be connected now (a remote window: the agent would run on this machine, its sign-in elsewhere), or null.</summary>
    public string? WhyNot() =>
        agents.IsRemoteWindow()
            ? Localizer.Get("msg.acp.connect.remote", "Agents can't be connected from a remote window: they would run on this machine, while the folder and its terminal are on the remote one. Connect it from a local window.")
            : null;

    private void ThrowIfRemote()
    {
        if (WhyNot() is { } reason)
        {
            throw new AcpException(reason);
        }
    }

    // ------------------------------------------------------------------ requirements

    /// <summary>
    /// What <paramref name="template"/> needs and has, off the caller's thread, each program found on the PATH and asked its
    /// version once; with the install options for the first one missing. With <paramref name="codalusNode"/>, Node.js is
    /// Codalus's own.
    /// </summary>
    public async Task<AcpRequirements> CheckAsync(AcpAgentTemplate template, bool codalusNode, CancellationToken cancellationToken)
    {
        ThrowIfRemote();
        var rows = new List<AcpRequirement>();
        var nodeFolder = codalusNode && CodalusNode is { IsInstalled: true } runtime ? Path.GetDirectoryName(runtime.NodePath) : null;
        var path = nodeFolder is null ? SearchPath ?? Environment.GetEnvironmentVariable("PATH") : nodeFolder;
        if (template.Package is not null || template.NeedsCli && template.CliInstall(Platform).Any(o => o.Id == "npm"))
        {
            var needed = template.NodeMajor;
            var node = await ProbeAsync("node", "Node.js", path, cancellationToken).ConfigureAwait(false);
            rows.Add(node.State == AcpRequirementState.Found && needed is { } major && AcpNodeInstall.Major(node.Version ?? "") < major
                ? node with { State = AcpRequirementState.TooOld, Needed = major.ToString(CultureInfo.InvariantCulture) }
                : node with { Needed = needed?.ToString(CultureInfo.InvariantCulture) });
            rows.Add(await ProbeAsync("npx", "npx", path, cancellationToken).ConfigureAwait(false));
        }

        if (template.NeedsCli)
        {
            var cli = await ProbeAsync(template.Agent.Cli, template.Agent.Cli, SearchPath ?? Environment.GetEnvironmentVariable("PATH"), cancellationToken).ConfigureAwait(false);
            rows.Add(cli.State == AcpRequirementState.Found && template.CliMinimum is { } minimum && Older(cli.Version, minimum)
                ? cli with { Id = "cli", State = AcpRequirementState.TooOld, Needed = minimum }
                : cli with { Id = "cli", Needed = template.CliMinimum });
        }

        var missing = rows.FirstOrDefault(r => r.State != AcpRequirementState.Found);
        if (missing is null)
        {
            Log(template.Id, "requirements met (" + string.Join(", ", rows.Select(r => $"{r.Name} {r.Version}")) + ")");
            return new AcpRequirements(rows, [], null, null);
        }

        Log(template.Id, $"{missing.Name} is {(missing.State == AcpRequirementState.Missing ? "missing" : "too old")}");
        if (missing.Id is "node" or "npx")
        {
            var facts = await NodeFactsAsync(cancellationToken).ConfigureAwait(false);
            var (options, note) = AcpNodeInstall.Options(facts, template.NodeMajor);
            var title = template.NodeMajor is { } major
                ? Localizer.Format("msg.acp.connect.installNodeMajor", "Install Node.js {0} or later", major)
                : Localizer.Get("msg.acp.connect.installNode", "Install Node.js");
            return new AcpRequirements(rows, options, title, note);
        }

        var cliOptions = new List<AcpInstallOption>();
        foreach (var option in template.CliInstall(Platform))
        {
            cliOptions.Add(option.Id == "npm" && !OperatingSystem.IsWindows() && !await NpmPrefixWritableAsync(path, cancellationToken).ConfigureAwait(false)
                ? option with { Kind = InstallKind.Terminal, Command = "sudo " + option.Command, Program = "sudo", Arguments = ["npm", .. option.Arguments] }
                : option);
        }

        return new AcpRequirements(rows, cliOptions, Localizer.Format("msg.acp.connect.installCli", "Install {0}", template.Agent.Cli + (template.CliMinimum is { } min ? " " + min + "+" : "")), null);
    }

    /// <summary>A program on <paramref name="path"/> and its <c>--version</c>.</summary>
    private async Task<AcpRequirement> ProbeAsync(string program, string name, string? path, CancellationToken cancellationToken)
    {
        if (FindOnPath(program, path) is not { } found)
        {
            return new AcpRequirement(program, name, AcpRequirementState.Missing, null, null, null);
        }

        var (code, lines) = await RunAsync(Start(found, ["--version"], path), null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        var version = code == 0 ? lines.Select(l => VersionText().Match(l)).FirstOrDefault(m => m.Success)?.Value : null;
        return version is null
            ? new AcpRequirement(program, name, AcpRequirementState.Missing, null, found, null)
            : new AcpRequirement(program, name, AcpRequirementState.Found, version, found, null);
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionText();

    private static bool Older(string? version, string minimum) =>
        !System.Version.TryParse(version, out var have) || !System.Version.TryParse(minimum, out var need) || have < need;

    /// <summary>A program's file on <paramref name="path"/> (with PATHEXT on Windows; executable elsewhere), or null.</summary>
    public static string? FindOnPath(string program, string? path)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var folder in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, program + extension);
                if (AcpAgentCatalog.IsProgram(candidate))
                {
                    // Windows matches names in any case: the file's own name (node.cmd), not PATHEXT's (node.CMD), as the
                    // Connect dialog shows it.
                    return OperatingSystem.IsWindows() ? new DirectoryInfo(folder).EnumerateFiles(program + extension).FirstOrDefault()?.FullName ?? candidate : candidate;
                }
            }
        }

        return null;
    }

    /// <summary>What decides the Node.js install options: the OS's package managers, and the version apt or dnf would install.</summary>
    private async Task<AcpNodeFacts> NodeFactsAsync(CancellationToken cancellationToken)
    {
        var path = SearchPath ?? Environment.GetEnvironmentVariable("PATH");
        var ids = (Distribution ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? apt = null, dnf = null;
        if (Platform == PlatformKind.Linux && ids.Any(i => i is "debian" or "ubuntu") && FindOnPath("apt-cache", path) is { } aptCache)
        {
            var (code, lines) = await RunAsync(Start(aptCache, ["policy", "nodejs"], path), null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
            apt = code == 0 ? AcpNodeInstall.AptCandidate(string.Join('\n', lines)) : null;
        }

        if (Platform == PlatformKind.Linux && ids.Any(i => i is "fedora" or "rhel" or "centos") && FindOnPath("dnf", path) is { } dnfPath)
        {
            // From the metadata already on this machine (-C): no download to answer a dialog.
            var (code, lines) = await RunAsync(Start(dnfPath, ["-C", "info", "nodejs"], path), null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
            dnf = code == 0 ? AcpNodeInstall.DnfCandidate(string.Join('\n', lines)) : null;
        }

        return new AcpNodeFacts(
            Platform,
            Distribution,
            Platform == PlatformKind.Windows && FindOnPath("winget", path) is not null,
            Platform == PlatformKind.MacOS && FindOnPath("brew", path) is not null,
            Platform == PlatformKind.Linux && FindOnPath("snap", path) is not null,
            apt,
            dnf,
            CodalusNode is not null && Node.NodeRuntime.Platform is not null);
    }

    /// <summary>Whether npm's global folder (<c>npm config get prefix</c>) can be written without sudo (a distribution's Node.js: not).</summary>
    private async Task<bool> NpmPrefixWritableAsync(string? path, CancellationToken cancellationToken)
    {
        if (FindOnPath("npm", path) is not { } npm)
        {
            return true;
        }

        var (code, lines) = await RunAsync(Start(npm, ["config", "get", "prefix"], path), null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (code != 0 || lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() is not { Length: > 0 } prefix)
        {
            return true;
        }

        var folder = Path.Combine(prefix, "lib", "node_modules");
        try
        {
            var probe = Path.Combine(Directory.Exists(folder) ? folder : prefix, ".codalus-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>
    /// Installs with <paramref name="option"/>: a Run option here, its output to <paramref name="output"/>; a Terminal option
    /// in the IDE's terminal (sudo asks there), waiting for it to end; Codalus's own Node.js downloaded and verified. On
    /// Windows the PATH is read again from the registry afterwards. The exit code (null when it didn't run); the caller
    /// checks the requirements again.
    /// </summary>
    public async Task<int?> InstallAsync(string agent, AcpInstallOption option, IAcpConnectHost host, Action<string>? output, CancellationToken cancellationToken)
    {
        ThrowIfRemote();
        Log(agent, $"install {option.Id}: {option.Command}");
        int? exit;
        if (option.IsCodalusNode)
        {
            var runtime = CodalusNode ?? throw new AcpException("Codalus's own Node.js isn't available here.");
            await runtime.EnsureAsync(new Progress<string>(line => output?.Invoke(line)), cancellationToken).ConfigureAwait(false);
            exit = 0;
        }
        else if (option.Kind == InstallKind.OpenPage || option.Program is null)
        {
            return null;
        }
        else
        {
            var path = SearchPath ?? Environment.GetEnvironmentVariable("PATH");
            var program = Path.IsPathRooted(option.Program) ? option.Program : FindOnPath(option.Program, path) ?? option.Program;
            var launch = Start(program, option.Arguments, path) with { WorkingDirectory = Home };
            if (option.Kind == InstallKind.Terminal)
            {
                exit = await host.RunInTerminalAsync(option.Name, option.Command, launch, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (exit, _) = await RunAsync(launch, output, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            }
        }

        Log(agent, $"install {option.Id} → exit {exit?.ToString(CultureInfo.InvariantCulture) ?? "(didn't run)"}");
        if (option.ReadsWindowsPath && OperatingSystem.IsWindows())
        {
            var added = WindowsPath.Refresh(ReadWindowsPath);
            if (added.Count > 0)
            {
                Log(agent, "PATH read again from the registry: " + string.Join(';', added));
            }
        }

        return exit;
    }

    // ------------------------------------------------------------------ the test

    /// <summary>
    /// The connection test (ADR 0083), on the dialog's unsaved consent: the box is checked, or AI is on in the user's
    /// settings; nothing is written. Requirements, the package downloaded with npm's own progress, the agent started in
    /// an empty folder with no workspace, no MCP servers and every request refused, signed in the chosen way, then its
    /// sign-in checked as its template says. The agent is stopped at the end. AI turned off meanwhile (when the consent
    /// was the setting's) cancels it.
    /// </summary>
    public async Task<AcpTestResult> TestAsync(AcpConnectRequest request, IAcpConnectHost host, IProgress<AcpTestProgress>? progress, CancellationToken cancellationToken)
    {
        ThrowIfRemote();
        var fromSetting = ai.IsEnabled;
        if (!request.Consent && !fromSetting)
        {
            throw new AcpException(Localizer.Get("msg.acp.connect.needsConsent", "Check Turn on AI assistance first: external agents run only while it's on."));
        }

        var template = request.Template;
        var option = request.SignIn;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnAiChanged(object? sender, EventArgs e)
        {
            if (fromSetting && !ai.IsEnabled)
            {
                Log(template.Id, "AI assistance was turned off: the test stops");
                cancel.Cancel();
            }
        }

        ai.Changed += OnAiChanged;
        try
        {
            Log(template.Id, $"test {template.Tag}, sign-in {option.Id}");
            progress?.Report(new AcpTestProgress(AcpTestStep.Requirements, AcpStepState.Running, null));
            var requirements = await CheckAsync(template, request.UseCodalusNode, cancel.Token).ConfigureAwait(false);
            if (!requirements.Met)
            {
                var missing = requirements.Rows.First(r => r.State != AcpRequirementState.Found);
                progress?.Report(new AcpTestProgress(AcpTestStep.Requirements, AcpStepState.Failed, missing.Name));
                return new AcpTestResult(AcpTestOutcome.Failed, Localizer.Format("msg.acp.connect.requirementMissing", "{0} needs {1}, which isn't installed or is too old.", template.Name, missing.Name)) { FailedStep = AcpTestStep.Requirements };
            }

            progress?.Report(new AcpTestProgress(AcpTestStep.Requirements, AcpStepState.Done, string.Join(", ", requirements.Rows.Select(r => $"{r.Name} {r.Version}"))));
            var directory = agents.ConnectTestDirectory;
            Directory.CreateDirectory(directory);
            var path = TestPath(request);
            if (template.Package is { } package)
            {
                var download = await DownloadAsync(template.Id, package, requirements.Row("npx")!.Path!, path, directory, progress, cancel.Token).ConfigureAwait(false);
                if (download is not null)
                {
                    return download;
                }
            }
            else
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Skipped, null));
            }

            var command = template.Package is not null ? requirements.Row("npx")!.Path! : requirements.Row("cli")!.Path!;
            var config = new AcpAgentConfig(command, template.Agent.Args, path is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["PATH"] = path })
            {
                SecretEnv = option.Kind == AcpSignInKind.ApiKeyEnv ? [option.EnvVar!] : [],
                SecretValues = option.Kind == AcpSignInKind.ApiKeyEnv ? new Dictionary<string, string> { [option.EnvVar!] = request.ApiKey ?? "" } : new Dictionary<string, string>(),
                UnsetEnv = option.UnsetEnv,
            };
            return await RunTestAsync(template.Id, template.Name, config, template.SignInCheck, option, request.ApiKey, host, progress, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new AcpTestResult(AcpTestOutcome.Failed, Localizer.Get("msg.acp.connect.aiTurnedOff", "AI assistance was turned off, so the test stopped. Nothing was saved."));
        }
        catch (AcpException ex)
        {
            Log(template.Id, "test failed: " + ex.Message.Split('\n')[0]);
            return new AcpTestResult(AcpTestOutcome.Failed, ex.Message);
        }
        finally
        {
            ai.Changed -= OnAiChanged;
        }
    }

    /// <summary>
    /// Checks a configured agent (Manage Agents… → Check Connection): its entry as written, started in the test folder,
    /// its sign-in checked as its template says ("not confirmed" for a hand-written one). AI must be on.
    /// </summary>
    public async Task<AcpTestResult> CheckConnectionAsync(string name, IAcpConnectHost host, IProgress<AcpTestProgress>? progress, CancellationToken cancellationToken)
    {
        ThrowIfRemote();
        if (!ai.IsEnabled)
        {
            throw new AcpException(Localizer.Get("msg.acp.connect.turnOnFirst", "Turn on AI assistance first: external agents run only while it's on."));
        }

        if (!agents.Configured.TryGetValue(name, out var entry))
        {
            throw new AcpException(agents.Problems.FirstOrDefault(p => p.StartsWith($"ai.agents.{name}:", StringComparison.Ordinal)) ?? $"There is no agent '{name}' in your settings (ai.agents).");
        }

        await agents.StopAgentAsync(name).ConfigureAwait(false);
        var config = await agents.ResolveAsync(name, entry, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(agents.ConnectTestDirectory);
        progress?.Report(new AcpTestProgress(AcpTestStep.Requirements, AcpStepState.Skipped, null));
        progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Skipped, null));
        var template = AcpAgentTemplates.FromTag(config.Template);
        var already = new AcpSignInOption("already", AcpSignInKind.AlreadySignedIn, "", "");
        try
        {
            return await RunTestAsync(name, template?.Name ?? name, config, template?.SignInCheck ?? AcpSignInCheck.Unknown, already, null, host, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (AcpException ex)
        {
            return new AcpTestResult(AcpTestOutcome.Failed, ex.Message);
        }
    }

    /// <summary>The PATH the test runs with: the stand-ins' or Codalus's Node.js first, else null (this process's).</summary>
    private string? TestPath(AcpConnectRequest request)
    {
        var nodeFolder = request.UseCodalusNode && CodalusNode is { IsInstalled: true } runtime ? Path.GetDirectoryName(runtime.NodePath) : null;
        var path = SearchPath;
        if (nodeFolder is not null)
        {
            path = nodeFolder + Path.PathSeparator + (path ?? Environment.GetEnvironmentVariable("PATH"));
        }
        else if (path is not null)
        {
            path += Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        return path;
    }

    /// <summary>
    /// The download step: <c>npx --yes --package=&lt;pkg&gt;@&lt;ver&gt; --loglevel=http -- node -e 0</c> in the test folder, so the
    /// package is in npm's cache before the agent's 60 s start, and npm's own lines show the progress; null when it's in.
    /// </summary>
    private async Task<AcpTestResult?> DownloadAsync(string agent, string package, string npx, string? path, string directory, IProgress<AcpTestProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Running, package));
        Log(agent, "download " + package);
        var launch = Start(npx, ["--yes", "--package=" + package, "--loglevel=http", "--", "node", "-e", "0"], path) with { WorkingDirectory = directory };
        if (path is not null)
        {
            launch = launch with { Environment = new Dictionary<string, string> { ["PATH"] = path } };
        }

        var (code, lines) = await RunAsync(launch, line =>
        {
            if (line.Contains("http fetch", StringComparison.Ordinal))
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Running, line.Trim()));
            }
        }, DownloadTimeout, cancellationToken).ConfigureAwait(false);
        if (code == 0)
        {
            progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Done, package));
            Log(agent, "download → in npm's cache");
            return null;
        }

        var message = NpmError(package, lines);
        Log(agent, "download failed: " + message);
        progress?.Report(new AcpTestProgress(AcpTestStep.Download, AcpStepState.Failed, message));
        return new AcpTestResult(AcpTestOutcome.Failed, message)
        {
            FailedStep = AcpTestStep.Download,
            Output = [.. lines.Where(l => l.Contains("npm error", StringComparison.OrdinalIgnoreCase) || l.Contains("ERR!", StringComparison.Ordinal) || l.Contains("EBADENGINE", StringComparison.Ordinal)).TakeLast(8)],
        };
    }

    /// <summary>npm's failure in plain words: a package that isn't there, the network, a proxy or certificate, the Node.js version.</summary>
    public static string NpmError(string package, IReadOnlyList<string> lines)
    {
        var text = string.Join('\n', lines);
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("E404") || Has("404 Not Found"))
        {
            return Localizer.Format("msg.acp.connect.npm404", "npm says {0} isn't in its registry. Nothing was saved.", package);
        }

        if (Has("SELF_SIGNED_CERT") || Has("UNABLE_TO_GET_ISSUER_CERT") || Has("UNABLE_TO_VERIFY_LEAF_SIGNATURE") || Has("CERT_"))
        {
            return Localizer.Get("msg.acp.connect.npmCertificate", "npm doesn't trust the certificate of the server it reached: behind a proxy that inspects traffic, npm needs its certificate (npm config set cafile). Nothing was saved.");
        }

        if (Has("ETIMEDOUT") || Has("ECONNREFUSED") || Has("ENOTFOUND") || Has("EAI_AGAIN") || Has("ECONNRESET") || Has("network request"))
        {
            return Localizer.Get("msg.acp.connect.npmNetwork", "npm couldn't reach its registry. Behind a proxy, npm needs it in its own settings (npm config set proxy). Nothing was saved.");
        }

        if (Has("EPROXY") || Has("407"))
        {
            return Localizer.Get("msg.acp.connect.npmProxy", "npm's proxy refused the download (npm config get proxy shows it). Nothing was saved.");
        }

        if (Has("EBADENGINE"))
        {
            return Localizer.Get("msg.acp.connect.npmEngine", "npm says this Node.js is too old for the package. Install a newer Node.js, then try again. Nothing was saved.");
        }

        return Localizer.Format("msg.acp.connect.npmFailed", "npm couldn't download {0}. Nothing was saved.", package);
    }

    /// <summary>Start, sign in, check: the steps every test shares. The agent is stopped at the end, whatever happens.</summary>
    private async Task<AcpTestResult> RunTestAsync(string id, string name, AcpAgentConfig config, AcpSignInCheck check, AcpSignInOption option, string? apiKey, IAcpConnectHost host, IProgress<AcpTestProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = agents.ConnectTestDirectory;
        var handler = new RefusingClientHandler(line => Log(id, line));
        AcpClient? client = null;
        try
        {
            progress?.Report(new AcpTestProgress(AcpTestStep.Start, AcpStepState.Running, null));
            try
            {
                client = await AcpClient.StartAsync(id, config, directory, handler, StartTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (AcpException ex)
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.Start, AcpStepState.Failed, ex.Message));
                Log(id, "Start failed: " + ex.Message.Split('\n')[0]);
                return new AcpTestResult(AcpTestOutcome.Failed, ex.Message) { FailedStep = AcpTestStep.Start };
            }

            var info = client.Info!;
            var canLogout = client.CanLogout;
            var agent = $"{info.AgentInfo?.Name ?? id} {info.AgentInfo?.Version}".Trim();
            var offered = (info.AuthMethods ?? []).Select(m => m.Name).ToList();
            Log(id, string.Create(CultureInfo.InvariantCulture, $"started: {agent}, protocol {info.ProtocolVersion}, {offered.Count} sign-in method(s){(client.CanLogout ? ", logout" : "")}{(client.ReportsAuthStatus ? ", reports who is signed in" : "")}"));
            progress?.Report(new AcpTestProgress(AcpTestStep.Start, AcpStepState.Done, $"{agent} · ACP v{info.ProtocolVersion}" + (offered.Count > 0 ? " · " + Localizer.Format("msg.acp.connect.offers", "offers {0}", string.Join(", ", offered)) : "")));
            AcpTestResult Result(AcpTestOutcome outcome, string message) => new(outcome, message) { Agent = agent, Protocol = info.ProtocolVersion, Offered = offered, CanLogout = canLogout };
            AcpTestResult Fail(AcpTestStep step, string message)
            {
                progress?.Report(new AcpTestProgress(step, AcpStepState.Failed, message));
                Log(id, $"{step} failed: {message}");
                return Result(AcpTestOutcome.Failed, message) with { FailedStep = step };
            }

            // Sign in.
            progress?.Report(new AcpTestProgress(AcpTestStep.SignIn, option.Kind == AcpSignInKind.AlreadySignedIn ? AcpStepState.Skipped : AcpStepState.Running, option.Name));
            switch (option.Kind)
            {
                case AcpSignInKind.TerminalMethod:
                {
                    var methods = info.AuthMethods ?? [];
                    var method = methods.FirstOrDefault(m => m.IsTerminal && m.Id == option.MethodId) ?? methods.FirstOrDefault(m => m.IsTerminal && m.Id == option.RemoteMethodId);
                    if (method is null)
                    {
                        return Fail(AcpTestStep.SignIn, Localizer.Format("msg.acp.connect.methodNotOffered", "{0} doesn't offer that sign-in here. It offers: {1}.", name, offered.Count > 0 ? string.Join(", ", offered) : Localizer.Get("msg.acp.connect.nothing", "nothing")));
                    }

                    // The command the dialog showed is the one that runs: an agent that changed it doesn't run it.
                    var shown = method.Id == option.MethodId ? option.TerminalArgs : option.RemoteTerminalArgs;
                    if (shown.Count > 0 && !(method.Args ?? []).SequenceEqual(shown, StringComparer.Ordinal))
                    {
                        return Fail(AcpTestStep.SignIn, Localizer.Format("msg.acp.connect.signInChanged", "{0} offers a different sign-in command ({1}) than Codalus showed, so it didn't run. Nothing was saved.", name, string.Join(' ', method.Args ?? [])));
                    }

                    var (_, signIn) = AcpAgentSession.TerminalSignIn(name, config, method, directory);
                    Log(id, "terminal sign-in " + method.Id);
                    var exit = await host.RunInTerminalAsync(Localizer.Format("msg.acp.connect.signInTitle", "{0} sign-in", name), signIn.CommandLine, signIn.Launch, signIn.Hint, cancellationToken).ConfigureAwait(false);
                    Log(id, $"terminal sign-in {method.Id} → exit {exit?.ToString(CultureInfo.InvariantCulture) ?? "(didn't run)"}");
                    if (exit is null)
                    {
                        return Fail(AcpTestStep.SignIn, Localizer.Get("msg.acp.connect.signInDidntRun", "The sign-in didn't run. Nothing was saved."));
                    }

                    if (exit != 0 && !client.ReportsAuthStatus)
                    {
                        return Result(AcpTestOutcome.NeedsSignIn, Localizer.Format("msg.acp.connect.signInExit", "The sign-in ended with exit code {0}, so {1} isn't signed in. Nothing was saved.", exit, name)) with { FailedStep = AcpTestStep.SignIn };
                    }

                    // The protocol's "reconnect and reinitialize": a new process reads the new sign-in.
                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    client = await AcpClient.StartAsync(id, config, directory, handler, StartTimeout, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case AcpSignInKind.TerminalCommand:
                {
                    var cli = config with { Args = option.CommandArgs, Env = config.Env };
                    var launch = AcpClient.Launch(cli, directory);
                    var exit = await host.RunInTerminalAsync(Localizer.Format("msg.acp.connect.signInTitle", "{0} sign-in", name), cli.ToString(), launch, null, cancellationToken).ConfigureAwait(false);
                    Log(id, $"sign-in command → exit {exit?.ToString(CultureInfo.InvariantCulture) ?? "(didn't run)"}");
                    if (exit is not 0)
                    {
                        return Result(AcpTestOutcome.NeedsSignIn, Localizer.Format("msg.acp.connect.signInCommandFailed", "{0}'s sign-in didn't finish, so it isn't signed in. Nothing was saved.", name)) with { FailedStep = AcpTestStep.SignIn };
                    }

                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    client = await AcpClient.StartAsync(id, config, directory, handler, StartTimeout, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case AcpSignInKind.AgentMethod:
                case AcpSignInKind.ApiKeyOnce:
                case AcpSignInKind.ApiKeyEnv when option.MethodId is not null:
                {
                    if ((info.AuthMethods ?? []).All(m => m.Id != option.MethodId))
                    {
                        return Fail(AcpTestStep.SignIn, Localizer.Format("msg.acp.connect.methodNotOffered", "{0} doesn't offer that sign-in here. It offers: {1}.", name, offered.Count > 0 ? string.Join(", ", offered) : Localizer.Get("msg.acp.connect.nothing", "nothing")));
                    }

                    Log(id, "authenticate " + option.MethodId);
                    try
                    {
                        // Codex's key goes once in _meta["api-key"], never in its environment; Codex keeps it itself.
                        JsonElement? key = option.Kind == AcpSignInKind.ApiKeyOnce ? JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["apiKey"] = apiKey ?? "" }, AcpConnectJson.Default.DictionaryStringString) : null;
                        await client.AuthenticateAsync(option.MethodId!, cancellationToken, key).ConfigureAwait(false);
                    }
                    catch (AcpException ex)
                    {
                        return Result(AcpTestOutcome.NeedsSignIn, Localizer.Format("msg.acp.connect.authenticateFailed", "{0} didn't sign in: {1} Nothing was saved.", name, SecretRedactor.Redact(ex.Message, apiKey is null ? [] : [apiKey]).Text)) with { FailedStep = AcpTestStep.SignIn };
                    }

                    break;
                }
            }

            if (option.Kind != AcpSignInKind.AlreadySignedIn)
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.SignIn, AcpStepState.Done, option.Name));
            }

            // Check: a session in the empty folder, no MCP servers; then what the template trusts.
            progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Running, null));
            AcpNewSessionResponse session;
            try
            {
                session = await client.NewSessionAsync(new AcpNewSessionRequest(directory, []), cancellationToken).ConfigureAwait(false);
            }
            catch (AcpAuthRequiredException)
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Failed, null));
                Log(id, "session/new → sign-in needed");
                return Result(AcpTestOutcome.NeedsSignIn, Localizer.Format("msg.acp.connect.needsSignIn", "{0} says you aren't signed in. Nothing was saved.", name)) with { FailedStep = AcpTestStep.Check };
            }
            catch (AcpException ex)
            {
                return Fail(AcpTestStep.Check, ex.Message);
            }

            var modes = session.Modes?.AvailableModes ?? [];
            var current = modes.FirstOrDefault(m => m.Id == session.Modes?.CurrentModeId);
            IReadOnlyList<string> modeNames = [.. (current is null ? [] : new[] { current.Name }).Concat(modes.Where(m => m != current).Select(m => m.Name))];
            Log(id, string.Create(CultureInfo.InvariantCulture, $"session/new → created ({modes.Count} mode(s))"));
            switch (check)
            {
                case AcpSignInCheck.SessionNew:
                    Done(Localizer.Get("msg.acp.connect.sessionCreated", "Session created"));
                    return Result(AcpTestOutcome.Connected, Localizer.Format("msg.acp.connect.connected", "{0} is connected.", name)) with { Modes = modeNames };

                case AcpSignInCheck.AuthStatusPush:
                    var status = await client.WaitForAuthStatusAsync(AuthStatusWait, cancellationToken).ConfigureAwait(false);
                    Log(id, "signed in: " + (status?.Kind ?? "not reported"));
                    if (status is { IsSignedOut: true })
                    {
                        progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Failed, status.Label));
                        return Result(AcpTestOutcome.NeedsSignIn, Localizer.Format("msg.acp.connect.reportsSignedOut", "{0} says you aren't signed in ({1}). Nothing was saved.", name, status.Label ?? "Not logged in")) with { FailedStep = AcpTestStep.Check, SignedInAs = status.Label, Modes = modeNames };
                    }

                    if (status is not null)
                    {
                        var who = string.Join(" · ", new[] { status.Label, status.Detail }.Where(s => s is { Length: > 0 }));
                        Done(Localizer.Format("msg.acp.connect.reports", "{0} reports: {1}", name, who));
                        return Result(AcpTestOutcome.Connected, Localizer.Format("msg.acp.connect.connected", "{0} is connected.", name)) with { Modes = modeNames, SignedInAs = who };
                    }

                    progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Failed, Localizer.Get("msg.acp.connect.notReported", "Not reported")));
                    return Result(AcpTestOutcome.NotConfirmed, Localizer.Format("msg.acp.connect.notConfirmedStatus", "{0} started, but it didn't say whether you're signed in within {1} seconds: its own check may be slow, or offline. If you aren't signed in, your first question asks you to.", name, (int)AuthStatusWait.TotalSeconds)) with { Modes = modeNames };

                default:
                    progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Failed, Localizer.Get("msg.acp.connect.notReported", "Not reported")));
                    return Result(AcpTestOutcome.NotConfirmed, Localizer.Format("msg.acp.connect.notConfirmed", "{0} started and created a session, but it doesn't say whether you're signed in. If you aren't, your first question asks you to.", name)) with { Modes = modeNames };
            }

            void Done(string how)
            {
                progress?.Report(new AcpTestProgress(AcpTestStep.Check, AcpStepState.Done, how));
                progress?.Report(new AcpTestProgress(AcpTestStep.Done, AcpStepState.Done, null));
            }
        }
        finally
        {
            if (client is not null)
            {
                // Closing stdin ends the agent (ACP has no shutdown).
                await client.DisposeAsync().ConfigureAwait(false);
                Log(id, "test agent stopped");
            }
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes the agent after a passing test (or Save Anyway when its sign-in wasn't confirmed, D43): its key to the
    /// credential store first, then its <c>ai.agents</c> entry (one member, the others and the comments kept), then
    /// <c>ai.enabled</c> when the dialog's box turned AI on. The user's settings only. Its running chat stops first.
    /// </summary>
    public async Task SaveAsync(AcpConnectRequest request, CancellationToken cancellationToken)
    {
        ThrowIfRemote();
        var template = request.Template;
        var option = request.SignIn;
        await agents.StopAgentAsync(template.Id).ConfigureAwait(false);
        IReadOnlyList<string> secretEnv = option.Kind == AcpSignInKind.ApiKeyEnv ? [option.EnvVar!] : [];
        var previous = agents.Configured.TryGetValue(template.Id, out var old) ? old.SecretEnv : [];
        if (secretEnv.Count > 0)
        {
            var secrets = agents.Secrets ?? throw new AcpException("This window has no credential store for the key.");
            await secrets.StoreAsync(template.Id, option.EnvVar!, request.ApiKey ?? throw new AcpException("Type the key first."), cancellationToken).ConfigureAwait(false);
        }

        var entry = AcpAgentCatalog.Entry(template.Agent.Command, template.Agent.Args, secretEnv, option.UnsetEnv, template.Tag, request.UseCodalusNode ? "builtIn" : null);
        try
        {
            await settings.SetMemberAsync(AcpAgents.AgentsSetting, template.Id, entry, SettingScope.User, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (secretEnv.Count > 0 && agents.Secrets is { } secrets)
            {
                await secrets.DeleteAllAsync(template.Id, secretEnv, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // A key the new entry doesn't use goes (reconnected with a subscription after a key).
        if (previous.Except(secretEnv, StringComparer.Ordinal).ToList() is { Count: > 0 } stale && agents.Secrets is { } store)
        {
            await store.DeleteAllAsync(template.Id, stale, cancellationToken).ConfigureAwait(false);
        }

        Log(template.Id, $"wrote ai.agents.{template.Id} ({template.Tag}{(secretEnv.Count > 0 ? ", secretEnv " + string.Join(", ", secretEnv) : "")}) to the user settings");
        if (request.Consent && !ai.IsEnabled && !ai.IsOffForWorkspace)
        {
            await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, cancellationToken).ConfigureAwait(false);
            Log(template.Id, "wrote ai.enabled to the user settings");
        }
    }

    /// <summary>
    /// Disconnect (D43): stops the agent's chat; with <paramref name="signOut"/>, starts it once in the test folder and
    /// sends <c>logout</c> when it advertises it; then removes its entry (one member) and its keys. Never changes
    /// <c>ai.enabled</c>, never touches npm's cache. A sign-out that fails is returned, and the rest goes on.
    /// </summary>
    public async Task<string?> DisconnectAsync(string name, bool signOut, CancellationToken cancellationToken)
    {
        await agents.StopAgentAsync(name).ConfigureAwait(false);
        var configured = agents.Configured.TryGetValue(name, out var entry) ? entry : null;
        string? problem = null;
        if (signOut && configured is not null)
        {
            ThrowIfRemote();
            AcpClient? client = null;
            try
            {
                var config = await agents.ResolveAsync(name, configured, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(agents.ConnectTestDirectory);
                client = await AcpClient.StartAsync(name, config, agents.ConnectTestDirectory, new RefusingClientHandler(line => Log(name, line)), StartTimeout, cancellationToken).ConfigureAwait(false);
                if (client.CanLogout)
                {
                    await client.LogoutAsync(cancellationToken).ConfigureAwait(false);
                    Log(name, "logout → signed out");
                }
                else
                {
                    problem = Localizer.Format("msg.acp.connect.cantSignOut", "{0} can't sign out from Codalus IDE; sign out with its own command.", name);
                }
            }
            catch (AcpException ex)
            {
                problem = Localizer.Format("msg.acp.connect.signOutFailed", "{0} didn't sign out: {1}", name, ex.Message);
                Log(name, "logout failed");
            }
            finally
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        await settings.SetMemberAsync(AcpAgents.AgentsSetting, name, null, SettingScope.User, cancellationToken).ConfigureAwait(false);
        var names = (configured?.SecretEnv ?? []).Concat(AcpAgentTemplates.Find(name)?.SignIn.Where(o => o.EnvVar is not null).Select(o => o.EnvVar!) ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > 0 && agents.Secrets is { } secrets)
        {
            await secrets.DeleteAllAsync(name, names, cancellationToken).ConfigureAwait(false);
        }

        Log(name, $"removed ai.agents.{name} from the user settings");
        return problem;
    }

    // ------------------------------------------------------------------ processes

    /// <summary>How <paramref name="program"/> starts: on Windows a script through <see cref="WindowsCommand"/>.</summary>
    private static ProcessLaunchInfo Start(string program, IReadOnlyList<string> arguments, string? path) =>
        OperatingSystem.IsWindows() ? WindowsCommand.Launch(program, arguments, path) : new ProcessLaunchInfo(program, arguments);

    /// <summary>Runs a program to its end within <paramref name="limit"/>, each line of its output and errors to <paramref name="line"/>; its exit code and lines.</summary>
    private static async Task<(int? Exit, IReadOnlyList<string> Lines)> RunAsync(ProcessLaunchInfo launch, Action<string>? line, TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(launch.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (launch.WorkingDirectory is { } directory)
        {
            start.WorkingDirectory = directory;
        }

        foreach (var (key, value) in launch.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        var lines = new List<string>();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException(launch.FileName + " didn't start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, [ex.Message]);
        }

        using (process)
        {
            process.StandardInput.Close();
            void Read(object? sender, DataReceivedEventArgs e)
            {
                if (e.Data is { } text)
                {
                    lock (lines)
                    {
                        lines.Add(text);
                    }

                    line?.Invoke(text);
                }
            }

            process.OutputDataReceived += Read;
            process.ErrorDataReceived += Read;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                return (null, [.. lines, $"{Path.GetFileName(launch.FileName)} didn't end within {limit.TotalSeconds:0} seconds."]);
            }

            // The output's end comes in moments after the exit.
            process.WaitForExit(2000);
            lock (lines)
            {
                return (process.ExitCode, [.. lines]);
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AcpConnectJson : System.Text.Json.Serialization.JsonSerializerContext;
