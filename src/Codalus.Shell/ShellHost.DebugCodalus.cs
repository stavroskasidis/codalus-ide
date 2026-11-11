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
