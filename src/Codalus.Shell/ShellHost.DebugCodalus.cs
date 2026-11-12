using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

// What the Codalus debugger does that netcoredbg doesn't (M58, ADR 0094), as commands: Set Next Statement and Step Into
// Specific. Each is offered by what the stopped thread's debugger says it can do (the context keys below), so under
// netcoredbg, or in the browser, they're off.
public sealed partial class ShellHost
{
    /// <summary>
    /// The Codalus debugger's bundles this install offers as components (M58b, ADR 0094): for x64 programs on Apple
    /// silicon, and for 32-bit .NET Framework programs on Windows.
    /// </summary>
    public Codalus.Workbench.Debugging.DebuggerComponents DebuggerBundles { get; private set; } = null!;

    /// <summary>
    /// Offers the debug service the bundles that fit this machine: each is downloaded the first time such a program is
    /// debugged, after the user agrees, and used from the cache after that.
    /// </summary>
    private void InitializeDebuggerBundles(string cache)
    {
        DebuggerBundles = new Codalus.Workbench.Debugging.DebuggerComponents(cache);
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            && DebuggerBundles.Offers(Codalus.Workbench.Debugging.DebuggerComponents.X64OnAppleSilicon))
        {
            Debugger.X64CoreClrAdapter = ct => DebuggerBundleAsync(
                Codalus.Workbench.Debugging.DebuggerComponents.X64OnAppleSilicon,
                Codalus.Debugging.CoreClrAdapter.AdapterArgument,
                codalus: true,
                Localizer.Get("msg.debugBundles.x64Programs", "x64 programs (.NET 5 and earlier, which run under Rosetta 2)"),
                ct);
        }

