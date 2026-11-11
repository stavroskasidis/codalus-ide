using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

// What the Codalus debugger does that netcoredbg doesn't (M58, ADR 0094), as commands: Set Next Statement and Step Into
// Specific. Each is offered by what the stopped thread's debugger says it can do (the context keys below), so under
// netcoredbg, or in the browser, they're off.
public sealed partial class ShellHost
{
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
