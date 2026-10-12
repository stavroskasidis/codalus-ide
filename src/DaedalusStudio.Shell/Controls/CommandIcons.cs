namespace DaedalusStudio.Shell.Controls;

/// <summary>An icon for a command: its name in <see cref="IconSet"/> and the command family that colors it.</summary>
public readonly record struct CommandIcon(string Kind, string? Accent = null)
{
    /// <summary>The icon's style class (<c>accent-run</c>…), or null for a monochrome icon.</summary>
    public string? AccentClass => Accent is null ? null : "accent-" + Accent;
}

/// <summary>
/// The icons that menus and toolbars show for built-in commands (M34a, D24), where a classic IDE shows one. Command
/// families color them: run green, stop red, save and navigation blue, open amber, build purple, Git orange, tests
/// teal (theme tokens <c>Shell.Icon.*</c>, all monochrome in High Contrast). Tool windows' <c>workbench.view.*</c>
/// commands use the window's own icon.
/// </summary>
public static class CommandIcons
{
    public const string Run = "run";
    public const string Stop = "stop";
    public const string Save = "save";
    public const string Open = "open";
    public const string Build = "build";
    public const string Git = "git";
    public const string Test = "test";
    public const string Navigate = "navigate";

    private static readonly Dictionary<string, CommandIcon> s_icons = new(StringComparer.Ordinal)
    {
        // File.
        ["templates.newProject"] = new("new"),
        ["templates.newSolution"] = new("new-solution"),
        ["workbench.openSolution"] = new("folder-open", Open),
        ["workbench.openFolder"] = new("folder", Open),
        ["workbench.openRecent"] = new("history"),
        ["git.clone"] = new("fork", Git),
        ["workbench.closeActiveDocument"] = new("close"),
        ["workbench.closeSolution"] = new("close"),
        ["file.save"] = new("save", Save),
        ["file.saveAll"] = new("save-all", Save),
        ["workbench.action.localHistory"] = new("history"),
        ["workbench.compareFiles"] = new("compare"),
        ["workbench.compareActiveWith"] = new("compare"),

        // Edit.
        ["edit.undo"] = new("undo"),
        ["edit.redo"] = new("redo"),
        ["edit.cut"] = new("cut"),
        ["edit.copy"] = new("copy"),
        ["edit.paste"] = new("paste"),
        ["editor.action.pasteFromClipboardRing"] = new("paste"),
        ["editor.action.pasteJsonAsClasses"] = new("braces"),
        ["editor.action.find"] = new("search"),
        ["editor.action.replace"] = new("replace"),
        ["workbench.action.findInFiles"] = new("find-in-files"),
        ["workbench.action.gotoAll"] = new("go-to", Navigate),
        ["editor.action.gotoLine"] = new("go-to-line"),
        ["editor.action.revealDefinition"] = new("definition", Navigate),
        ["editor.action.peekDefinition"] = new("definition"),
        ["editor.action.referenceSearch"] = new("references"),
        ["editor.action.showCallHierarchy"] = new("call-hierarchy"),
        ["editor.action.showTypeHierarchy"] = new("type-hierarchy"),
        ["workbench.navigateBack"] = new("arrow-left", Navigate),
        ["workbench.navigateForward"] = new("arrow-right", Navigate),
        ["editor.action.marker.next"] = new("error"),
        ["editor.action.formatDocument"] = new("format"),
        ["editor.action.formatSelection"] = new("format"),
        ["editor.action.addCommentLine"] = new("comment"),
        ["editor.action.removeCommentLine"] = new("uncomment"),
        ["editor.action.duplicateSelection"] = new("duplicate"),
        ["editor.action.moveLinesUpAction"] = new("arrow-up"),
        ["editor.action.moveLinesDownAction"] = new("arrow-down"),
        ["editor.action.smartSelect.expand"] = new("select-expand"),
        ["bookmarks.toggle"] = new("bookmark", Navigate),
        ["workbench.view.bookmarks"] = new("bookmarks"),
        ["editor.action.triggerSuggest"] = new("list"),
        ["editor.action.showHover"] = new("info"),
        ["editor.action.insertSnippet"] = new("snippet"),
        ["editor.action.surroundWithSnippet"] = new("snippet"),
        ["editor.action.insertCursorBelow"] = new("caret"),
        ["editor.action.toggleFold"] = new("fold"),
        ["editor.action.foldAll"] = new("fold-all"),
        ["editor.action.unfoldAll"] = new("unfold-all"),
        ["editor.action.rename"] = new("rename"),
        ["editor.action.quickFix"] = new("lightbulb"),
        ["editor.action.codeCleanup"] = new("broom"),

        // View and Window.
        ["workbench.showCommandPalette"] = new("search"),
        ["terminal.new"] = new("terminal"),
        ["workbench.toggleEditorSplit"] = new("split-down"),
        ["workbench.splitEditorRight"] = new("split-right"),
        ["workbench.splitEditorDown"] = new("split-down"),
        ["workbench.nextDocument"] = new("tab"),
        ["workbench.closeAllDocuments"] = new("close-all"),
        ["workbench.resetLayout"] = new("layout"),

        // Git.
        ["git.commit"] = new("check", Git),
        ["git.history"] = new("history", Git),
        ["git.fetch"] = new("sync", Git),
        ["git.pull"] = new("download", Git),
        ["git.push"] = new("upload", Git),
        ["git.switchBranch"] = new("branch", Git),
        ["git.createBranch"] = new("branch", Git),
        ["git.merge"] = new("merge", Git),
        ["git.stash"] = new("stash", Git),
        ["git.tags"] = new("tag"),
        ["git.showDiff"] = new("compare"),
        ["git.compareWith"] = new("compare"),
        ["git.fileHistory"] = new("history"),
        ["git.lineHistory"] = new("history"),
        ["git.pullRequests"] = new("pull-request", Git),
        ["git.createPullRequest"] = new("pull-request", Git),
        ["git.workItems"] = new("task-list"),
        ["git.publish"] = new("publish", Git),
        ["hosting.accounts"] = new("account"),
        ["git.refresh"] = new("refresh"),
        ["git.lfsLocks"] = new("lock"),
        ["git.lfsLock"] = new("lock"),

        // Project.
        ["project.addNewItem"] = new("file-add"),
        ["project.addExistingItem"] = new("file"),
        ["project.addReference"] = new("link-add"),
        ["project.references"] = new("link"),
        ["packages.manage"] = new("package"),
        ["packages.manageSolution"] = new("package"),
        ["solution.restore"] = new("download"),
        ["containers.addDockerSupport"] = new("container"),
        ["web.manageUserSecrets"] = new("key"),
        ["ef.show"] = new("database"),
        ["explorer.showAllFiles"] = new("files"),
        ["project.exportTemplate"] = new("templates"),
        ["project.properties"] = new("tools"),
        ["workbench.view.properties"] = new("tools"),

        // Build.
        ["build.buildSolution"] = new("build", Build),
        ["build.rebuildSolution"] = new("refresh", Build),
        ["build.cleanSolution"] = new("broom"),
        ["build.buildProject"] = new("build", Build),
        ["build.rebuildProject"] = new("refresh", Build),
        ["build.cleanProject"] = new("broom"),
        ["project.pack"] = new("package"),
        ["project.publish"] = new("publish"),
        ["build.cancel"] = new("cancel", Stop),
        ["build.batchBuild"] = new("layers"),
        ["solution.configurationManager"] = new("gear"),
        ["build.checkUpToDate"] = new("check-circle"),
        ["build.showTimings"] = new("timer"),
        ["build.openBinaryLog"] = new("file-data"),
        ["build.openBuildLog"] = new("document-text"),
        ["tasks.run"] = new("play-outline"),

        // Debug.
        ["debug.start"] = new("play", Run),
        ["debug.continue"] = new("play", Run),
        ["run.startWithoutDebugging"] = new("play-outline", Run),
        ["run.startWithHotReload"] = new("flame"),
        ["run.applyCodeChanges"] = new("flame"),
        ["debug.pause"] = new("pause", Navigate),
        ["debug.stop"] = new("stop", Stop),
        ["run.stop"] = new("stop", Stop),
        ["debug.restart"] = new("restart", Navigate),
        ["debug.attach"] = new("attach"),
        ["debug.reattach"] = new("attach"),
        ["debug.attachRemote"] = new("globe"),
        ["containers.attach"] = new("container"),
        ["containers.composeDebug"] = new("container"),
        ["debug.webPage"] = new("globe"),
        ["debug.stepInto"] = new("step-into", Navigate),
        ["debug.stepOver"] = new("step-over", Navigate),
        ["debug.stepOut"] = new("step-out", Navigate),
        ["debug.runToCursor"] = new("caret"),
        ["debug.toggleBreakpoint"] = new("breakpoint", Stop),
        ["debug.toggleBreakpointEnabled"] = new("breakpoint-outline", Stop),
        ["debug.deleteAllBreakpoints"] = new("cancel"),
        ["debug.quickWatch"] = new("watch"),
        ["debug.addWatch"] = new("watch"),
        ["debug.exceptionSettings"] = new("warning"),
        ["debug.openDump"] = new("file-data"),
        ["web.checkDevCertificate"] = new("shield"),
        ["aspire.openDashboard"] = new("gauge"),
        ["profiler.show"] = new("gauge"),
        ["profiler.stop"] = new("stop", Stop),

        // Test.
        ["testing.runAll"] = new("play", Test),
        ["testing.debugAll"] = new("debug", Test),
        ["testing.coverAll"] = new("coverage", Test),
        ["testing.runFailed"] = new("play", Test),
        ["testing.repeatLast"] = new("restart", Test),
        ["testing.cancel"] = new("stop", Stop),
        ["workbench.view.testExplorer"] = new("beaker", Test),
        ["testing.refresh"] = new("refresh"),
        ["workbench.view.coverageResults"] = new("coverage", Test),
        ["testing.runAtCursor"] = new("play", Test),
        ["testing.debugAtCursor"] = new("debug", Test),
        ["testing.coverAtCursor"] = new("coverage", Test),
        ["dotnettest.createUnitTests"] = new("beaker", Test),

        // Analyze, Tools, Help.
        ["analysis.codeMetrics.solution"] = new("metrics"),
        ["analysis.syntaxVisualizer"] = new("type-hierarchy"),
        ["data.explorer"] = new("database"),
        ["data.addConnection"] = new("link-add"),
        ["data.newQuery"] = new("file-add"),
        ["tools.templates"] = new("templates"),
        ["tools.externalTools"] = new("tools"),
        ["workbench.openPluginsFolder"] = new("extensions"),
        ["workbench.selectTheme"] = new("color"),
        ["workbench.openKeyboardShortcuts"] = new("keyboard"),
        ["workbench.openOptions"] = new("gear"),
        ["welcome.show"] = new("home"),
        ["workbench.showLogs"] = new("document-text"),
        ["help.about"] = new("info"),
        ["update.check"] = new("update"),
        ["update.whatsNew"] = new("sparkle"),

        // Solution Explorer.
        ["explorer.newFile"] = new("file-add"),
        ["explorer.newFolder"] = new("folder"),
        ["explorer.cut"] = new("cut"),
        ["explorer.copy"] = new("copy"),
        ["explorer.paste"] = new("paste"),
        ["explorer.rename"] = new("rename"),
        ["explorer.delete"] = new("trash"),
        ["explorer.copyPath"] = new("copy"),
        ["explorer.reveal"] = new("folder-open", Open),
        ["terminal.openInTerminal"] = new("terminal"),
        ["run.setStartupProject"] = new("play-outline", Run),
    };

