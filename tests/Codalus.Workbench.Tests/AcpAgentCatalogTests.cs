using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The known ACP agents behind AI Chat's "Add agent…" (M48, ADR 0079): every entry pinned (no unpinned package, no
/// <c>@latest</c>) and in step with the user guide; "installed" from a stand-in CLI on a test PATH, found by looking for
/// files only; adding writes exactly that entry to the user's settings and never the workspace's, once.
/// </summary>
public sealed partial class AcpAgentCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-acp-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspaceFile;
    private readonly WorkbenchServices _workbench;

    public AcpAgentCatalogTests()
    {
        _workspaceFile = Path.Combine(_root, "ws", ".codalus", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_workspaceFile)!);
        File.WriteAllText(_workspaceFile, "{}");
        _workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user")), PlatformInfo.Current, _workspaceFile, watchFiles: false);
        _workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
    }

    public void Dispose()
    {
        _workbench.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactVersion();

    [Fact]
    public void Every_entry_is_well_formed_and_pinned_and_the_user_guide_has_the_same_versions()
    {
        var agents = AcpAgentCatalog.Agents;
        Assert.Equal(["pi", "claude", "codex", "gemini", "copilot", "opencode", "goose"], agents.Select(a => a.Id));
        Assert.Equal(agents.Count, agents.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
        var guide = File.ReadAllText(Path.Combine(DataHostTests.Metadata("CodalusRepoRoot"), "docs", "user-guide", "ai.md"));
        foreach (var agent in agents)
        {
            Assert.Matches(ExactVersion(), agent.Version);
            Assert.False(string.IsNullOrWhiteSpace(agent.Name));
            Assert.False(string.IsNullOrWhiteSpace(agent.Description));
            Assert.DoesNotContain(agent.Args, a => a.Contains("latest", StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith(agent.Command + " ", agent.CommandLine, StringComparison.Ordinal);
            if (agent.UsesNpx)
            {
                // npx -y <package>@<exact version>: npx never resolves a range or a tag at the user's first chat.
                Assert.Equal("npx", agent.Command);
                Assert.Equal("-y", agent.Args[0]);
                Assert.Equal(agent.Package, agent.Args[1]);
                Assert.EndsWith("@" + agent.Version, agent.Package, StringComparison.Ordinal);
                Assert.Contains(agent.Package!, guide, StringComparison.Ordinal);
            }
            else
            {
                // An agent the registry ships as a binary runs its installed CLI: Codalus downloads nothing.
                Assert.Equal(agent.Cli, agent.Command);
                Assert.DoesNotContain(agent.Args, a => a.Contains('@', StringComparison.Ordinal) || a.Contains("://", StringComparison.Ordinal));
                Assert.Contains("`" + agent.CommandLine + "`", guide, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task An_agent_is_installed_when_its_cli_is_on_the_path_and_the_lookup_reads_files_only()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var gemini = Path.Combine(bin, OperatingSystem.IsWindows() ? "gemini.cmd" : "gemini");
        File.WriteAllText(gemini, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gemini, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // A file that isn't executable isn't a program.
            File.WriteAllText(Path.Combine(bin, "claude"), "not a program\n");
        }

        var missing = Path.Combine(_root, "no-such-folder");
        var found = await AcpAgentCatalog.FindInstalledAsync(AcpAgentCatalog.Agents, missing + Path.PathSeparator + bin, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(["gemini"], found);

        // The stand-in would fail if it ran (exit 1); nothing ran it.
        Assert.Empty(await AcpAgentCatalog.FindInstalledAsync(AcpAgentCatalog.Agents, missing, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Adding_writes_exactly_that_entry_to_the_user_settings_once_and_never_the_workspace()
    {
        var settings = _workbench.Settings;
        using (var mine = JsonDocument.Parse("""{ "mine": { "command": "my-agent", "args": ["--acp"] } }"""))
        {
            await settings.SetAsync(AcpAgents.AgentsSetting, mine.RootElement.Clone(), SettingScope.User, CancellationToken.None);
        }

        var claude = AcpAgentCatalog.Agents.Single(a => a.Id == "claude");
        Assert.True(await AcpAgentCatalog.AddAsync(settings, claude, CancellationToken.None));

        var user = settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value;
        Assert.Equal(["mine", "claude"], user.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            """{"command":"npx","args":["-y","@agentclientprotocol/claude-agent-acp@0.94.0"]}""",
            user.GetProperty("claude").GetRawText().Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal));
        var configured = AcpAgents.Parse(user);
        Assert.Equal("npx", configured["claude"].Command);
        Assert.Equal(["-y", "@agentclientprotocol/claude-agent-acp@0.94.0"], configured["claude"].Args);
        Assert.Null(settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.Workspace));
        Assert.Equal("{}", File.ReadAllText(_workspaceFile));

        // Already there: not added again, and nothing written.
        Assert.False(await AcpAgentCatalog.AddAsync(settings, claude, CancellationToken.None));
        Assert.Equal(2, settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.EnumerateObject().Count());
    }

    [Fact]
    public void Every_template_is_pinned_says_how_its_sign_in_is_checked_and_offers_its_agents_own_methods()
    {
        var recordings = Path.Combine(DataHostTests.Metadata("CodalusRepoRoot"), "tests", "Shared", "AcpRecordings");
        Assert.Equal(["claude", "codex", "gemini", "copilot", "pi", "opencode", "goose"], AcpAgentTemplates.All.Select(t => t.Id));
        Assert.Equal(["claude", "codex", "gemini", "copilot"], AcpAgentTemplates.All.Where(t => t.Tier == AcpTemplateTier.OneClick).Select(t => t.Id));
        Assert.Equal("Claude", AcpAgentTemplates.Find("claude")!.Name); // never "Claude Code" as the agent's name (D43)
        foreach (var template in AcpAgentTemplates.All)
        {
            Assert.Same(AcpAgentCatalog.Agents.Single(a => a.Id == template.Id), template.Agent);
            Assert.Equal(template.Id + "@" + template.Version, template.Tag);
            Assert.Same(template, AcpAgentTemplates.FromTag(template.Tag));
            Assert.True(Enum.IsDefined(template.SignInCheck));
            Assert.Contains(template.SignIn, o => o.Kind == AcpSignInKind.AlreadySignedIn);
            Assert.Equal(template.SignIn.Count, template.SignIn.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.StartsWith("https://", template.DocsUrl, StringComparison.Ordinal);
            foreach (var option in template.SignIn)
            {
                if (option.EnvVar is { } variable)
                {
                    Assert.True(AcpAgents.IsVariableName(variable), variable);
                    Assert.Null(AcpTerminals.RefusedVariable([variable]));
                }

                Assert.Equal(option.Kind == AcpSignInKind.ApiKeyEnv, option.EnvVar is not null);
                Assert.Equal(option.TakesKey, option.KeyName is not null);
            }

            // The methods it names are the pinned agent's own (its recorded initialize).
            var named = template.SignIn.Where(o => o.Kind is AcpSignInKind.AgentMethod or AcpSignInKind.TerminalMethod or AcpSignInKind.ApiKeyOnce || o.MethodId is not null).ToList();
            if (named.Count == 0)
            {
                continue;
            }

            var recording = Path.Combine(recordings, $"{template.Id}-{template.Version}-initialize.json");
            Assert.True(File.Exists(recording), $"{template.Id} names sign-in methods but has no recording at {recording}");
            using var document = JsonDocument.Parse(File.ReadAllText(recording));
            var offered = document.RootElement.GetProperty("authMethods").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!, m => m.TryGetProperty("type", out var type) ? type.GetString() : null);
            foreach (var option in named)
            {
                Assert.True(offered.ContainsKey(option.MethodId!), $"{template.Id}: {option.MethodId} isn't in its recorded initialize");
                Assert.Equal(option.Kind == AcpSignInKind.TerminalMethod ? "terminal" : null, offered[option.MethodId!]);
                if (option.Kind == AcpSignInKind.TerminalMethod)
                {
                    var args = document.RootElement.GetProperty("authMethods").EnumerateArray().Single(m => m.GetProperty("id").GetString() == option.MethodId).GetProperty("args").EnumerateArray().Select(a => a.GetString());
                    Assert.Equal(option.TerminalArgs, args);
                }
            }
        }

        Assert.Equal(["oauth-personal", "gemini-api-key"], AcpAgentTemplates.Find("gemini")!.SignIn.Where(o => o.MethodId is not null).Select(o => o.MethodId));
        var claude = AcpAgentTemplates.Find("claude")!;
        Assert.Equal(AcpSignInCheck.AuthStatusPush, claude.SignInCheck);
        Assert.Equal(["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"], claude.SignIn.Single(o => o.Id == "subscription").UnsetEnv);
        Assert.Equal("claude-login", claude.SignIn.Single(o => o.Id == "subscription").RemoteMethodId);
        using var ssh = JsonDocument.Parse(File.ReadAllText(Path.Combine(recordings, $"claude-{claude.Version}-initialize-ssh.json")));
        Assert.Equal(["claude-login"], ssh.RootElement.GetProperty("authMethods").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal(AcpSignInCheck.SessionNew, AcpAgentTemplates.Find("codex")!.SignInCheck);
        Assert.Equal(AcpSignInCheck.SessionNew, AcpAgentTemplates.Find("gemini")!.SignInCheck);
        Assert.Equal(["copilot", "pi", "opencode", "goose"], AcpAgentTemplates.All.Where(t => t.SignInCheck == AcpSignInCheck.Unknown).Select(t => t.Id));
        Assert.Equal(AcpSignInKind.ApiKeyOnce, AcpAgentTemplates.Find("codex")!.SignIn.Single(o => o.Id == "apikey").Kind); // Codex keeps its key itself
    }

    [Theory]
    [InlineData(PlatformKind.Windows)]
    [InlineData(PlatformKind.MacOS)]
    [InlineData(PlatformKind.Linux)]
    public void Install_commands_are_pinned_and_use_only_the_users_package_managers(PlatformKind platform)
    {
        string[] allowed = ["winget", "brew", "sudo apt-get", "sudo dnf", "sudo snap", "npm", "scoop", "choco"];
        var facts = new AcpNodeFacts(platform, "ubuntu debian", true, true, true, 22, 22, true);
        var options = AcpAgentTemplates.All.SelectMany(t => t.CliInstall(platform)).Concat(AcpNodeInstall.Options(facts, 22).Options);
        foreach (var option in options)
        {
            Assert.DoesNotContain("curl", option.Command, StringComparison.Ordinal);
            Assert.DoesNotContain("wget", option.Command, StringComparison.Ordinal);
            if (option.Kind == Setup.InstallKind.OpenPage || option.IsCodalusNode)
            {
                Assert.StartsWith("https://", option.Command, StringComparison.Ordinal);
                continue;
            }

            Assert.Contains(allowed, a => option.Command.StartsWith(a + " ", StringComparison.Ordinal));
            if (option.Command.StartsWith("npm install", StringComparison.Ordinal))
            {
                Assert.Matches(@"@\d+\.\d+\.\d+$", option.Command); // an exact version, never a range or a tag
            }
        }
    }

    [Fact]
    public void Node_is_offered_from_a_package_manager_only_when_it_has_a_new_enough_one_and_codalus_own_only_where_none_can()
    {
        static string[] Ids(AcpNodeFacts facts, int? major) => [.. AcpNodeInstall.Options(facts, major).Options.Select(o => o.Id)];
        Assert.Equal(["winget", "page"], Ids(new(PlatformKind.Windows, null, true, false, false, null, null, true), 22));
        Assert.Equal(["page", "codalus-node"], Ids(new(PlatformKind.Windows, null, false, false, false, null, null, true), 22));
        Assert.Equal(["brew", "page"], Ids(new(PlatformKind.MacOS, null, false, true, false, null, null, true), 22));
        Assert.Equal(["page", "codalus-node"], Ids(new(PlatformKind.MacOS, null, false, false, false, null, null, true), 22));
        Assert.Equal(["page"], Ids(new(PlatformKind.MacOS, null, false, false, false, null, null, false), 22));

        // Ubuntu 24.04's Node.js is 18: apt isn't offered for Claude (22), snap is.
        var (ubuntu, note) = AcpNodeInstall.Options(new(PlatformKind.Linux, "ubuntu debian", false, false, true, 18, null, true), 22);
        Assert.Equal(["snap", "page"], ubuntu.Select(o => o.Id));
        Assert.Equal("sudo snap install node --classic --channel=24/stable", ubuntu[0].Command);
        Assert.Equal(Setup.InstallKind.Terminal, ubuntu[0].Kind);
        Assert.Equal("apt isn't offered: the distribution's nodejs package is 18, and 22 or later is needed.", note);
        Assert.Equal(["apt", "page"], Ids(new(PlatformKind.Linux, "debian", false, false, false, 22, null, true), 22));
        Assert.Equal("sudo apt-get install -y nodejs npm", AcpNodeInstall.Options(new(PlatformKind.Linux, "debian", false, false, false, 22, null, true), 22).Options[0].Command);
        Assert.Equal(["page", "codalus-node"], Ids(new(PlatformKind.Linux, "debian", false, false, false, 20, null, true), 22));
        Assert.Equal(["dnf", "page"], Ids(new(PlatformKind.Linux, "fedora", false, false, false, null, 22, true), 22));
        Assert.True(AcpNodeInstall.Options(new(PlatformKind.Windows, null, true, false, false, null, null, true), 22).Options[0].ReadsWindowsPath);

        Assert.Equal(18, AcpNodeInstall.AptCandidate("nodejs:\n  Installed: (none)\n  Candidate: 18.19.1+dfsg-6ubuntu5\n  Version table:\n"));
        Assert.Null(AcpNodeInstall.AptCandidate("nodejs:\n  Installed: (none)\n  Candidate: (none)\n"));
        Assert.Equal(20, AcpNodeInstall.DnfCandidate("Name         : nodejs\nEpoch        : 1\nVersion      : 20.19.2\n"));
        Assert.Equal(22, AcpNodeInstall.Major("v22.11.0"));
        Assert.Equal(20, AcpNodeInstall.Major("1:20.19.2-1"));
    }

    [Fact]
    public void The_windows_path_read_again_adds_only_new_folders_at_its_end()
    {
        // The merge's logic (Windows compares folders without case and a trailing separator), with this OS's separator.
        var sep = Path.PathSeparator;
        var (path, added) = Setup.WindowsPath.Merge($"/windows{sep}/tools/", ["/windows", "/TOOLS", "/program files/nodejs/", ""]);
        Assert.Equal($"/windows{sep}/tools/{sep}/program files/nodejs/", path);
        Assert.Equal(["/program files/nodejs/"], added);
        Assert.Empty(Setup.WindowsPath.Merge(path, ["/program files/nodejs"]).Added);
    }

    [Fact]
    public void Npm_failures_are_said_in_plain_words()
    {
        Assert.Contains("isn't in its registry", AcpAgentConnector.NpmError("a@1.0.0", ["npm error code E404"]), StringComparison.Ordinal);
        Assert.Contains("npm config set proxy", AcpAgentConnector.NpmError("a@1.0.0", ["npm error code ETIMEDOUT"]), StringComparison.Ordinal);
        Assert.Contains("npm config set cafile", AcpAgentConnector.NpmError("a@1.0.0", ["npm error code SELF_SIGNED_CERT_IN_CHAIN"]), StringComparison.Ordinal);
        Assert.Contains("too old for the package", AcpAgentConnector.NpmError("a@1.0.0", ["npm error code EBADENGINE"]), StringComparison.Ordinal);
        Assert.Equal("npm couldn't download a@1.0.0. Nothing was saved.", AcpAgentConnector.NpmError("a@1.0.0", ["something else"]));
    }

    [Fact]
    public void The_user_guide_has_the_coverage_table_and_every_templates_sign_in()
    {
        var guide = File.ReadAllText(Path.Combine(DataHostTests.Metadata("CodalusRepoRoot"), "docs", "user-guide", "ai.md"));
        foreach (var (agent, windows, macOS, linux) in AcpAgentTemplates.Coverage)
        {
            Assert.Contains($"| {agent} | {windows} | {macOS} | {linux} |", guide, StringComparison.Ordinal);
        }

        foreach (var template in AcpAgentTemplates.All)
        {
            Assert.Contains(template.Agent.CommandLine, guide, StringComparison.Ordinal);
        }

        Assert.Contains(AcpAgentTemplates.PiCliPackage, guide, StringComparison.Ordinal);
        Assert.Contains(AcpAgentTemplates.OpenCodeCliPackage, guide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_keeps_the_comments_inside_ai_agents()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_workbench.Settings.UserFile)!);
        File.WriteAllText(_workbench.Settings.UserFile, "{\n  \"ai.agents\": {\n    // my own\n    \"mine\": { \"command\": \"my-agent\" }\n  }\n}\n");
        _workbench.Settings.Reload();
        Assert.True(await AcpAgentCatalog.AddAsync(_workbench.Settings, AcpAgentCatalog.Agents.Single(a => a.Id == "codex"), CancellationToken.None));
        var text = File.ReadAllText(_workbench.Settings.UserFile);
        Assert.Contains("// my own", text, StringComparison.Ordinal);
        Assert.Equal(["mine", "codex"], _workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.EnumerateObject().Select(p => p.Name));
    }
}
