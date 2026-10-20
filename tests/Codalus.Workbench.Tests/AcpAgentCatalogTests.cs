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
            """{"command":"npx","args":["-y","@agentclientprotocol/claude-agent-acp@0.93.0"]}""",
            user.GetProperty("claude").GetRawText().Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal));
        var configured = AcpAgents.Parse(user);
        Assert.Equal("npx", configured["claude"].Command);
        Assert.Equal(["-y", "@agentclientprotocol/claude-agent-acp@0.93.0"], configured["claude"].Args);
        Assert.Null(settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.Workspace));
        Assert.Equal("{}", File.ReadAllText(_workspaceFile));

        // Already there: not added again, and nothing written.
        Assert.False(await AcpAgentCatalog.AddAsync(settings, claude, CancellationToken.None));
        Assert.Equal(2, settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.EnumerateObject().Count());
    }
}
