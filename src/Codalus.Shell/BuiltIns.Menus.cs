using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// The menu bar and the editor's context menu, in one place (M34a). Each top-level menu is a tree like a classic IDE's:
/// a short first level of the common commands, and submenus (Edit → Go To ▸, IntelliSense ▸, Advanced ▸…) for the
/// rest, so no menu needs scrolling at 1080p (<c>UxPassTests</c> checks it). Feature files keep their own context
/// menus (the Solution Explorer's, the Database Explorer's) and their tool windows' View entries
/// (<see cref="ViewMenuEntry"/>). Plugins add to any of these locations, including the submenus.
/// </summary>
internal static partial class BuiltIns
{
    // Submenu locations. Plugins may contribute to them (docs/plugins.md).
    public const string FileNewMenu = "menuBar/file/new";
    public const string FileOpenMenu = "menuBar/file/open";
    public const string EditGoToMenu = "menuBar/edit/goTo";
    public const string EditFindMenu = "menuBar/edit/find";
    public const string EditPasteSpecialMenu = "menuBar/edit/pasteSpecial";
    public const string EditAdvancedMenu = "menuBar/edit/advanced";
    public const string FileProfilesMenu = "menuBar/file/profiles";
    public const string EditBookmarksMenu = "menuBar/edit/bookmarks";
    public const string EditIntelliSenseMenu = "menuBar/edit/intelliSense";
    public const string EditCaretsMenu = "menuBar/edit/multipleCarets";
    public const string EditOutliningMenu = "menuBar/edit/outlining";
    public const string EditRefactorMenu = "menuBar/edit/refactor";
    public const string GitBranchesMenu = "menuBar/git/branches";
    public const string GitStashMenu = "menuBar/git/stash";
    public const string GitFileMenu = "menuBar/git/file";
    public const string GitHostingMenu = "menuBar/git/hosting";
    public const string GitRepositoryMenu = "menuBar/git/repository";
    public const string ProjectPackagesMenu = "menuBar/project/packages";
    public const string ProjectEntityFrameworkMenu = "menuBar/project/entityFramework";
    public const string ProjectSolutionMenu = "menuBar/project/solution";
    public const string BuildDiagnosticsMenu = "menuBar/build/diagnostics";
    public const string BuildTasksMenu = "menuBar/build/tasks";
    public const string DebugTargetsMenu = "menuBar/debug/otherTargets";
    public const string DebugBreakpointsMenu = "menuBar/debug/breakpoints";
    public const string DebugStartupMenu = "menuBar/debug/startup";
    public const string DebugDumpsMenu = "menuBar/debug/dumps";
    public const string DebugWebMenu = "menuBar/debug/web";
    public const string DebugProfilerMenu = "menuBar/debug/profiler";
    public const string TestPlaylistsMenu = "menuBar/test/playlists";
    public const string TestResultsMenu = "menuBar/test/results";
    public const string TestCoverageMenu = "menuBar/test/coverage";
    public const string AnalyzeMetricsMenu = "menuBar/analyze/codeMetrics";
    public const string ToolsDataMenu = "menuBar/tools/data";
    public const string EditorGitMenu = "editor/context/git";
    public const string EditorSnippetsMenu = "editor/context/snippets";
    public const string EditorOutliningMenu = "editor/context/outlining";
    public const string EditorBreakpointMenu = "editor/context/breakpoint";

