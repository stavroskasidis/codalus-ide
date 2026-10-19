using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The command sweep's findings (the smoke step <c>shell.commandSweep</c> and <c>CommandSweepTests</c>): each test
/// proves one fix of a command, a key binding or a menu item that didn't do what it says.
/// </summary>
public sealed class SweepFindingsTests
{
    [Fact]
    public async Task A_clipboard_another_program_holds_is_tried_again_then_reported_not_thrown()
    {
        // Windows' OpenClipboard fails while another program holds the clipboard, and Avalonia throws COMException: copy
        // and paste failed with an error (or an unobserved exception) instead of saying so.
        const int cantOpen = unchecked((int)0x800401D0);
        var busy = 0;
        void OnBusy() => busy++;
        Infrastructure.SafeClipboard.Busy += OnBusy;
        try
        {
            var calls = 0;
            Assert.Equal("text", await Infrastructure.SafeClipboard.TryAsync(() => ++calls < 2
                ? throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", cantOpen)
                : Task.FromResult<string?>("text")));
            Assert.Equal((2, 0), (calls, busy));

            Assert.Null(await Infrastructure.SafeClipboard.TryAsync<string?>(() => throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", cantOpen)));
            Assert.Equal(1, busy);
            Assert.True(Infrastructure.SafeClipboard.IsBusy(new AggregateException(new System.Runtime.InteropServices.COMException("x", cantOpen))));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Infrastructure.SafeClipboard.TryAsync<string?>(() => throw new InvalidOperationException()));
        }
        finally
        {
            Infrastructure.SafeClipboard.Busy -= OnBusy;
        }
    }

    [Fact]
    public void Reattach_here_never_takes_another_dotnet_by_name()
    {
        // Once the process attached to last ended, Reattach took the first process with its name: for a framework-dependent
        // program, any "dotnet" (an MSBuild node, a language server).
        Workbench.Debugging.ProcessEntry[] processes = [new(7, "dotnet", null, true), new(9, "Hello", null, true)];
        Assert.Null(ShellHost.FindAgain(processes, (42, "dotnet")));
        Assert.Equal(7, ShellHost.FindAgain(processes, (7, "dotnet"))?.Id);
        Assert.Equal(9, ShellHost.FindAgain(processes, (42, "Hello"))?.Id); // an apphost started again
    }

    [Fact]
    public void Create_Dump_dumps_only_programs_on_this_machine()
    {
        // In a session over SSH, in a container or in WSL, the program's id is the target's: the local companion dumped
        // whatever process here has that id, or failed.
        Workbench.Debugging.DebugProcess[] processes =
        [
            new("Hello", 42, Workbench.Debugging.DebugProcess.Attached, "ssh dev@box"),
            new("Web", 7, Workbench.Debugging.DebugProcess.Launched, null),
        ];
        Assert.Equal("ssh dev@box", ShellHost.RunsElsewhere(null, processes, 42));
        Assert.Null(ShellHost.RunsElsewhere(null, processes, 7));
        Assert.Equal("WSL: Ubuntu", ShellHost.RunsElsewhere("WSL: Ubuntu", processes, 7));
    }

    [AvaloniaFact]
    public async Task Turning_a_plugin_on_or_off_keeps_each_settings_layers_own_list()
    {
        // The merged list was written to the user's settings: a workspace's plugins leaked into them, and Turn On never
        // took a plugin out of the workspace's list.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.WorkspaceFile is not null);
        var settings = f.Shell.Workbench.Settings;
        string[] List(SettingScope scope) => settings.GetLayerValue(Workbench.Plugins.PluginManager.DisabledSetting, scope) is { ValueKind: JsonValueKind.Array } ids
            ? [.. ids.EnumerateArray().Select(i => i.GetString()!)]
            : [];
        string[] workspaceList = ["acme.one", "acme.two"];
        await settings.SetAsync(Workbench.Plugins.PluginManager.DisabledSetting, JsonSerializer.SerializeToElement(workspaceList), SettingScope.Workspace, CancellationToken.None);

        _ = f.Shell.SetPluginTurnedOffAsync("acme.three", off: true); // waits on its Restart Now notification
        await ShellFixture.PumpUntil(() => List(SettingScope.User).Length > 0);
        Assert.Equal(["acme.three"], List(SettingScope.User));
        Assert.Equal(["acme.one", "acme.two"], List(SettingScope.Workspace));

        _ = f.Shell.SetPluginTurnedOffAsync("acme.one", off: false);
        await ShellFixture.PumpUntil(() => List(SettingScope.Workspace).Length == 1);
        Assert.Equal(["acme.two"], List(SettingScope.Workspace));
        Assert.Equal(["acme.three"], List(SettingScope.User));
    }

    [AvaloniaFact]
    public async Task A_plugin_activating_on_onSolutionOpened_starts_when_a_solution_opens()
    {
        // The activation event was accepted and documented (API 1.0) but never fired: such a plugin never started.
        var userData = Path.Combine(Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(Platform.AppPaths.Resolve(Platform.PlatformInfo.Current, _ => null, userData).PluginsDirectory, "codalus-tests.on-solution");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "codalus-plugin.json"), """
            {
              "id": "codalus-tests.on-solution",
              "version": "1.0.0",
              "publisher": "codalus-tests",
              "engines": { "codalus": "^1.0.0" },
              "activationEvents": [ "onSolutionOpened" ],
              "contributes": { "settings": { "title": "On Solution", "properties": { "onSolution.enabled": { "type": "boolean", "default": true } } } }
            }
            """, TestContext.Current.CancellationToken);

        await using var f = ShellFixture.Create(userData);
        var plugin = f.Shell.Plugins.Plugins.Single(p => p.Id == "codalus-tests.on-solution");
        Assert.NotEqual(Workbench.Plugins.PluginState.Active, plugin.State);

        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => plugin.State == Workbench.Plugins.PluginState.Active, describe: () => $"the plugin is {plugin.State}");
    }

    [AvaloniaFact]
    public async Task A_csharp_references_CodeLens_runs_Find_All_References()
    {
        // The C# host's CodeLens runs VS Code's editor.action.showReferences, which nothing registered: an error toast.
        await using var f = ShellFixture.Create();
        Assert.False(f.Shell.Workbench.Commands.Find("editor.action.showReferences")?.ShowInPalette ?? true);
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var shapes = document.Controller!;
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("class Circle", StringComparison.Ordinal) + "class C".Length;
        shapes.Editor.TextArea.Focus();
        ShellFixture.Pump();

        await f.ExecuteAsync("editor.action.showReferences");
        await ShellFixture.PumpUntil(() => f.Shell.FindResults.Summary?.Contains("References to 'Circle'", StringComparison.Ordinal) == true, seconds: 60);
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Severity == NotificationSeverity.Error);
    }

    [AvaloniaFact]
    public async Task Ctrl_S_and_File_Save_save_a_resource_file()
    {
        // file.save was enabled only in text files, so Ctrl+S and File → Save did nothing in the .resx, .editorconfig,
        // Windows Forms and plugin editors (their own Ctrl+S binding was blocked by that enablement).
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "Strings.resx");
        var resx = Codalus.Workbench.CustomTools.ResxFile.Empty();
        resx.Set("Greeting", "Hello", null);
        await File.WriteAllTextAsync(path, resx.Save(), TestContext.Current.CancellationToken);
        Assert.Null(await f.Shell.OpenFileAsync(path));
        var document = Assert.IsType<ResxEditorDocument>(f.Shell.Layout.ActiveDocument);
        await ShellFixture.PumpUntil(() => document.Rows.Count == 1);

        document.Rows[0].Value = "Hello, world";
        Assert.True(f.Shell.Workbench.Commands.IsEnabled("file.save"));
        f.Press(Key.S, ShellFixture.Primary);
        await ShellFixture.PumpUntil(() => !document.IsModified);
        Assert.Contains("Hello, world", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Git_history_from_the_Solution_Explorer_shows_the_item_right_clicked()
    {
        // Solution Explorer → Git → View History showed the active editor's file instead of the selected item.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var root = Path.GetDirectoryName(solution)!;
        Git(root, "init", "-q");
        Git(root, "config", "user.name", "Test");
        Git(root, "config", "user.email", "test@example.com");
        Git(root, "config", "commit.gpgsign", "false");
        Git(root, "add", ".");
        Git(root, "commit", "-q", "-m", "initial");
        f.Shell.Trust.Trust(Path.GetDirectoryName(root)!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count > 0 && f.Shell.HasRepository, seconds: 120);

        // HasRepository holds once the repository is found; gitRepository (the command's enablement) once its first
        // status has run, a git process and a posted job later: the command was refused in between (macOS CI, PR #76).
        await f.WhenEnabledAsync("git.fileHistory", seconds: 60);

        var file = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).First(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.NotNull(await f.Shell.OpenFileAsync(file));
        var project = f.Shell.Explorer.Rows.First(r => r.Node.Kind == Codalus.Workbench.Projects.ExplorerNodeKind.Project);
        f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: true);
        f.Shell.Explorer.Selected = project;
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId); // as SolutionTests do: the context menu's

        await f.Shell.Workbench.Commands.ExecuteAsync("git.fileHistory", TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Codalus.Shell.ViewModels.GitHistoryDocument);
        Assert.Equal("History: " + Path.GetFileName(project.Node.Path), f.Shell.Layout.ActiveDocument!.Title);
    }

    [AvaloniaFact]
    public async Task Menus_list_each_window_once_and_titles_name_their_category_once()
    {
        await using var f = ShellFixture.Create();

        // View → Other Windows had the remote Ports window twice: Forwarded Ports and the generic Ports entry.
        var others = f.Shell.Workbench.Menus.GetAllItems(BuiltIns.OtherWindowsMenu);
        Assert.Single(others, i => i.Command is "remote.ports" or "workbench.view." + RemotePortsViewModel.ToolWindowId);

        // The palette showed "Markdown: Markdown: Open Preview to the Side".
        Assert.Equal("Markdown: Open Preview to the Side", f.Shell.Workbench.Commands.Find("markdown.showPreviewToSide")!.DisplayTitle);
    }

    [AvaloniaFact]
    public async Task Escape_closes_any_quick_pick_before_it_hides_the_toasts()
    {
        // Only the command palette and Color Theme set inCommandPalette, so in every other picker the first Escape hid
        // the toasts (workbench.hideToasts) instead of closing the picker.
        await using var f = ShellFixture.Create();
        f.Shell.Workbench.Notifications.Warning("Something needs attention");
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("toastsVisible"), true));

        var pick = f.Shell.Palette.ShowAsync([new QuickPickItem("One"), new QuickPickItem("Two")], "Pick one");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Window.GetVisualDescendants().OfType<QuickPickView>().Any(v => v.IsEffectivelyVisible));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("inCommandPalette"));
        f.Press(Key.Escape);

        Assert.Null(await pick.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("toastsVisible"));
    }

    [AvaloniaFact]
    public async Task The_package_manager_ignores_a_search_after_its_document_closed()
    {
        // A load that finished after the document closed restarted its list load on the disposed cancellation source:
        // an ObjectDisposedException, unobserved (the command sweep's packages.manage and packages.sources).
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count > 0, seconds: 120);

        f.Shell.OpenPackageManager(f.Shell.Solutions.Projects.Keys.First());
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is PackageManagerDocument);
        var document = (PackageManagerDocument)f.Shell.Layout.ActiveDocument!;
        f.Shell.Layout.CloseDocument(document);
        await ShellFixture.PumpUntil(() => !f.Shell.Layout.Documents.Contains(document));

        await document.ViewModel.SearchNowAsync();
    }

    [AvaloniaFact]
    public async Task Save_as_solution_filter_says_saved_only_when_it_was()
    {
        // In folder mode the build host refuses a filter (no solution file): the error showed, then "Saved …" anyway.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.Combine(Path.GetDirectoryName(solution)!, "FolderOnly");
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenFolderAsync(folder);
        // Also until the command is enabled: its context keys (solutionOpen, solutionTrusted) are set on the UI thread from
        // the solution service's event, after the service's own state, which the build host's thread changes. The wait
        // could end between the two, and the command was refused (#68's and #69's Linux runs).
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Solution is { IsFolder: true } && f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Workbench.Commands.IsEnabled("solution.saveFilter"),
            seconds: 120,
            describe: () => $"solution {f.Shell.Solutions.Solution?.Path} (folder {f.Shell.Solutions.Solution?.IsFolder}, trusted {f.Shell.Solutions.Solution?.Trusted}), progress completed {f.Shell.Solutions.Progress?.Completed}, solution.saveFilter enabled {f.Shell.Workbench.Commands.IsEnabled("solution.saveFilter")}");

        var target = Path.Combine(f.UserData, "Folder.slnf");
        f.Shell.Window = new SavingWindow(f.Shell.Window, target);
        var before = f.Shell.Workbench.Notifications.Items.Count;
        await f.ExecuteAsync("solution.saveFilter");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Count > before || File.Exists(target), seconds: 60);
        ShellFixture.Pump();

        var saved = f.Shell.Workbench.Notifications.Items.Any(n => n.Message.StartsWith("Saved ", StringComparison.Ordinal));
        Assert.Equal(File.Exists(target), saved);
    }

    [Fact]
    public void No_ui_string_says_Visual_Studio()
    {
        // The product's own name only (UI rules): the WCF note said "as Visual Studio does".
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Localization", "en.json")))!.AsObject();
        Assert.Empty(catalog.Where(p => p.Value!.GetValue<string>().Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key));
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
    }

    /// <summary>The window, with Save As answering <paramref name="path"/> instead of a native dialog.</summary>
    private sealed class SavingWindow(IShellWindow? inner, string path) : IShellWindow
    {
        public Avalonia.Input.Platform.IClipboard? Clipboard => inner?.Clipboard;

        public bool IsFullScreen
        {
            get => false;
            set { }
        }

        public void FocusPane(bool backwards) => inner?.FocusPane(backwards);

        public void RequestClose()
        {
        }

        public void BringToFront()
        {
        }

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickProjectAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns) => Task.FromResult<string?>(path);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }
}
