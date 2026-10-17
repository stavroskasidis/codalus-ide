using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Shell.Views;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// M43a (ADR 0075, D31) in the headless shell on samples/NetFramework: .NET Framework projects open (the old-style one
/// marked, the web one saying why it can't build), off Windows Start and Start Without Debugging say they're Windows
/// only and build nothing, and Convert to SDK-Style shows its preview and applies it. On Windows, Start Without
/// Debugging runs the net48 console app and its output comes back, and F5 debugs it on the desktop CLR (M43b, ADR
/// 0076): it stops at a breakpoint, evaluates and ends.
/// </summary>
public sealed class NetFrameworkShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("NetFramework");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "NetFramework.slnx"));
        var legacy = Project(folder, "LegacyApp");
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Projects.Count == 8 && f.Shell.Solutions.Progress is { Completed: true }
                  && f.Shell.Solutions.Projects.TryGetValue(legacy, out var info) && info.References.Any(r => r.Kind == ReferenceKinds.Package && r.Resolved),
            seconds: 300);
        return (f, folder);
    }

    private static string Project(string folder, string name) => Path.Combine(folder, name, name + ".csproj");

    private static ExplorerNode? Find(ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [AvaloniaFact]
    public async Task Net_framework_projects_open_and_run_only_on_windows()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var console = Project(folder, "ConsoleApp");
        var legacy = Project(folder, "LegacyApp");
        var web = Project(folder, "LegacyWebApp");

        // Solution Explorer: the old-style project says so, with its packages.config package; the web project says why it doesn't build.
        var explorer = f.Shell.Explorer;
        await ShellFixture.PumpUntil(() => explorer.Root is { } root && Find(root, "p:" + legacy) is { Detail: "(non-SDK-style)" } && Find(root, "p:" + web) is { } webNode && webNode.Children.Any(c => c.Id == "p:" + web + "|cannotBuild"), seconds: 60);
        var webNode = Find(explorer.Root!, "p:" + web)!;
        Assert.Equal("(non-SDK-style, can't build here)", webNode.Detail);
        Assert.Contains("Visual Studio's own MSBuild targets", webNode.Children.Single(c => c.Id == "p:" + web + "|cannotBuild").Name, StringComparison.Ordinal);
        Assert.True(f.Shell.Solutions.Projects[legacy].IsNonSdkStyle);
        Assert.True(NetFrameworkProjects.IsNetFramework(f.Shell.Solutions.Projects[console]));

        // Convert to SDK-Style is on the context menu of old-style projects only.
        Assert.Contains(f.Shell.Workbench.Menus.GetAllItems(SolutionExplorerView.ContextMenuLocation), i => i.Command == "project.convertToSdkStyle");

        // Hot Reload doesn't apply to .NET Framework.
        Assert.Equal("Hot Reload needs .NET 6 or later: ConsoleApp targets net48 (.NET Framework).", f.Shell.HotReloadUnsupportedReason(console));

        // Off Windows, Start and Start Without Debugging say it's Windows only, and build nothing.
        f.Shell.SetStartupProject(console);
        if (!OperatingSystem.IsWindows())
        {
            var builds = f.Shell.Builds.LastBuild;
            await f.Shell.StartDebuggingAsync();
            await f.Shell.StartWithoutDebuggingAsync();
            Assert.Equal(2, f.Shell.Workbench.Notifications.Items.Count(n => n.Message.Contains("ConsoleApp targets .NET Framework (net48): it runs, debugs and tests only on Windows", StringComparison.Ordinal)));
            Assert.Same(builds, f.Shell.Builds.LastBuild);
            Assert.Empty(f.Shell.Runs.Running);
            Assert.NotNull(f.Shell.WindowsOnlyReason(legacy));
        }

        // The solution builds everywhere, without the web project.
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, null), f.Shell.Builds.Channel.GetText());
        // Output notifications may be handled just after the result arrives.
        await ShellFixture.PumpUntil(() => f.Shell.Builds.Channel.GetText().Contains("LegacyWebApp is left out of the build", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(folder, "LegacyApp", "bin", "Debug", "LegacyApp.exe")));
    }

    [AvaloniaFact]
    public async Task Convert_to_sdk_style_previews_the_change_and_applies_it()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var legacy = Project(folder, "LegacyApp");
        var golden = Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "NetFramework");

        var converting = f.Shell.ConvertToSdkStyleAsync(legacy);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, seconds: 60);
        var details = f.Shell.Dialogs.Details;
        Assert.Contains(details, l => l.Text.StartsWith("• packages.config's 1 package(s) became PackageReference items", StringComparison.Ordinal));
        Assert.Contains(details, l => l.Kind == "header" && l.Text == legacy);
        Assert.Contains(details, l => l.Kind == "removed" && l.Text.StartsWith("- <Project ToolsVersion=\"15.0\"", StringComparison.Ordinal));
        Assert.Contains(details, l => l.Kind == "added" && l.Text == "+ <Project Sdk=\"Microsoft.NET.Sdk\">");
        Assert.Contains(details, l => l.Kind == "added" && l.Text == "+     <Compile Remove=\"Drafts\\NotInProject.cs\" />");
        Assert.Contains(details, l => l.Kind == "header" && l.Text == Path.Combine(folder, "LegacyApp", "Properties", "AssemblyInfo.cs"));
        Assert.Contains(details, l => l.Kind == "removed" && l.Text == Path.Combine(folder, "LegacyApp", "packages.config") + " → deleted");
        f.Shell.Dialogs.Accept();
        await converting;

        Assert.Equal(File.ReadAllText(Path.Combine(golden, "LegacyApp.csproj.golden")), File.ReadAllText(legacy));
        Assert.Equal(File.ReadAllText(Path.Combine(golden, "AssemblyInfo.cs.golden")), File.ReadAllText(Path.Combine(folder, "LegacyApp", "Properties", "AssemblyInfo.cs")));
        Assert.False(File.Exists(Path.Combine(folder, "LegacyApp", "packages.config")));

        // It reloads SDK-style, restores its package as a package reference, and builds.
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[legacy] is { IsNonSdkStyle: false } info && info.References.Any(r => r.Kind == ReferenceKinds.Package && r.Name == "Codalus.Sample.Legacy" && r.Resolved), seconds: 180);
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, legacy), f.Shell.Builds.Channel.GetText());
        Assert.True(File.Exists(Path.Combine(folder, "LegacyApp", "bin", "Debug", "net472", "LegacyApp.exe")));
    }

    [AvaloniaFact]
    public async Task Windows_starts_and_debugs_the_net48_console_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), ".NET Framework apps run on Windows only (D31).");
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var console = Project(folder, "ConsoleApp");
        f.Shell.SetStartupProject(console);

        await f.Shell.StartWithoutDebuggingAsync();

        // A program that ends leaves Runs.Running at once; its Run channel says how it ended, after its output.
        string Output() => f.Shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: ConsoleApp")?.GetText() ?? string.Empty;
        await ShellFixture.PumpUntil(() => Output().Contains("has exited with code", StringComparison.Ordinal), seconds: 240);
        Assert.Contains("has exited with code 0 (0x0)", Output(), StringComparison.Ordinal);
        Assert.Contains("Runtime: .NET Framework 4.", Output(), StringComparison.Ordinal);
        Assert.Contains("Hello from .NET Framework, Codalus!", Output(), StringComparison.Ordinal);

        // F5 debugs it on the desktop CLR (M43b): a breakpoint in Main, the frame, an evaluation, and Stop ends it.
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugConsoleSetting, "internalConsole", Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
        var source = Path.Combine(folder, "ConsoleApp", "Program.cs");
        var line = File.ReadAllLines(source).ToList().FindIndex(l => l.Contains("Console.WriteLine(greeter.Greet())", StringComparison.Ordinal)) + 1;
        f.Shell.Debugger.Breakpoints.Toggle(source, line);
        await f.Shell.StartDebuggingAsync();
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == Codalus.Workbench.Debugging.DebugState.Stopped, seconds: 240);
            Assert.Equal("breakpoint", f.Shell.Debugger.CurrentStop?.Reason);
            Assert.Equal(line, f.Shell.Debugger.SelectedFrame?.Line);
            Assert.Equal("ConsoleApp.Program.Main", f.Shell.Debugger.SelectedFrame?.Name);
            Assert.Equal("\"Hello from .NET Framework, Codalus!\"", (await f.Shell.Debugger.EvaluateAsync("greeter.Greet()", "watch", CancellationToken.None)).Result);
        }
        finally
        {
            await f.Shell.Debugger.StopAsync();
        }

        await ShellFixture.PumpUntil(() => !f.Shell.Debugger.IsActive, seconds: 60);
    }
}