    public static void RegisterMenuBar(MenuRegistry menus, Codalus.Platform.PlatformKind platform)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("file", "_File", 10));
        menus.AddMenuBarMenu(new MenuBarMenu("edit", "_Edit", 20));
        menus.AddMenuBarMenu(new MenuBarMenu("view", "_View", 30));
        menus.AddMenuBarMenu(new MenuBarMenu("git", "_Git", 35));
        menus.AddMenuBarMenu(new MenuBarMenu("project", "_Project", 40));
        menus.AddMenuBarMenu(new MenuBarMenu("build", "_Build", 50));
        menus.AddMenuBarMenu(new MenuBarMenu("debug", "_Debug", 60));
        menus.AddMenuBarMenu(new MenuBarMenu("test", "_Test", 65));
        menus.AddMenuBarMenu(new MenuBarMenu("analyze", "_Analyze", 67));
        menus.AddMenuBarMenu(new MenuBarMenu("tools", "_Tools", 70));
        menus.AddMenuBarMenu(new MenuBarMenu("window", "_Window", 80));
        menus.AddMenuBarMenu(new MenuBarMenu("help", "_Help", 90));

        var shell = ContributionSource.Shell;
        var order = 0;
        MenuItemRule Item(string location, string command, string group, string? title = null, string? when = null, string? keys = null) =>
            new(location, command, shell) { Group = group, Order = ++order, Title = title, When = WhenClause.Parse(when), Keys = keys };
        MenuItemRule Sub(string location, string submenu, string title, string group) =>
            MenuItemRule.ForSubmenu(location, submenu, title, shell) with { Group = group, Order = ++order };
        string File = MenuLocations.MenuBar("file"), Edit = MenuLocations.MenuBar("edit"), View = MenuLocations.MenuBar("view"),
            Git = MenuLocations.MenuBar("git"), Project = MenuLocations.MenuBar("project"), Build = MenuLocations.MenuBar("build"),
            Debug = MenuLocations.MenuBar("debug"), Test = MenuLocations.MenuBar("test"), Analyze = MenuLocations.MenuBar("analyze"),
            Tools = MenuLocations.MenuBar("tools"), Window = MenuLocations.MenuBar("window"), Help = MenuLocations.MenuBar("help");

        // macOS apps start Edit with Undo, Redo and the clipboard; a classic IDE starts it with Go To and Find.
        string Undo = platform == Codalus.Platform.PlatformKind.MacOS ? "0a-undo" : "2-undo",
            Clipboard = platform == Codalus.Platform.PlatformKind.MacOS ? "0b-clipboard" : "3-clipboard";
        menus.Add(
        [
            // File. New ▸ holds New Project and New Solution (BuiltIns.Templates.cs).
            Sub(File, FileNewMenu, "New", "1-open"),
            Sub(File, FileOpenMenu, "Open", "1-open"),
            Item(FileOpenMenu, "workbench.openSolution", "1-open", "Project/Solution…"),
            Item(FileOpenMenu, "workbench.openFolder", "1-open", "Folder…"),
            Item(File, "git.clone", "1-open"),
            Item(File, "workbench.openRecent", "1-open"),
            Item(File, "workbench.showStartWindow", "1-open"),
            Item(File, "workbench.closeActiveDocument", "2-close"),
            Item(File, "workbench.closeSolution", "2-close"),
            Item(File, "file.save", "3-save"),
            Item(File, "file.saveAll", "3-save"),
            Item(File, "solution.saveFilter", "3-save"),
            Item(File, "workbench.action.localHistory", "4-history"),
            Item(File, "workbench.compareFiles", "4-history"),
            Item(File, "workbench.compareActiveWith", "4-history"),
            Sub(File, FileProfilesMenu, "Profiles", "5-profiles"),
            Item(FileProfilesMenu, "workbench.profiles.switch", "1-switch"),
            Item(FileProfilesMenu, "workbench.profiles.new", "2-manage"),
            Item(FileProfilesMenu, "workbench.profiles.delete", "2-manage"),
            Item(FileProfilesMenu, "workbench.profiles.export", "3-transfer"),
            Item(FileProfilesMenu, "workbench.profiles.import", "3-transfer"),
            Item(File, "workbench.restart", "exit"),
            Item(File, "workbench.quit", "exit"),

            // Edit.
            Sub(Edit, EditGoToMenu, "Go To", "1-go"),
            Sub(Edit, EditFindMenu, "Find and Replace", "1-go"),
            Item(Edit, "edit.undo", Undo, keys: EditKeys("edit.undo", platform)),
            Item(Edit, "edit.redo", Undo, keys: EditKeys("edit.redo", platform)),
            Item(Edit, "edit.cut", Clipboard, keys: EditKeys("edit.cut", platform)),
            Item(Edit, "edit.copy", Clipboard, keys: EditKeys("edit.copy", platform)),
            Item(Edit, "edit.paste", Clipboard, keys: EditKeys("edit.paste", platform)),
            Item(Edit, "editor.action.pasteFromClipboardRing", Clipboard, "Show Clipboard History"),
            Sub(Edit, EditPasteSpecialMenu, "Paste Special", Clipboard),
            Item(EditPasteSpecialMenu, "editor.action.pasteJsonAsClasses", "1-paste", "Paste JSON as Classes"),
            Item(EditPasteSpecialMenu, "editor.action.pasteXmlAsClasses", "1-paste", "Paste XML as Classes"),
            Item(Edit, "edit.selectAll", Clipboard, keys: EditKeys("edit.selectAll", platform)),
            Sub(Edit, EditAdvancedMenu, "Advanced", "4-more"),
            Sub(Edit, EditBookmarksMenu, "Bookmarks", "4-more"),
            Sub(Edit, EditIntelliSenseMenu, "IntelliSense", "4-more"),
            Sub(Edit, EditCaretsMenu, "Multiple Carets", "4-more"),
            Sub(Edit, EditOutliningMenu, "Outlining", "4-more"),
            Sub(Edit, EditRefactorMenu, "Refactor", "4-more"),

            Item(EditGoToMenu, "workbench.action.gotoAll", "1-all"),
            Item(EditGoToMenu, "workbench.action.gotoFile", "1-all"),
            Item(EditGoToMenu, "workbench.action.gotoType", "1-all"),
            Item(EditGoToMenu, "workbench.action.gotoMember", "1-all"),
            Item(EditGoToMenu, "workbench.action.gotoText", "1-all"),
            Item(EditGoToMenu, "editor.action.gotoLine", "1-all"),
            Item(EditGoToMenu, "workbench.action.navigateToLastEditLocation", "1-all"),
            Item(EditGoToMenu, "editor.action.revealDefinition", "2-symbol"),
            Item(EditGoToMenu, "editor.action.goToImplementation", "2-symbol"),
            Item(EditGoToMenu, "editor.action.peekDefinition", "2-symbol"),
            Item(EditGoToMenu, "editor.action.goToBase", "2-symbol"),
            Item(EditGoToMenu, "editor.action.referenceSearch", "3-references"),
            Item(EditGoToMenu, "editor.action.showCallHierarchy", "3-references"),
            Item(EditGoToMenu, "editor.action.showTypeHierarchy", "3-references"),
            Item(EditGoToMenu, "workbench.navigateBack", "4-history"),
            Item(EditGoToMenu, "workbench.navigateForward", "4-history"),
            Item(EditGoToMenu, "editor.action.marker.next", "5-problems"),
            Item(EditGoToMenu, "editor.action.marker.prev", "5-problems"),

            Item(EditFindMenu, "editor.action.find", "1-document", "Quick Find"),
            Item(EditFindMenu, "editor.action.replace", "1-document", "Quick Replace"),
            Item(EditFindMenu, "editor.action.findNext", "1-document"),
            Item(EditFindMenu, "editor.action.findPrevious", "1-document"),
            Item(EditFindMenu, "workbench.action.findInFiles", "2-files"),
            Item(EditFindMenu, "workbench.action.replaceInFiles", "2-files"),

            Item(EditAdvancedMenu, "editor.action.formatDocument", "1-format"),
            Item(EditAdvancedMenu, "editor.action.formatSelection", "1-format"),
            Item(EditAdvancedMenu, "editor.action.addCommentLine", "2-comment"),
            Item(EditAdvancedMenu, "editor.action.removeCommentLine", "2-comment"),
            Item(EditAdvancedMenu, "editor.action.commentLine", "2-comment"),
            Item(EditAdvancedMenu, "editor.action.duplicateSelection", "3-lines"),
            Item(EditAdvancedMenu, "editor.action.moveLinesUpAction", "3-lines"),
            Item(EditAdvancedMenu, "editor.action.moveLinesDownAction", "3-lines"),
            Item(EditAdvancedMenu, "editor.action.smartSelect.expand", "4-selection"),
            Item(EditAdvancedMenu, "editor.action.smartSelect.shrink", "4-selection"),
            Item(EditAdvancedMenu, "editor.action.changeEndOfLine", "5-file"),

            Item(EditBookmarksMenu, "bookmarks.toggle", "1-bookmarks"),
            Item(EditBookmarksMenu, "bookmarks.next", "1-bookmarks"),
            Item(EditBookmarksMenu, "bookmarks.previous", "1-bookmarks"),
            Item(EditBookmarksMenu, "bookmarks.clearAll", "1-bookmarks"),
            Item(EditBookmarksMenu, ViewCommandPrefix + "bookmarks", "2-window", "Bookmark Window"),

            Item(EditIntelliSenseMenu, "editor.action.triggerSuggest", "1-complete", "List Members (Trigger Suggest)"),
            Item(EditIntelliSenseMenu, "editor.action.triggerParameterHints", "1-complete"),
            Item(EditIntelliSenseMenu, "editor.action.showHover", "1-complete"),
            Item(EditIntelliSenseMenu, "editor.action.triggerWordCompletion", "1-complete"),
            Item(EditIntelliSenseMenu, "editor.action.insertSnippet", "2-snippets"),
            Item(EditIntelliSenseMenu, "editor.action.surroundWithSnippet", "2-snippets"),

            Item(EditCaretsMenu, "editor.action.insertCursorAbove", "1-carets"),
            Item(EditCaretsMenu, "editor.action.insertCursorBelow", "1-carets"),
            Item(EditCaretsMenu, "editor.action.addSelectionToNextFindMatch", "1-carets"),
            Item(EditCaretsMenu, "editor.action.selectHighlights", "1-carets"),

            Item(EditOutliningMenu, "editor.action.toggleFold", "1-outlining"),
            Item(EditOutliningMenu, "editor.action.foldAll", "1-outlining"),
            Item(EditOutliningMenu, "editor.action.unfoldAll", "1-outlining"),

            Item(EditRefactorMenu, "editor.action.rename", "1-refactor"),
            Item(EditRefactorMenu, "editor.action.quickFix", "1-refactor"),
            Item(EditRefactorMenu, "editor.action.codeCleanup", "2-cleanup"),

            // View: the main tool windows come from ViewMenuEntry (group "view", orders 0–100).
            Item(View, "workbench.showCommandPalette", "navigation"),
            Item(View, "terminal.new", "view", when: null) with { Order = 85 },
            MenuItemRule.ForSubmenu(View, OtherWindowsMenu, "Other Windows", shell) with { Group = "view", Order = 1000 },
            new MenuItemRule(OtherWindowsMenu, "workbench.view.objectBrowser", shell) { Group = "windows", Order = AlphabeticalOrder("Object Browser") },
            Item(View, "workbench.focusNextPane", "layout"),
            Item(View, "workbench.focusPreviousPane", "layout"),

            // Git.
            Item(Git, "git.commit", "1-commit"),
            Item(Git, ViewCommandPrefix + ViewModels.GitChangesViewModel.ToolWindowId, "1-commit", "Git Changes"),
            Item(Git, "git.history", "1-commit"),
            Item(Git, "git.fetch", "2-remote"),
            Item(Git, "git.pull", "2-remote"),
            Item(Git, "git.push", "2-remote"),
            Item(Git, "git.sync", "2-remote"),
            Item(Git, "git.remotes", "2-remote"),
            Sub(Git, GitBranchesMenu, "Branches", "3-more"),
            Sub(Git, GitStashMenu, "Stash", "3-more"),
            Sub(Git, GitFileMenu, "Current File", "3-more"),
            Sub(Git, GitHostingMenu, "Hosting", "3-more"),
            Sub(Git, GitRepositoryMenu, "Repository", "3-more"),
            Item(Git, "git.clone", "4-repository"),
            Item(Git, "git.init", "4-repository"),

            Item(GitBranchesMenu, "git.switchBranch", "1-branch"),
            Item(GitBranchesMenu, "git.createBranch", "1-branch"),
            Item(GitBranchesMenu, "git.updateBranch", "1-branch"),
            Item(GitBranchesMenu, "git.merge", "2-merge"),
            Item(GitBranchesMenu, "git.abortMerge", "2-merge"),
            Item(GitBranchesMenu, "git.rebase", "2-merge"),
            Item(GitBranchesMenu, "git.interactiveRebase", "2-merge"),
            Item(GitBranchesMenu, "git.continue", "3-operation", "Continue the Operation"),
            Item(GitBranchesMenu, "git.skip", "3-operation"),
            Item(GitBranchesMenu, "git.abort", "3-operation", "Abort the Operation"),
            Item(GitBranchesMenu, "git.tags", "4-tags"),

            Item(GitStashMenu, "git.stash", "1-stash"),
            Item(GitStashMenu, "git.stashes", "1-stash"),
            Item(GitStashMenu, "git.dropAllStashes", "1-stash"),

            Item(GitFileMenu, "git.showDiff", "1-diff"),
            Item(GitFileMenu, "git.compareWith", "1-diff"),
            Item(GitFileMenu, "git.stageFile", "2-stage"),
            Item(GitFileMenu, "git.toggleBlame", "3-history"),
            Item(GitFileMenu, "git.fileHistory", "3-history"),
            Item(GitFileMenu, "git.lineHistory", "3-history"),
            Item(GitFileMenu, "git.showCommit", "3-history"),

            Item(GitHostingMenu, "git.pullRequests", "1-pr"),
            Item(GitHostingMenu, "git.createPullRequest", "1-pr"),
            Item(GitHostingMenu, "git.workItems", "1-pr"),
            Item(GitHostingMenu, "git.publish", "2-publish"),
            Item(GitHostingMenu, "hosting.accounts", "3-accounts"),

            Item(GitRepositoryMenu, "git.switchRepository", "1-repository"),
            Item(GitRepositoryMenu, "git.refresh", "1-repository"),
            Item(GitRepositoryMenu, "git.worktrees", "2-worktrees"),
            Item(GitRepositoryMenu, "git.addWorktree", "2-worktrees"),
            Item(GitRepositoryMenu, "git.updateSubmodules", "3-content"),
            Item(GitRepositoryMenu, "git.lfsLocks", "3-content"),
            Item(GitRepositoryMenu, "git.lfsPull", "3-content"),

            // Project.
            Item(Project, "project.addNewItem", "1-add"),
            Item(Project, "project.addExistingItem", "1-add"),
            Item(Project, "project.addExistingItemAsLink", "1-add"),
            Item(Project, "web.addScaffoldedItem", "1-add"),
            Item(Project, "solution.addExistingProject", "1-add"),
            Item(Project, "solution.addFolder", "1-add"),
            Item(Project, "project.addReference", "2-references"),
            Item(Project, "project.references", "2-references"),
            Item(Project, "web.serviceReferences", "2-references"),
            Item(Project, "packages.manage", "3-packages"),
            Item(Project, "packages.manageSolution", "3-packages"),
            Sub(Project, ProjectPackagesMenu, "Packages and Tools", "3-packages"),
            Item(ProjectPackagesMenu, "solution.restore", "1-restore"),
            Item(ProjectPackagesMenu, "npm.restore", "1-restore"),
            Item(ProjectPackagesMenu, "packages.sources", "2-configure"),
            Item(ProjectPackagesMenu, "packages.signIn", "2-configure"),
            Item(ProjectPackagesMenu, "packages.tools", "2-configure"),
            Item(Project, "containers.addDockerSupport", "4-features"),
            Item(Project, "web.manageUserSecrets", "4-features"),
            Sub(Project, ProjectEntityFrameworkMenu, "Entity Framework", "4-features"),
            Item(ProjectEntityFrameworkMenu, "ef.show", "1-ef", "Entity Framework Window", "efProject"),
            Item(ProjectEntityFrameworkMenu, "ef.addMigration", "1-ef", when: "efProject"),
            Item(ProjectEntityFrameworkMenu, "ef.updateDatabase", "1-ef", when: "efProject"),
            Item(ProjectEntityFrameworkMenu, "ef.scaffold", "2-scaffold"),
            Sub(Project, ProjectSolutionMenu, "Solution", "5-solution"),
            Item(ProjectSolutionMenu, "solution.selectConfiguration", "1-solution"),
            Item(ProjectSolutionMenu, "solution.projectDependencies", "1-solution"),
            Item(ProjectSolutionMenu, "solution.sdks", "1-solution"),
            Item(ProjectSolutionMenu, "solution.trust", "2-trust"),
            Item(ProjectSolutionMenu, "folder.loadAllProjects", "3-folder"),
            Item(Project, "explorer.showAllFiles", "5-solution"),
            Item(Project, "project.exportTemplate", "5-solution"),
            Item(Project, "project.properties", "z-properties", "Project Properties"),

            // Build.
            Item(Build, "build.buildSolution", "1-solution"),
            Item(Build, "build.rebuildSolution", "1-solution"),
            Item(Build, "build.cleanSolution", "1-solution"),
            Item(Build, "build.buildProject", "2-project"),
            Item(Build, "build.rebuildProject", "2-project"),
            Item(Build, "build.cleanProject", "2-project"),
            Item(Build, "project.pack", "2-project", "Pack Project"),
            Item(Build, "project.publish", "2-project"),
            Item(Build, "build.cancel", "3-cancel"),
            Item(Build, "build.batchBuild", "4-configuration"),
            Item(Build, "solution.configurationManager", "4-configuration"),
            Item(Build, "solution.selectConfiguration", "4-configuration", "Configuration…"),
            Sub(Build, BuildDiagnosticsMenu, "Build Diagnostics", "5-more"),
            Item(BuildDiagnosticsMenu, "build.checkUpToDate", "1-check"),
            Item(BuildDiagnosticsMenu, "build.showTimings", "1-check"),
            Item(BuildDiagnosticsMenu, "build.buildWithBinaryLog", "2-logs"),
            Item(BuildDiagnosticsMenu, "build.openBinaryLog", "2-logs"),
            Item(BuildDiagnosticsMenu, "build.openBuildLog", "2-logs"),
            Sub(Build, BuildTasksMenu, "Tasks", "5-more"),
            Item(BuildTasksMenu, "tasks.run", "1-tasks"),
            Item(BuildTasksMenu, "tasks.configure", "1-tasks"),

            // Debug. Session commands show while a session runs, as a classic IDE's Debug menu.
            MenuItemRule.ForSubmenu(Debug, DebugWindowsMenu, "Windows", shell) with { Group = "0-windows", Order = 0 },
            Item(Debug, "debug.start", "1-start", when: "!debugActive"),
            Item(Debug, "debug.continue", "1-start", when: "debugActive", keys: "f5"),
            Item(Debug, "run.startWithoutDebugging", "1-start"),
            Item(Debug, "run.startWithHotReload", "1-start", when: "hotReloadSupported"),
            Item(Debug, "run.startWithHotReload", "1-start", "Start with Hot Reload (needs .NET 6 or later)", "!hotReloadSupported"),
            Item(Debug, "run.applyCodeChanges", "1-start"),
            Item(Debug, "debug.pause", "2-session", when: "debugActive"),
            Item(Debug, "debug.stop", "2-session", when: "debugActive"),
            Item(Debug, "debug.detachAll", "2-session", when: "debugActive"),
            Item(Debug, "debug.restart", "2-session", when: "debugActive"),
            Item(Debug, "run.stop", "2-session", when: "programRunning"),
            Item(Debug, "debug.attach", "3-attach"),
            Item(Debug, "debug.reattach", "3-attach"),
            Sub(Debug, DebugTargetsMenu, "Other Debug Targets", "3-attach"),
            Item(DebugTargetsMenu, "debug.attachRemote", "1-attach"),
            Item(DebugTargetsMenu, "containers.attach", "1-attach"),
            Item(DebugTargetsMenu, "containers.composeDebug", "2-launch"),
            Item(DebugTargetsMenu, "debug.webPage", "2-launch"),
            Item(Debug, "debug.stepInto", "4-step"),
            Item(Debug, "debug.stepOver", "4-step"),
            Item(Debug, "debug.stepOut", "4-step"),
            Item(Debug, "debug.runToCursor", "4-step"),
            Item(Debug, "debug.forceRunToCursor", "4-step"),
            Item(Debug, "debug.toggleBreakpoint", "5-breakpoints"),
            Item(Debug, "debug.toggleBreakpointEnabled", "5-breakpoints"),
            Item(Debug, "debug.deleteAllBreakpoints", "5-breakpoints"),
            Sub(Debug, DebugBreakpointsMenu, "Breakpoints", "5-breakpoints"),
            Item(DebugBreakpointsMenu, "debug.editBreakpoint", "1-edit"),
            Item(DebugBreakpointsMenu, "debug.newFunctionBreakpoint", "2-new"),
            Item(DebugBreakpointsMenu, "debug.insertTemporaryBreakpoint", "2-new"),
            Item(DebugBreakpointsMenu, "debug.exportBreakpoints", "3-files"),
            Item(DebugBreakpointsMenu, "debug.importBreakpoints", "3-files"),
            Item(Debug, "debug.quickWatch", "6-inspect"),
            Item(Debug, "debug.addWatch", "6-inspect"),
            Item(Debug, "debug.clearDataTips", "6-inspect"),
            Item(Debug, "debug.exceptionSettings", "6-inspect", "Exception Settings…"),
            Sub(Debug, DebugStartupMenu, "Startup and Launch", "7-more"),
            Item(DebugStartupMenu, "run.selectStartupProject", "1-startup"),
            Item(DebugStartupMenu, "run.configureStartupProjects", "1-startup"),
            Item(DebugStartupMenu, "run.selectLaunchProfile", "2-launch"),
            Item(DebugStartupMenu, "run.selectLaunchConfiguration", "2-launch"),
            Item(DebugStartupMenu, "launch.configure", "2-launch"),
            Sub(Debug, DebugWebMenu, "ASP.NET Core and Aspire", "7-more"),
            Item(DebugWebMenu, "web.selectEnvironment", "1-environment"),
            Item(DebugWebMenu, "web.checkDevCertificate", "2-certificate"),
            Item(DebugWebMenu, "web.trustDevCertificate", "2-certificate"),
            Item(DebugWebMenu, "aspire.openDashboard", "3-aspire"),
            Sub(Debug, DebugDumpsMenu, "Dumps", "7-more"),
            Item(DebugDumpsMenu, "debug.openDump", "1-dumps"),
            Item(DebugDumpsMenu, "debug.createDump", "1-dumps"),
            Item(DebugDumpsMenu, "debug.justInTime", "2-jit"),
            Item(Debug, "profiler.show", "8-profiler"),
            Sub(Debug, DebugProfilerMenu, "Profiler", "8-profiler"),
            Item(DebugProfilerMenu, "profiler.stop", "1-session", when: "profilerCollecting"),
            Item(DebugProfilerMenu, "profiler.open", "1-session"),
            Item(DebugProfilerMenu, "profiler.toggleHotLines", "2-editor"),

            // Test. Create Unit Tests comes from the dotnettest plugin.
            Item(Test, "testing.runAll", "1-run"),
            Item(Test, "testing.debugAll", "1-run"),
            Item(Test, "testing.coverAll", "1-run"),
            Item(Test, "testing.runFailed", "1-run"),
            Item(Test, "testing.runNotRun", "1-run"),
            Item(Test, "testing.repeatLast", "1-run"),
            Item(Test, "testing.cancel", "1-run"),
            Item(Test, ViewCommandPrefix + ViewModels.TestExplorerViewModel.ToolWindowId, "2-explorer", "Test Explorer"),
            Item(Test, "testing.refresh", "2-explorer"),
            Item(Test, "testing.nextFailure", "2-explorer"),
            Item(Test, "testing.previousFailure", "2-explorer"),
            Sub(Test, TestPlaylistsMenu, "Playlists", "3-more"),
            Item(TestPlaylistsMenu, "testing.newPlaylist", "1-playlists"),
            Item(TestPlaylistsMenu, "testing.importPlaylist", "1-playlists"),
            Item(TestPlaylistsMenu, "testing.deletePlaylist", "1-playlists"),
            Sub(Test, TestResultsMenu, "Test Results", "3-more"),
            Item(TestResultsMenu, "testing.exportTrx", "1-trx"),
            Item(TestResultsMenu, "testing.importTrx", "1-trx"),
            Sub(Test, TestCoverageMenu, "Code Coverage", "3-more"),
            Item(TestCoverageMenu, ViewCommandPrefix + ViewModels.CoverageResultsViewModel.ToolWindowId, "1-results", "Coverage Results"),
            Item(TestCoverageMenu, "testing.toggleCoverageColoring", "1-results"),
            Item(TestCoverageMenu, "testing.exportCoverage", "2-files"),
            Item(TestCoverageMenu, "testing.clearCoverage", "2-files"),

            // Analyze.
            Sub(Analyze, AnalyzeMetricsMenu, "Calculate Code Metrics", "1-metrics"),
            Item(AnalyzeMetricsMenu, "analysis.codeMetrics.solution", "1-metrics", "For Solution"),
            Item(AnalyzeMetricsMenu, "analysis.codeMetrics.project", "1-metrics", "For Project"),
            Item(Analyze, "editor.action.codeCleanup", "2-cleanup"),
            Item(Analyze, "analysis.syntaxVisualizer", "3-roslyn"),

            // Tools. External tools are added to the "external" group as they're configured.
            Sub(Tools, ToolsDataMenu, "Database", "1-data"),
            Item(ToolsDataMenu, "data.explorer", "1-data"),
            Item(ToolsDataMenu, "data.addConnection", "1-data"),
            Item(ToolsDataMenu, "data.newQuery", "1-data"),
            Item(Tools, "tools.templates", "2-templates"),
            Item(Tools, "snippets.manager", "2-templates"),
            Item(Tools, "tools.externalTools", "3-external-config"),
            Item(Tools, "workbench.openPluginsFolder", "plugins") with { Order = 100 },
            Item(Tools, "workbench.selectTheme", "z-preferences"),
            Item(Tools, "workbench.openKeyboardShortcuts", "z-preferences"),
            Item(Tools, "workbench.openFontsAndColors", "z-preferences"),
            Item(Tools, "workbench.customize", "z-preferences"),
            Item(Tools, "workbench.importSettings", "z-preferences"),
            Item(Tools, "workbench.exportSettings", "z-preferences"),
            Item(Tools, "workbench.importVsCodeSettings", "z-preferences"),
            Item(Tools, "settingsSync.syncNow", "z-preferences"),
            Item(Tools, "workbench.importVsSettings", "z-preferences"),
            Item(Tools, "workbench.openOptions", "z-preferences"),

            // Window.
            Item(Window, "workbench.toggleEditorSplit", "1-split"),
            Item(Window, "workbench.splitEditorRight", "1-split"),
            Item(Window, "workbench.splitEditorDown", "1-split"),
            Item(Window, "workbench.keepEditor", "2-documents"),
            Item(Window, "workbench.pinEditor", "2-documents"),
            Item(Window, "workbench.showOpenDocuments", "2-documents"),
            Item(Window, "workbench.switchDocument", "2-documents"),
            Item(Window, "workbench.nextDocument", "2-documents"),
            Item(Window, "workbench.previousDocument", "2-documents"),
            Item(Window, "workbench.closeOtherDocuments", "3-close"),
            Item(Window, "workbench.closeAllDocuments", "3-close"),
            Item(Window, "workbench.closeUnpinnedDocuments", "3-close"),
            Item(Window, "workbench.closeDocumentsToTheRight", "3-close"),
            Item(Window, "workbench.saveWindowLayout", "4-layout"),
            Item(Window, "workbench.applyWindowLayout", "4-layout"),
            Item(Window, "workbench.deleteWindowLayout", "4-layout"),
            Item(Window, "workbench.resetLayout", "4-layout"),
            Item(Window, "workbench.toggleFullScreen", "5-screen"),
            Item(Window, "workbench.toggleDistractionFree", "5-screen"),

            // Help. The Start page comes from the welcome plugin ("navigation").
            Item(Help, "workbench.showLogs", "help"),
            Item(Help, "help.setUp", "help"),
            Item(Help, "help.checkEnvironment", "help"),
            Item(Help, "help.openCrashReports", "help"),
            Item(Help, "help.about", "help"),
            Item(Help, "update.check", "updates"),
            Item(Help, "update.whatsNew", "updates"),
        ]);
    }

    /// <summary>
    /// A document tab's context menu (M34e), instead of Dock's: closing, keeping and pinning, the file's path, splitting.
    /// Right-clicking a tab makes it the active document first, so these act on it.
    /// </summary>
    public static void RegisterDocumentTabContextMenu(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        var order = 0;
        const string Tab = MenuLocations.DocumentTabContext;
        MenuItemRule Item(string command, string group) => new(Tab, command, shell) { Group = group, Order = ++order };
        menus.Add(
        [
            Item("workbench.closeActiveDocument", "1-close"),
            Item("workbench.closeOtherDocuments", "1-close"),
            Item("workbench.closeDocumentsToTheRight", "1-close"),
            Item("workbench.closeUnpinnedDocuments", "1-close"),
            Item("workbench.closeAllDocuments", "1-close"),
            Item("workbench.pinEditor", "2-keep"),
            Item("workbench.keepEditor", "2-keep"),
            Item("workbench.copyDocumentPath", "3-path"),
            Item("workbench.revealDocument", "3-path"),
            Item("workbench.splitEditorRight", "4-split"),
            Item("workbench.splitEditorDown", "4-split"),
        ]);
    }

    /// <summary>
    /// The code editor's context menu (M34a): refactoring first, then navigation, tests, debugging, then submenus.
    /// Language-specific entries (.http, SQL, XAML) are contributed by their features.
    /// </summary>
    public static void RegisterEditorContextMenu(MenuRegistry menus, Codalus.Platform.PlatformKind platform)
    {
        var shell = ContributionSource.Shell;
        var order = 0;
        const string Context = MenuLocations.EditorContext;
        MenuItemRule Item(string location, string command, string group, string? title = null, string? when = null, string? keys = null) =>
            new(location, command, shell) { Group = group, Order = ++order, Title = title, When = WhenClause.Parse(when), Keys = keys };
        MenuItemRule Sub(string location, string submenu, string title, string group, string? when = null) =>
            MenuItemRule.ForSubmenu(location, submenu, title, shell) with { Group = group, Order = ++order, When = WhenClause.Parse(when) };

        menus.Add(
        [
            Item(Context, "editor.action.quickFix", "1-refactor"),
            Item(Context, "editor.action.rename", "1-refactor"),
            Item(Context, "editor.action.formatDocument", "1-refactor"),
            Item(Context, "editor.action.peekDefinition", "2-navigation"),
            Item(Context, "editor.action.revealDefinition", "2-navigation"),
            Item(Context, "editor.action.goToBase", "2-navigation", when: "editorLanguageId == csharp"),
            Item(Context, "editor.action.goToImplementation", "2-navigation"),
            Item(Context, "editor.action.referenceSearch", "2-navigation"),
            Item(Context, "editor.action.showCallHierarchy", "2-navigation", when: "editorLanguageId == csharp"),
            Item(Context, "testing.runAtCursor", "3-tests", "Run Tests"),
            Item(Context, "testing.debugAtCursor", "3-tests", "Debug Tests"),
            Item(Context, "testing.coverAtCursor", "3-tests", "Run Tests with Coverage"),
            Sub(Context, EditorBreakpointMenu, "Breakpoint", "4-debug"),
            Item(EditorBreakpointMenu, "debug.toggleBreakpoint", "1-breakpoint", "Insert or Remove Breakpoint"),
            Item(EditorBreakpointMenu, "debug.insertTemporaryBreakpoint", "1-breakpoint"),
            Item(EditorBreakpointMenu, "debug.toggleBreakpointEnabled", "1-breakpoint"),
            Item(Context, "debug.runToCursor", "4-debug", when: "debugActive"),
            Item(Context, "interactive.sendSelection", "5-send", when: "editorLanguageId != fsharp"),
            Item(Context, "fsi.sendSelection", "5-send", when: "editorLanguageId == fsharp"),
            Item(Context, "fsi.sendLine", "5-send", when: "editorLanguageId == fsharp"),
            Item(Context, "fsi.runScript", "5-send", when: "editorExtension == .fsx"),
            Item(Context, "terminal.runSelectedText", "5-send"),
            Sub(Context, EditorSnippetsMenu, "Snippet", "6-more"),
            Item(EditorSnippetsMenu, "editor.action.insertSnippet", "1-snippets"),
            Item(EditorSnippetsMenu, "editor.action.surroundWithSnippet", "1-snippets"),
            Sub(Context, EditorGitMenu, "Git", "6-more", "gitRepository"),
            Item(EditorGitMenu, "git.lineHistory", "1-history", when: "gitRepository"),
            Item(EditorGitMenu, "git.showCommit", "1-history", when: "gitRepository"),
            Item(EditorGitMenu, "git.compareWith", "2-compare", when: "gitRepository"),
            Sub(Context, EditorOutliningMenu, "Outlining", "6-more"),
            Item(EditorOutliningMenu, "editor.action.toggleFold", "1-outlining"),
            Item(EditorOutliningMenu, "editor.action.foldAll", "1-outlining"),
            Item(EditorOutliningMenu, "editor.action.unfoldAll", "1-outlining"),
            Item(Context, "edit.cut", "7-clipboard", keys: EditKeys("edit.cut", platform)),
            Item(Context, "edit.copy", "7-clipboard", keys: EditKeys("edit.copy", platform)),
            Item(Context, "edit.paste", "7-clipboard", keys: EditKeys("edit.paste", platform)),

            // The Solution Explorer's Git ▸ (its other entries live with their features).
            MenuItemRule.ForSubmenu(SolutionExplorerView.ContextMenuLocation, SolutionExplorerView.GitMenuLocation, "Git", shell) with { Group = "6-more", Order = 1 },
            Item(SolutionExplorerView.GitMenuLocation, "git.fileHistory", "1-history", "View History", "gitRepository && explorerItemKind != solution"),
            Item(SolutionExplorerView.GitMenuLocation, "explorer.addToGitignore", "2-ignore", when: "gitRepository && explorerItemKind != solution"),
            Item(SolutionExplorerView.GitMenuLocation, "git.lfsLock", "3-lfs", when: "gitLfs && explorerItemKind == file"),
            Item(SolutionExplorerView.GitMenuLocation, "git.lfsUnlock", "3-lfs", when: "gitLfs && explorerItemKind == file"),
        ]);
    }

    /// <summary>
    /// Edit → Undo, Redo, Cut, Copy, Paste and Select All (M34a): they act on the focused text box or the active editor.
    /// Their keys stay with the controls themselves (multi-caret paste, text boxes); the menus only show them.
    /// </summary>
    public static void RegisterEditCommands(ShellHost shell)
    {
        var platform = shell.Workbench.Platform;
        void Add(string id, string title, Action<Views.EditTarget> action) =>
            shell.Workbench.Commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Edit" },
                Ui(() =>
                {
                    if (Views.EditTarget.Current(shell) is { } target)
                    {
                        action(target);
                        return;
                    }

                    // Not a text control (the Solution Explorer's Cut, Copy and Paste…): what the same keys do there.
                    var chord = Codalus.Platform.KeySequence.Parse(EditKeys(id, platform), platform).Chords[0];
                    if (shell.Workbench.Keybindings.Peek(chord) is { Kind: Workbench.Keybindings.KeyDispatchKind.Command, Rule: { } rule } && !rule.Command.StartsWith("edit.", StringComparison.Ordinal))
                    {
                        shell.Run(rule.Command);
                    }
                }));

        Add("edit.undo", "Undo", t => t.Undo());
        Add("edit.redo", "Redo", t => t.Redo());
        Add("edit.cut", "Cut", t => t.Cut());
        Add("edit.copy", "Copy", t => t.Copy());
        Add("edit.paste", "Paste", t => t.Paste());
        Add("edit.selectAll", "Select All", t => t.SelectAll());
    }

    /// <summary>The keys the text controls give Edit's commands on <paramref name="platform"/> (shown in the menus).</summary>
    public static string EditKeys(string command, Codalus.Platform.PlatformKind platform) => command switch
    {
        "edit.undo" => "primary+z",
        "edit.redo" => platform == Codalus.Platform.PlatformKind.MacOS ? "primary+shift+z" : "primary+y",
        "edit.cut" => "primary+x",
        "edit.copy" => "primary+c",
        "edit.paste" => "primary+v",
        "edit.selectAll" => "primary+a",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
