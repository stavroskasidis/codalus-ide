using System.Text;
using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>
/// Debuggers from plugins (plugin API 1.0, M22a): a declarative plugin's debug adapter serves the launch configurations
/// of its type. The adapter is the scripted one (<c>Codalus.LspConformance.Server --dap</c>).
/// </summary>
public sealed class ContributedDebuggerTests
{
    private static string ScriptedServer => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.LspConformance.Server", "bin",
        typeof(ContributedDebuggerTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusConfiguration").Value!, "net10.0", "codalus-lsp-conformance.dll");

    [AvaloniaFact]
    public async Task A_launch_configuration_of_a_plugins_type_runs_under_its_adapter()
    {
        // A declarative plugin: the adapter is a .dll, started with dotnet; ${pluginDir} isn't needed here.
        var plugin = Path.Combine(Path.GetTempPath(), "codalus-debugger-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "codalus-plugin.json"), $$"""
            {
              "id": "codalus-tests.scripted",
              "version": "1.0.0",
              "displayName": "Scripted Debugger",
              "publisher": "codalus-tests",
              "engines": { "codalus": "^1.0.0" },
              "contributes": {
                "debuggers": [ { "type": "scripted", "label": "Scripted", "command": {{System.Text.Json.JsonSerializer.Serialize(ScriptedServer)}}, "args": [ "--dap" ] } ]
              }
            }
            """);
        try
        {
            await using var f = ShellFixture.Create(builtInPlugins: plugin);
            Assert.Contains(f.Shell.Plugins.Debuggers, d => d.Type == "scripted" && d.Label == "Scripted");

            // A folder with a launch configuration of that type, with a variable in it.
            var folder = Path.Combine(f.UserData, "Scripted Folder");
            Directory.CreateDirectory(Path.Combine(folder, ".codalus"));
            File.WriteAllText(Path.Combine(folder, ".codalus", "launch.json"), """
                {
                  "version": "0.2.0",
                  "configurations": [
                    { "name": "Run the script", "type": "scripted", "request": "launch", "greeting": "hello from ${workspaceFolderBasename}", "level": 3 }
                  ]
                }
                """);
            f.Shell.Trust.Trust(folder);
            await f.Shell.OpenSolutionAsync(folder);
            await ShellFixture.PumpUntil(() => f.Shell.LaunchConfigurations().Any(c => c.Name == "Run the script"), seconds: 120);

            var output = new StringBuilder();
            f.Shell.Debugger.Output += (_, o) =>
            {
                lock (output)
                {
                    output.Append(o.Text);
                }
            };
            string Output()
            {
                lock (output)
                {
                    return output.ToString();
                }
            }

            f.Shell.SelectLaunchConfiguration("Run the script");
            f.Shell.Run("debug.start");

            // The adapter got the configuration, variables expanded and other values as they were, and its program ran.
            try
            {
                await ShellFixture.PumpUntil(() => Output().Contains("Hello from the scripted program", StringComparison.Ordinal), seconds: 60);
            }
            catch (Exception ex) when (ex is TimeoutException or Xunit.Sdk.XunitException)
            {
                Assert.Fail("Debug log:\n" + f.Shell.DebugLog.GetText() + "\nOutput:\n" + Output() + "\nNotifications:\n" + string.Join('\n', f.Shell.Workbench.Notifications.Items.Select(n => n.Message)));
            }
            Assert.Contains("\"greeting\":\"hello from Scripted Folder\"", Output(), StringComparison.Ordinal);
            Assert.Contains("\"level\":3", Output(), StringComparison.Ordinal);
            Assert.DoesNotContain("noDebug", Output(), StringComparison.Ordinal);
            await ShellFixture.PumpUntil(() => !f.Shell.Debugger.IsActive, seconds: 30);

            // Start Without Debugging: the same adapter, told noDebug.
            f.Shell.Run("run.startWithoutDebugging");
            await ShellFixture.PumpUntil(() => Output().Contains("\"noDebug\":true", StringComparison.Ordinal), seconds: 60);
            await ShellFixture.PumpUntil(() => !f.Shell.Debugger.IsActive, seconds: 30);
        }
        finally
        {
            Directory.Delete(plugin, recursive: true);
        }
    }
}