    /// <summary>The icon for <paramref name="command"/>, or null. <paramref name="toolWindowIcon"/> answers <c>workbench.view.*</c>.</summary>
    public static CommandIcon? For(string command, Func<string, string?>? toolWindowIcon = null)
    {
        if (s_icons.TryGetValue(command, out var icon))
        {
            return icon;
        }

        if (toolWindowIcon is not null && command.StartsWith(BuiltIns.ViewCommandPrefix, StringComparison.Ordinal)
            && toolWindowIcon(command[BuiltIns.ViewCommandPrefix.Length..]) is { } kind && (IconSet.GetFilled(kind) is not null || IconSet.Get(kind) is not null))
        {
            return new CommandIcon(kind);
        }

        return null;
    }

    /// <summary>Every mapped command (tests).</summary>
    public static IReadOnlyDictionary<string, CommandIcon> All => s_icons;

    /// <summary>A 16×16 icon control for a command, colored by its family; null when the command has none.</summary>
    public static Icon? Create(string command, Func<string, string?>? toolWindowIcon = null)
    {
        if (For(command, toolWindowIcon) is not { } icon)
        {
            return null;
        }

        var control = new Icon { Kind = icon.Kind, Width = 16, Height = 16 };
        if (icon.AccentClass is { } accent)
        {
            control.Classes.Add(accent);
        }

        return control;
    }
}
