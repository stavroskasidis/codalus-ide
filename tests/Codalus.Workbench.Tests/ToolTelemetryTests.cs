using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Xunit;

namespace Codalus.Workbench.Tests;

/// <summary>M59: the tools' telemetry opt-outs, set for every host and tool the IDE starts through one helper.</summary>
public sealed class ToolTelemetryTests
{
    private static string Metadata(string key) =>
        typeof(ToolTelemetryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    [Fact]
    public void The_helper_adds_each_opt_out_and_keeps_a_value_the_user_set()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["AVALONIA_TELEMETRY_OPTOUT"] = "0", // the user opted in
            ["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "", // set, even empty
        };
        ToolTelemetry.OptOut(inherited);
        Assert.Equal("1", inherited["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", inherited["DOTNET_SVCUTIL_TELEMETRY_OPTOUT"]);
        Assert.Equal("0", inherited["AVALONIA_TELEMETRY_OPTOUT"]);
        Assert.Equal("", inherited["TESTINGPLATFORM_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public void On_top_of_an_inherited_environment_a_variable_already_inherited_is_left_to_it()
    {
        var added = new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1" };
        ToolTelemetry.OptOutOnTop(added, name => name == "AVALONIA_TELEMETRY_OPTOUT" ? "0" : null);
        Assert.False(added.ContainsKey("AVALONIA_TELEMETRY_OPTOUT"));
        Assert.Equal("1", added["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", added["DOTNET_WATCH_SUPPRESS_EMOJIS"]);

        var with = ToolTelemetry.With(null, _ => null);
        Assert.Equal(ToolTelemetry.Variables.Order(), with.Keys.Order());
    }

    [Fact]
    public async Task A_host_started_here_gets_the_opt_outs_and_an_explicit_value_wins()
    {
        var (shell, arguments) = OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/d", "/c", "set" }) : ("/bin/sh", new[] { "-c", "env" });
        var launch = new ProcessLaunchInfo(shell, arguments)
        {
            Environment = new Dictionary<string, string> { ["AVALONIA_TELEMETRY_OPTOUT"] = "0" },
            ToolTelemetryOptOut = true,
            CheckRuntime = false,
        };
        var environment = await EnvironmentOfAsync(launch);
        Assert.Equal("0", environment["AVALONIA_TELEMETRY_OPTOUT"]);
        foreach (var name in ToolTelemetry.Variables.Where(n => n != "AVALONIA_TELEMETRY_OPTOUT"))
        {
            // CI sets them for the job itself: the inherited value stays, and is there either way.
            Assert.True(environment.ContainsKey(name), name + " is missing");
        }

        Assert.True(ProcessLaunchInfo.Host("codalus-buildhost.dll").ToolTelemetryOptOut);
        Assert.False(ProcessLaunchInfo.FromPath("codalus-buildhost.dll").ToolTelemetryOptOut);
    }

    [Fact]
    public void Dotnet_watch_runs_with_the_opt_outs_unless_the_environment_has_them()
    {
        var runs = new RunService(null!, new OutputService(), _ => { });
        var plan = new LaunchPlan("App", string.Empty, "/src/App", new Dictionary<string, string>(), null);
        var watch = runs.HotReloadPlan("/src/App/App.csproj", plan, null).Environment;
        foreach (var name in ToolTelemetry.Variables)
        {
            Assert.True(watch.ContainsKey(name) || Environment.GetEnvironmentVariable(name) is not null, name + " is missing");
        }

        Assert.DoesNotContain("TELEMETRY", string.Join(' ', plan.Environment.Keys), StringComparison.Ordinal); // the program's own plan is unchanged
    }

    [Fact]
    public void A_remote_test_environments_script_keeps_a_value_the_machine_has()
    {
        var exports = ToolTelemetry.PosixShellExports();
        Assert.StartsWith("export ", exports, StringComparison.Ordinal);
        Assert.All(ToolTelemetry.Variables, name => Assert.Contains($"{name}=\"${{{name}-1}}\"", exports, StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"{exports}; echo \"$AVALONIA_TELEMETRY_OPTOUT,$DOTNET_SVCUTIL_TELEMETRY_OPTOUT\"");
        start.Environment["AVALONIA_TELEMETRY_OPTOUT"] = "0";
        start.Environment.Remove("DOTNET_SVCUTIL_TELEMETRY_OPTOUT");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal("0,1", output);
    }

    [Fact]
    public void Avalonias_refusal_on_a_community_tier_gets_a_line_saying_how_to_build()
    {
        // The task's own message (Avalonia.BuildServices 11.3.2).
        var refusal = "App.csproj : error : Avalonia Accelerate Community requires telemetry. To opt out, please upgrade to a paid tier. Learn more: https://avaloniaui.net/accelerate/";
        Assert.Equal(ToolTelemetry.AvaloniaTierExplanation, BuildService.ExplainTelemetryRefusal(refusal));
        Assert.Contains("AVALONIA_TELEMETRY_OPTOUT=0", BuildService.ExplainTelemetryRefusal(refusal.Replace("Community", "Trial", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Null(BuildService.ExplainTelemetryRefusal("Build succeeded."));
    }

    /// <summary>
    /// The files that start a process without the opt-outs, each with why: the user's own programs and tools, and
    /// programs that aren't .NET tools. A new process start goes through the helper or gets a line here.
    /// </summary>
    private static readonly Dictionary<string, string> s_notTools = new(StringComparer.Ordinal)
    {
        ["Codalus.Debugging.Host/CoreClr/LaunchedProgram.cs"] = "the user's program, under the debugger",
        ["Codalus.Debugging.Host/CoreClr/RuntimeLocator.cs"] = "lsof, to find a program's runtime",
        ["Codalus.Debugging/DebugAdapter.cs"] = "a debug adapter: the program it launches inherits its environment",
        ["Codalus.Debugging/JsDebugAdapter.cs"] = "the browser debugger (Node)",
        ["Codalus.Platform/ShellIntegration.cs"] = "opens a URL or a file with the OS",
        ["Codalus.Remote.Server/ServerDaemon.cs"] = "the server's own daemon, which starts terminals",
        ["Codalus.Remote.Server/ServerPorts.cs"] = "lsof, to list ports",
        ["Codalus.Shell/Infrastructure/SmokeTest.CommandSweep.cs"] = "git, for the smoke test's fixture",
        ["Codalus.Shell/Infrastructure/SmokeTest.cs"] = "the smoke test's programs and git; CI sets the variables for the run",
        ["Codalus.Shell/Infrastructure/Updates.cs"] = "gh, for a token",
        ["Codalus.Shell/ShellHost.Hosting.cs"] = "another Codalus window",
        ["Codalus.Shell/ShellHost.Mobile.cs"] = "the platform's mobile tools (adb, emulators)",
        ["Codalus.Shell/ShellHost.PluginDevelopment.cs"] = "Codalus itself, for a plugin under development",
        ["Codalus.Shell/ShellHost.Restart.cs"] = "Codalus itself, restarting",
        ["Codalus.Shell/ShellHost.WebLanguages.cs"] = "Node language servers",
        ["Codalus.Workbench/Ai/Acp/AcpAgentConnector.cs"] = "an external agent: its telemetry is the user's choice",
        ["Codalus.Workbench/Ai/Acp/AcpClient.cs"] = "an external agent",
        ["Codalus.Workbench/Ai/Acp/AcpTerminals.cs"] = "an external agent's commands",
        ["Codalus.Workbench/Ai/Acp/WindowsCommand.cs"] = "an external agent's command line",
        ["Codalus.Workbench/Ai/Agent/AgentTools.cs"] = "agent mode's commands, approved by the user, run as in the terminal",
        ["Codalus.Workbench/Ai/Ide/AgentBrowser.cs"] = "a browser",
        ["Codalus.Workbench/Ai/Mcp/McpClient.cs"] = "the user's MCP servers",
        ["Codalus.Workbench/Build/ConsoleWindow.cs"] = "the user's program in a console window (a dotnet watch plan carries the variables)",
        ["Codalus.Workbench/Containers/ContainerEngine.cs"] = "docker or podman, the user's CLI",
        ["Codalus.Workbench/Debugging/JitDebugging.cs"] = "launchctl",
        ["Codalus.Workbench/Debugging/Remote/RemoteShell.cs"] = "ssh, docker or wsl, a transport",
        ["Codalus.Workbench/Debugging/Remote/RemoteTargets.cs"] = "ssh, docker or wsl, a transport",
        ["Codalus.Workbench/ExternalTools/ExternalTools.cs"] = "the user's external tools",
        ["Codalus.Workbench/Git/GitService.cs"] = "git, the user's CLI",
        ["Codalus.Workbench/Node/NodeRuntime.cs"] = "Node",
        ["Codalus.Workbench/Node/Npm.cs"] = "npm and the project's scripts",
        ["Codalus.Workbench/Node/WebTools.cs"] = "Node tools",
        ["Codalus.Workbench/Projects/FolderConfiguration.cs"] = "the user's tasks (tasks.json), run as in the terminal",
        ["Codalus.Workbench/Remote/RemoteConnection.cs"] = "ssh or the server's transport: the server adds the variables to the hosts it starts",
        ["Codalus.Workbench/Setup/LoginShellPath.cs"] = "the user's login shell, read for PATH",
        ["Codalus.Workbench/Terminal/TerminalService.cs"] = "the integrated terminal, the user's own",
        ["ThirdParty/Mono.Debugger.Soft/VirtualMachineManager.cs"] = "Mono, the user's program",
    };

    [Fact]
    public void Every_process_the_product_starts_gets_the_opt_outs_or_says_why_not()
    {
        var source = Path.Combine(Metadata("CodalusRepoRoot"), "src");
        var starts = new Regex(@"new (System\.Diagnostics\.)?ProcessStartInfo\b|new ProcessLaunchInfo\b|ProcessLaunchInfo\.FromPath\(|new PtyStartInfo\b|ProcessStartInfo\(", RegexOptions.CultureInvariant);
        var optOut = new Regex(@"ToolTelemetry\.|ToolTelemetryOptOut|ProcessLaunchInfo\.Host\(", RegexOptions.CultureInvariant);
        var starting = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (Path: Path.GetRelativePath(source, file).Replace('\\', '/'), Text: File.ReadAllText(file)))
            .Where(file => file.Path is not "Codalus.Platform/ToolTelemetry.cs" && starts.IsMatch(file.Text))
            .ToList();
        var missing = starting.Where(file => !optOut.IsMatch(file.Text) && !s_notTools.ContainsKey(file.Path)).Select(file => file.Path).ToList();
        Assert.True(missing.Count == 0, "Starts a process without the tools' telemetry opt-outs (ToolTelemetry, ProcessLaunchInfo.Host or ToolTelemetryOptOut), and isn't listed with why: " + string.Join(", ", missing));
        var stale = s_notTools.Keys.Where(path => !starting.Any(file => file.Path == path && !optOut.IsMatch(file.Text))).ToList();
        Assert.True(stale.Count == 0, "Listed, but no longer starts a process without the opt-outs: " + string.Join(", ", stale));
    }

    [Fact]
    public void The_repositorys_builds_set_every_opt_out()
    {
        var root = Metadata("CodalusRepoRoot");
        var script = File.ReadAllText(Path.Combine(root, "eng", "telemetry-optout.sh"));
        var setup = File.ReadAllText(Path.Combine(root, ".github", "actions", "setup", "action.yml"));
        foreach (var name in ToolTelemetry.Variables)
        {
            Assert.Contains(name, script, StringComparison.Ordinal);
            Assert.Contains(name, setup, StringComparison.Ordinal);
        }

        foreach (var user in new[] { "validate.sh", "smoke-test.sh", "wsl-test.sh", "ci-test.sh", "compat-containers.sh", "package.sh" })
        {
            Assert.Contains("telemetry-optout.sh", File.ReadAllText(Path.Combine(root, "eng", user)), StringComparison.Ordinal);
        }
    }

    private static async Task<Dictionary<string, string>> EnvironmentOfAsync(ProcessLaunchInfo launch)
    {
        await using var process = await LocalProcessLauncher.Instance.StartAsync(launch, CancellationToken.None);
        using var reader = new StreamReader(process.Output);
        var text = await reader.ReadToEndAsync();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimEnd('\r') is var entry && entry.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                environment[entry[..equals]] = entry[(equals + 1)..];
            }
        }

        return environment;
    }
}
