using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34g on M43 (.NET Framework, ADR 0075 and 0076): the .NET Framework project UI (Solution Explorer's old-style and
/// can't-build projects with the reason), Convert to SDK-Style's preview, the Error List entries of a project that needs
/// Visual Studio's targets, Check Environment's .NET Framework rows, the desktop-CLR debugger's pieces (its stack frames,
/// labels and frames without user code, its modules' symbol status) and the pickers (New Project's .NET Framework
/// entries, the framework, Attach to Process's .NET Framework processes), each in its states at its minimum size in all
/// sixteen themes, with the same checks as the other M34g views.
/// </summary>
public partial class PolishTests
{
    /// <summary>With CODALUS_UI_SNAPSHOTS set, a picture of <paramref name="view"/> as checked, in Classic Dark and Harbor High contrast, for review.</summary>
    private static void Snapshot(string surface, Control view, double width, double height)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is not { Length: > 0 } snapshots)
        {
            return;
        }

        Directory.CreateDirectory(snapshots);
        foreach (var (name, variant) in s_themes.Where(t => t.Variant == Avalonia.Styling.ThemeVariant.Dark || t.Variant == Theming.ThemeService.HarborHighContrast))
        {
            var panel = new Border { Child = view };
            panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("Shell.Panel"));
            var host = new Window { Width = width, Height = height, RequestedThemeVariant = variant, Content = panel };
            host.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            using (var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(host))
            {
                var file = string.Concat((surface + " " + name).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
                frame!.Save(Path.Combine(snapshots, file + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            panel.Child = null;
            host.Close();
            ShellFixture.Pump();
        }
    }

    [AvaloniaFact]
    public async Task The_net_framework_project_views_stay_readable_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("NetFramework");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "NetFramework.slnx"));
        var legacy = Path.Combine(folder, "LegacyApp", "LegacyApp.csproj");
        var web = Path.Combine(folder, "LegacyWebApp", "LegacyWebApp.csproj");
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Projects.Count == 8 && f.Shell.Solutions.Progress is { Completed: true }
                  && f.Shell.Solutions.Projects.TryGetValue(legacy, out var info) && info.References.Any(r => r.Kind == ReferenceKinds.Package && r.Resolved),
            seconds: 300);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var states = new List<string>();
        void Check(string surface, Control view, double width, double height)
        {
            states.Add(surface);
            Snapshot(surface, view, width, height);
            foreach (var problem in InspectInEveryTheme(view, width, height))
            {
                problems.Add($"{surface}: {problem}");
            }
        }

        // Solution Explorer at 280 px: the old-style project selected ("(non-SDK-style)"), the project that needs Visual
        // Studio's targets with its reason as its first child, the reason selected, then the project selected.
        var explorer = f.Shell.Explorer;
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Id == "p:" + legacy && r.Detail is not null), seconds: 60);
        var explorerTool = f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false)!;
        explorer.CollapseAll();
        explorer.Reveal("p:" + legacy);
        Check("solution-explorer (non-sdk-style selected)", new ContentControl { Content = explorerTool }, ToolWindowMinWidth, 420);
        explorer.Reveal("p:" + web + "|cannotBuild");
        Assert.Equal("p:" + web + "|cannotBuild", explorer.Selected?.Id);
        Check("solution-explorer (can't build, the reason selected)", new ContentControl { Content = explorerTool }, ToolWindowMinWidth, 420);
        explorer.Reveal("p:" + web);
        Check("solution-explorer (can't build, the project selected)", new ContentControl { Content = explorerTool }, ToolWindowMinWidth, 420);

        // The Error List: building the project alone fails with the reason; a solution build leaves it out with a warning.
        var errorList = f.Shell.Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false)!;
        Assert.False(await f.Shell.BuildAsync(BuildTargets.Build, web));
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.OfType<ErrorListRow>().Any(r => r.IsError && r.Description.Contains("imports proprietary MSBuild targets", StringComparison.Ordinal)), seconds: 60);
        var cannotBuild = f.Shell.ErrorList.Rows.OfType<ErrorListRow>().First(r => r.IsError);
        Assert.DoesNotContain(" :", cannotBuild.AccessibleName, StringComparison.Ordinal); // no code: none read out
        Check("error-list (can't build)", new ContentControl { Content = errorList }, ToolWindowMinWidth, 420);
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, null), f.Shell.Builds.Channel.GetText());
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.OfType<ErrorListRow>().Any(r => r.IsWarning && r.Description.StartsWith("LegacyWebApp is left out of the build", StringComparison.Ordinal)), seconds: 60);
        Check("error-list (left out of the build)", new ContentControl { Content = errorList }, ToolWindowMinWidth, 420);

        // Convert to SDK-Style's preview: the converter's notes and the changes, at the dialog's narrowest and at a document's.
        var converting = f.Shell.ConvertToSdkStyleAsync(legacy);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, seconds: 60);
        var dialog = new DialogView { DataContext = f.Shell.Dialogs };
        Check("convert-to-sdk-style (preview)", dialog, DialogMinWidth, 560);
        Check("convert-to-sdk-style (preview, wide)", dialog, DocumentMinWidth, 560);
        f.Shell.Dialogs.Cancel();
        await converting;

        // Check Environment's .NET Framework row on Windows (stood in for here): 4.7.2 installed, the solution targets 4.8.
        f.Shell.ChecksNetFrameworkRuntime = true;
        f.Shell.NetFrameworkRuntimeProbe = () => (461808, false);
        var tools = await f.Shell.CheckEnvironmentAsync(offer: false);
        Assert.Contains(tools, t => t.Id == Codalus.Workbench.Projects.NetFrameworkProjects.RuntimeToolId && t.State == Codalus.Workbench.Setup.ToolState.Mismatch);
        f.Shell.OpenCheckEnvironment();
        var environment = f.Shell.Layout.ActiveDocument!;
        await ShellFixture.PumpUntil(() => environment is EnvironmentDocument { ViewModel: { IsChecking: false } model } && model.Rows.Any(r => r.Tool.Id == Codalus.Workbench.Projects.NetFrameworkProjects.RuntimeToolId), seconds: 120);
        Check("check-environment (.net framework, older)", new ContentControl { Content = environment }, DocumentMinWidth, 560);
        f.Shell.Layout.CloseDocument(environment);

        Assert.True(problems.Count == 0, $"States checked: {string.Join(", ", states)}; problems:\n" + string.Join('\n', problems));
    }

    [AvaloniaFact]
    public async Task The_net_framework_debugger_pieces_and_pickers_stay_readable_in_every_theme()
    {
        // The desktop-CLR debugger runs on Windows only (D31): its stack frames and modules are what NetFxDebugSession
        // sends (internal frames as labels, code without symbols or outside Just My Code as subtle frames, a module's
        // symbol status), shown in the Call Stack and the Modules window as they come from any adapter.
        await using var f = ShellFixture.Create();
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var states = new List<string>();
        void Check(string surface, Control view, double width, double height)
        {
            states.Add(surface);
            Snapshot(surface, view, width, height);
            foreach (var problem in InspectInEveryTheme(view, width, height))
            {
                problems.Add($"{surface}: {problem}");
            }
        }

        var program = Path.Combine(ShellFixture.RepoRoot, "samples", "Debugging", "NetFxDebuggee", "Program.cs");
        var frames = new[]
        {
            new StackFrame(1, "NetFxDebuggee.Program.Describe(NetFxDebuggee.Shape shape)", new Source("Program.cs", program), 42, 13),
            new StackFrame(2, "[Managed to Native Transition]", null, 0, 0, PresentationHint: "label"),
            new StackFrame(3, "System.Linq.Enumerable.WhereSelectListIterator<NetFxDebuggee.Shape, string>.MoveNext()", new Source("Enumerable.cs", "/_/src/System.Core/System/Linq/Enumerable.cs"), 388, 17, PresentationHint: "subtle"),
            new StackFrame(4, "[mscorlib.dll]", null, 0, 0, PresentationHint: "subtle"),
            new StackFrame(5, "NetFxDebuggee.Program.Main(string[] args)", new Source("Program.cs", program), 18, 9),
        };
        var callStack = new CallStackViewModel(f.Shell.Debugger, (_, _) => Task.CompletedTask);
        callStack.Threads.Add(new DapThread(1, "Main Thread"));
        callStack.Threads.Add(new DapThread(7, "Order processing worker with a long name"));
        callStack.SelectedThread = callStack.Threads[0];
        for (var i = 0; i < frames.Length; i++)
        {
            callStack.Frames.Add(new FrameRow(frames[i], i == 0, i == 0));
        }

        var label = Assert.Single(callStack.Frames, r => r.Frame.Id == 2);
        Assert.True(label.IsLabel);
        Assert.Equal(string.Empty, label.Location); // a label isn't external code: nothing in Location
        Assert.True(Assert.Single(callStack.Frames, r => r.Frame.Id == 3).IsDimmed); // outside Just My Code, with source
        Assert.False(callStack.Frames[0].IsDimmed);
        Check("call-stack (desktop clr)", new CallStackView { DataContext = callStack }, ToolWindowMinWidth, 420);

        var modules = new ModulesViewModel(f.Shell.Debugger);
        var debuggee = Path.Combine(ShellFixture.RepoRoot, "samples", "Debugging", "NetFxDebuggee", "bin", "Debug", "net48");
        modules.Rows.Add(new ModuleRow(new DebugModule("1", "mscorlib.dll", @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll", "No symbols: there's no PDB.", true, false, 0)));
        modules.Rows.Add(new ModuleRow(new DebugModule("2", "NetFxDebuggee.exe", Path.Combine(debuggee, "NetFxDebuggee.exe"), "Symbols loaded.", false, true, 1)));
        modules.Rows.Add(new ModuleRow(new DebugModule("3", "Codalus.Sample.Legacy.dll", Path.Combine(debuggee, "Codalus.Sample.Legacy.dll"), "Codalus.Sample.Legacy.pdb is a Windows PDB, which this debugger doesn't read: build with portable PDBs (DebugType portable).", false, false, 2)));
        Check("modules (desktop clr)", new ModulesView { DataContext = modules }, ToolWindowMinWidth, 420);

        // The picker over the window: Attach to Process's processes. (New Project's .NET Framework rows are the wizard's
        // since 0.6.0, ADR 0082, no longer pickers.)
        var paletteView = f.Window.GetVisualDescendants().OfType<QuickPickView>().First();
        var pickers = new (string Name, string Placeholder, string Query, QuickPickItem[] Items)[]
        {
            ("attach-to-process", "Attach to a process (.NET processes are listed first)", "", [
                new QuickPickItem("LegacyApp (4242)") { Detail = new ProcessEntry(4242, "LegacyApp", "Orders — Contoso Legacy Order Entry (Administrator)", true, true).Runtime + " · Orders — Contoso Legacy Order Entry (Administrator)" },
                new QuickPickItem("ConsoleApp (5150)") { Detail = new ProcessEntry(5150, "ConsoleApp", null, true).Runtime },
            ]),
        };
        foreach (var (name, placeholder, query, items) in pickers)
        {
            var picking = f.Shell.Palette.ShowAsync(items, placeholder);
            await ShellFixture.PumpUntil(() => paletteView.IsEffectivelyVisible);
            f.Shell.Palette.Query = query;
            await ShellFixture.PumpUntil(() => f.Shell.Palette.Rows.Count > 0 && (query.Length == 0 || f.Shell.Palette.Rows[0].Highlights.Count > 0));
            states.Add(name);
            foreach (var problem in InspectInPlaceInEveryTheme(f.Window, paletteView))
            {
                problems.Add($"{name}: {problem}");
            }

            if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
            {
                Directory.CreateDirectory(snapshots);
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
                frame!.Save(Path.Combine(snapshots, string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-')) + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            f.Shell.Palette.Cancel();
            await picking;
        }

        Assert.True(problems.Count == 0, $"States checked: {string.Join(", ", states)}; problems:\n" + string.Join('\n', problems));
    }
}