        if (OperatingSystem.IsWindows() && DebuggerBundles.Offers(Codalus.Workbench.Debugging.DebuggerComponents.X86OnWindows))
        {
            Debugger.X86DesktopClrAdapter = ct => DebuggerBundleAsync(
                Codalus.Workbench.Debugging.DebuggerComponents.X86OnWindows,
                Codalus.Debugging.DesktopClr.AdapterArgument,
                codalus: false,
                Localizer.Get("msg.debugBundles.x86Programs", "32-bit .NET Framework programs"),
                ct);
        }
    }

    /// <summary>
    /// The debugger of <paramref name="platform"/>'s bundle, as an adapter to start: downloaded first when it isn't here,
    /// after the user agrees (the question names its size). Null when they declined or it couldn't be had (reported).
    /// </summary>
    private async Task<Codalus.Debugging.DebugAdapterStartInfo?> DebuggerBundleAsync(string platform, string argument, bool codalus, string programs, CancellationToken cancellationToken)
    {
        try
        {
            if (DebuggerBundles.Installed(platform) is not { } path)
            {
                var component = DebuggerBundles.Available.First(c => c.Platform == platform);
                var agreed = false;
                await Ui.RunAsync(async () =>
                {
                    agreed = await Dialogs.ConfirmAsync(
                        Localizer.Get("msg.debugBundles.downloadTheDebugger", "Download the Debugger"),
                        Localizer.Format("msg.debugBundles.question", "Debugging {0} needs another build of the Codalus debugger ({1} MB, MIT), which this installation downloads from its release once. Download it now?", programs, Math.Max(1, component.Size / (1024 * 1024))),
                        Localizer.Get("msg.debugBundles.download", "Download")).ConfigureAwait(true);
                }).ConfigureAwait(false);
                if (!agreed)
                {
                    return null;
                }

                path = await DebuggerBundles.EnsureAsync(platform, new Progress<string>(line => DebugLog.AppendLine(line)), cancellationToken).ConfigureAwait(false);
            }

            return new Codalus.Debugging.DebugAdapterStartInfo(path, [argument], Path.GetDirectoryName(path)) { IsCodalus = codalus };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.debugBundles.couldNotDownload", "The debugger for {0} couldn't be downloaded: {1}", programs, ex.Message));
            return null;
        }
    }

    public const string SymbolsMicrosoftServerSetting = "debug.symbols.microsoftServer";
    public const string SymbolsNuGetServerSetting = "debug.symbols.nugetServer";
    public const string SymbolsServersSetting = "debug.symbols.servers";
    public const string SymbolsPathsSetting = "debug.symbols.paths";
    public const string SymbolsCacheSetting = "debug.symbols.cacheFolder";
    public const string SymbolsOnlySpecifiedSetting = "debug.symbols.loadOnlySpecified";
    public const string SymbolsIncludeSetting = "debug.symbols.include";
    public const string SymbolsExcludeSetting = "debug.symbols.exclude";

    /// <summary>
    /// The symbol settings (Options › Debugging › Symbols) as the Codalus debugger takes them at a session's start:
    /// the servers that are turned on (none by default: nothing leaves the machine until one is), the symbol paths and
    /// the cache; null when nothing is listed.
    /// </summary>
    internal Codalus.Debugging.Symbols.SymbolOptions? SymbolOptions()
    {
        var settings = Workbench.Settings;
        var servers = new List<string>();
        if (settings.GetBoolean(SymbolsMicrosoftServerSetting, false))
        {
            servers.Add(Codalus.Debugging.Symbols.SymbolOptions.MicrosoftServer);
        }

        if (settings.GetBoolean(SymbolsNuGetServerSetting, false))
        {
            servers.Add(Codalus.Debugging.Symbols.SymbolOptions.NuGetServer);
        }

        servers.AddRange(settings.GetStrings(SymbolsServersSetting).Select(s => s.Trim()).Where(s => s.Length > 0));
        var paths = settings.GetStrings(SymbolsPathsSetting).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (servers.Count == 0 && paths.Count == 0)
        {
            return null;
        }

        var cache = settings.GetString(SymbolsCacheSetting, string.Empty).Trim();
        return new Codalus.Debugging.Symbols.SymbolOptions
        {
            Servers = servers,
            SearchPaths = paths,
            CacheFolder = cache.Length > 0 ? cache : Path.Combine(Paths.Cache, "symbols"),
            ModuleFilter = settings.GetBoolean(SymbolsOnlySpecifiedSetting, false) ? Codalus.Debugging.Symbols.SymbolModuleFilter.OnlySpecified : Codalus.Debugging.Symbols.SymbolModuleFilter.AllModules,
            IncludedModules = [.. settings.GetStrings(SymbolsIncludeSetting)],
            ExcludedModules = [.. settings.GetStrings(SymbolsExcludeSetting)],
        };
    }

    /// <summary>The context keys of what the current debugger can do: set at every change of the debug state.</summary>
    private void UpdateDebuggerAbilities()
    {
        Workbench.Context.Set("debugCanSetNextStatement", Debugger.CanSetNextStatement);
        Workbench.Context.Set("debugCanStepIntoSpecific", Debugger.CanStepIntoSpecific);
        Workbench.Context.Set("debugCanFreezeThreads", Debugger.CanFreezeThreads);
    }

    /// <summary>Ctrl+Shift+F10: the caret's line becomes the next statement the current thread runs; nothing in between runs.</summary>
    public async Task SetNextStatementAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        await SetNextStatementAsync(editor.Model.Path, controller.Editor.TextArea.Caret.Line).ConfigureAwait(true);
    }

    /// <summary>Sets the current thread's next statement to <paramref name="line"/> of <paramref name="path"/>, or says why it can't be.</summary>
    public async Task<bool> SetNextStatementAsync(string path, int line)
    {
        if (await Debugger.SetNextStatementAsync(path, line).ConfigureAwait(true) is { } problem)
        {
            StatusBar.ShowMessage(problem, TimeSpan.FromSeconds(6));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Make Object ID: the object gets a name (<c>$1</c>) and a watch on it, so it stays in reach when no variable holds
    /// it any more. Returns the name, or null with the reason in the status bar.
    /// </summary>
    public async Task<string?> MakeObjectIdAsync(string expression)
    {
        var (name, problem) = await Debugger.MakeObjectIdAsync(expression).ConfigureAwait(true);
        if (name is null)
        {
            StatusBar.ShowMessage(problem ?? string.Empty, TimeSpan.FromSeconds(6));
            return null;
        }

        if (!Watch.Expressions.Contains(name))
        {
            await AddWatchAsync(name).ConfigureAwait(true);
        }

        StatusBar.ShowMessage(Localizer.Format("msg.debug.objectIdMade", "{0} names this object for the rest of the session: use it in Watch, conditions and the Debug Console.", name), TimeSpan.FromSeconds(8));
        return name;
    }

    /// <summary>Delete Object ID: the name is let go, and its watch removed.</summary>
    public async Task DeleteObjectIdAsync(string name)
    {
        await Debugger.DeleteObjectIdAsync(name).ConfigureAwait(true);
        if (Watch.Roots.FirstOrDefault(r => r.Name == name) is { } watch)
        {
            Watch.Remove(watch);
        }
    }

    /// <summary>
    /// The expressions the Codalus debugger's own evaluator doesn't read (lambdas, LINQ, Visual Basic), compiled by the
    /// C# language host where the frame is stopped; the debugger runs the result in the program. Without the language
    /// host (no solution loaded, a file of no project) the answer says so, and the debugger shows it.
    /// </summary>
    private async Task<Codalus.Debugging.CompileExpressionResponse> CompileDebuggerExpressionAsync(Codalus.Debugging.CompileExpressionArguments request, CancellationToken cancellationToken)
    {
        if (Languages.RunningClient(CSharpLanguageId) is not { } client)
        {
            return new Codalus.Debugging.CompileExpressionResponse(Errors: [Localizer.Get("msg.debug.expressionNeedsTheProject", "This expression needs the compiler: open the program's solution to evaluate it.")]);
        }

        try
        {
            var result = await client.CompileExpressionAsync(
                new Codalus.LanguageServices.Protocol.CompileExpressionParams(request.Expression, request.SourcePath, request.Line, request.Column, request.ModulePath), cancellationToken).ConfigureAwait(false);
            return new Codalus.Debugging.CompileExpressionResponse(result.Assembly, result.TypeName, result.MethodName, result.Parameters, result.TypeParameters, result.Errors.Length == 0 ? null : result.Errors);
        }
        catch (Exception ex) when (ex is Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException or InvalidOperationException or TimeoutException)
        {
            return new Codalus.Debugging.CompileExpressionResponse(Errors: [ex.Message]);
        }
    }

    /// <summary>Shift+Alt+F11: the calls of the rest of the current line, to step into one; the others run without a stop.</summary>
    public async Task StepIntoSpecificAsync()
    {
        var targets = await Debugger.StepInTargetsAsync().ConfigureAwait(true);
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.debug.noCallsOnTheLine", "There are no calls on the rest of this line to step into."), TimeSpan.FromSeconds(5));
            return;
        }

        var items = targets.Select(target => new QuickPickItem(target.Label) { Icon = "step-into", Tag = target.Id }).ToList();
        if (await Palette.ShowAsync(items, Localizer.Get("msg.debug.stepIntoSpecific.placeholder", "Step into which call?")).ConfigureAwait(true) is { Tag: int id })
        {
            await Debugger.StepIntoSpecificAsync(id).ConfigureAwait(true);
        }
    }
}
