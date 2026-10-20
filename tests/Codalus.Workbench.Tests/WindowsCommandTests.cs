using Codalus.Workbench.Ai.Acp;

namespace Codalus.Workbench.Tests;

/// <summary>
/// How an agent's command or an approved command starts on Windows (M48, the PR 53 review): npm and npx as node with their
/// script (no cmd.exe reads their arguments), another script through cmd.exe only when nothing in it needs cmd.exe's
/// quoting, refused otherwise. The resolution only looks at files, so it runs on every OS; a script's real run is Windows'.
/// </summary>
public sealed class WindowsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-wincmd-" + Guid.NewGuid().ToString("N"));

    public WindowsCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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

    private string Touch(params string[] parts)
    {
        var file = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return file;
    }

    [Fact]
    public void Npx_from_nodes_layout_runs_as_node_with_its_script_and_its_arguments_untouched()
    {
        Touch("nodejs", "npx.cmd");
        var node = Touch("nodejs", "node.exe");
        var cli = Touch("nodejs", "node_modules", "npm", "bin", "npx-cli.js");
        string[] args = ["-y", "@agentclientprotocol/claude-agent-acp@0.93.0", "a b", "100%", "x&y"];
        var launch = WindowsCommand.Launch("npx", args, Path.Combine(_root, "nodejs"));
        Assert.Equal(node, launch.FileName);
        Assert.Equal([cli, .. args], launch.Arguments);
        Assert.Equal($"{node} {cli} -y @agentclientprotocol/claude-agent-acp@0.93.0 \"a b\" 100% x&y", WindowsCommand.Resolved("npx", launch));
    }

    [Fact]
    public void A_program_on_the_path_runs_as_written_and_another_script_goes_through_cmd_only_when_safe()
    {
        // A program (.exe) comes first, as CreateProcess finds it; nothing found runs as written.
        Touch("tools", "agent.exe");
        Touch("tools", "agent.cmd");
        var path = Path.Combine(_root, "tools") + Path.PathSeparator + Path.Combine(_root, "scripts");
        Assert.Equal("agent", WindowsCommand.Launch("agent", ["--acp"], path).FileName);
        Assert.Equal("missing", WindowsCommand.Launch("missing", [], path).FileName);
        Assert.Null(WindowsCommand.Resolved("agent", WindowsCommand.Launch("agent", ["--acp"], path)));

        // A script without anything cmd.exe reads differently: cmd.exe /d /c, its arguments as they are.
        var script = Touch("scripts", "my-agent.cmd");
        var launch = WindowsCommand.Launch("my-agent", ["--acp", "two words"], path);
        Assert.EndsWith("cmd.exe", launch.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/c", script, "--acp", "two words"], launch.Arguments);

        // Metacharacters cmd.exe would expand or split on, in an argument or the script's path: refused.
        foreach (var arg in new[] { "%PATH%", "a&b", "x|y", "<in", "^", "!v!", "say \"hi\"" })
        {
            var refused = Assert.Throws<AcpException>(() => WindowsCommand.Launch("my-agent", [arg], path));
            Assert.Contains("cmd.exe would read differently", refused.Message, StringComparison.Ordinal);
        }

        Touch("Program Files", "spaced.bat");
        Assert.Throws<AcpException>(() => WindowsCommand.Launch("spaced", [], Path.Combine(_root, "Program Files")));
    }

    [Fact]
    public async Task On_windows_a_script_gets_its_arguments_with_spaces_intact()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("cmd.exe scripts run on Windows only.");
        }

        if (_root.Any(char.IsWhiteSpace))
        {
            Assert.Skip("The temporary folder's path has spaces, which WindowsCommand refuses for a script.");
        }

        var script = Path.Combine(_root, "echo-args.cmd");
        await File.WriteAllTextAsync(script, "@echo [%~1] [%~2]\r\n", TestContext.Current.CancellationToken);
        var launch = WindowsCommand.Launch("echo-args", ["a b", "c"], _root);
        var start = new System.Diagnostics.ProcessStartInfo(launch.FileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in launch.Arguments)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("[a b] [c]", output.Trim());
    }
}
