using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// #67's half of the Windows Forms designer's document (ADR 0073), as the classic IDE's command set has them: Cut, Select
/// All, Select the parent, Paste's target, Lock Controls, the context keys the designer's menus are enabled by, the verbs
/// its context menu lists, and a default .resx written before an edit of its metadata.
/// </summary>
public sealed partial class WinFormsDesignerDocument
{
    private (string Component, int Version, Task<IReadOnlyList<Workbench.WinForms.DesignerActionItem>> Verbs)? _verbs;
    private CancellationTokenSource? _verbsDelay;
    private int _renderVersion;

    // ------------------------------------------------------------------ the selection

    /// <summary>Empties the designers' clipboard (tests: Paste is grayed until something is copied).</summary>
    internal static void ClearClipboard() => s_clipboard = [];

    /// <summary>Cut: Copy, then Delete.</summary>
    public async Task CutAsync()
    {
        Copy();
        await DeleteSelectionAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Select All, as the classic IDE's: every component of the form but the form itself (nested controls, the tray's
    /// components, tool strip items); a split container's panels aren't components.
    /// </summary>
    public void SelectAll() => SetSelection([.. _components.Where(c => c != Root && !c.IsPart).Select(c => c.Name)], notify: true);

    /// <summary>
    /// The primary selection's parent, which Select '…' and Esc select: a tool strip item's strip or menu item, a control's
    /// container (a split container's panel, <c>notesSplit.Panel1</c>, for a control in one). Null for the form and the
    /// tray's components.
    /// </summary>
    public string? ParentOfPrimary => PrimarySelection is { } primary && Find(primary) is { } view && view != Root ? view.Owner ?? view.Parent : null;

    /// <summary>Select '…' (and Esc): the primary selection's parent.</summary>
    public void SelectParent()
    {
        if (ParentOfPrimary is { } parent)
        {
            SetSelection([parent], notify: true);
        }
    }

    /// <summary>
    /// Where Paste puts the copied components, as the classic IDE's nearest drag client: the primary selection when it's a
    /// container (a panel, a group box, a split container's panel), else the nearest container it's in, else the form. A
    /// panel of a split container is its owner and the panel's path (<c>notesSplit</c>, <c>Panel1</c>).
    /// </summary>
    internal (string Parent, string? Path) PasteTarget()
    {
        for (var view = PrimarySelection is { } primary ? Find(primary) : null; view is not null && view != Root; view = view.Parent is { } parent ? Find(parent) : null)
        {
            if (!view.IsControl || !view.IsContainer)
            {
                continue;
            }

            if (view.IsPart && view.Parent is { } owner && view.Name.StartsWith(owner + ".", StringComparison.Ordinal))
            {
                return (ComponentOf(owner), view.Name[(owner.Length + 1)..]);
            }

            return (view.Name, null);
        }

        return (string.Empty, null);
    }

    // ------------------------------------------------------------------ Lock Controls

    /// <summary>
    /// Lock Controls is checked, as the classic IDE's <c>OnStatusLockControls</c>: the form, or any of its own controls
    /// (its designer's associated components), is locked.
    /// </summary>
    public bool AreControlsLocked => Root is { } root
        && (root.Locked == true || _components.Any(v => v.Parent == root.Name && v.IsControl && !v.IsPart && v.Locked == true));

    /// <summary>
    /// Format → Lock Controls, as the classic IDE's <c>OnMenuLockControls</c>: every component that can be locked (not a
    /// tray component, a tool strip item or a part) is set to the opposite of the check, in one change of the host, so
    /// one undo step. The host writes it as the default .resx's <c>Locked</c> metadata.
    /// </summary>
    public Task LockControlsAsync()
    {
        var locked = !AreControlsLocked;
        var edits = _components.Where(v => v.Locked is not null).Select(v => new DesignerPropertyEdit(ComponentOf(v.Name), "Locked") { Text = locked ? "True" : "False" }).ToList();
        return edits.Count == 0 ? Task.CompletedTask : RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. edits]), CancellationToken.None));
    }

    // ------------------------------------------------------------------ the context keys

    /// <summary>
    /// The context keys the designer's commands are enabled by, as the classic IDE's command set enables them (a selection
    /// with the form in it counts as empty there): set while this is the active document, after every change of the
    /// selection, the surface or the clipboard. <paramref name="force"/> sets them for a document becoming active.
    /// </summary>
    internal void PublishContext(bool force = false)
    {
        if (!force && !ReferenceEquals(_shell.Layout.ActiveDocument, this))
        {
            return;
        }

        var selection = _selection.Select(Find).OfType<DesignerComponentView>().ToList();
        var rootSelected = Root is { } root && _selection.Contains(root.Name);
        List<DesignerComponentView> effective = rootSelected ? [] : selection;
        var controlsOnly = effective.All(v => v.IsControl);
        var formatted = effective.Count(v => !v.IsPart);
        var primary = PrimarySelection is { } name ? Find(name) : null;
        var canAlign = controlsOnly && formatted > 1 && primary is not null && effective.Contains(primary);
        _shell.Workbench.Context.Set("winFormsControlsOnly", controlsOnly);
        _shell.Workbench.Context.Set("winFormsCanFormat", controlsOnly && formatted > 0);
        _shell.Workbench.Context.Set("winFormsCanAlign", canAlign);
        _shell.Workbench.Context.Set("winFormsCanSpace", canAlign && effective.Select(v => v.Parent).Distinct(StringComparer.Ordinal).Count() == 1);
        _shell.Workbench.Context.Set("winFormsCanOrder", controlsOnly && formatted > 0 && _components.Count(c => c != Root && !c.IsPart) > 1);
        _shell.Workbench.Context.Set("winFormsCanCopy", !rootSelected && selection.Any(v => !v.IsPart));
        _shell.Workbench.Context.Set("winFormsCanDelete", !rootSelected && selection.Count > 0 && selection.All(v => !v.IsPart));
        _shell.Workbench.Context.Set("winFormsCanPaste", s_clipboard.Count > 0);
        _shell.Workbench.Context.Set("winFormsSelectionHasParent", ParentOfPrimary is not null);
        _shell.Workbench.Context.Set("winFormsSelectionHasActions", primary?.HasActions == true);
        _shell.Workbench.Context.Set("winFormsControlsLocked", AreControlsLocked);
    }

    /// <summary>The designer's context keys cleared, when another kind of document becomes active.</summary>
    internal static void ClearContext(ShellHost shell)
    {
        shell.Workbench.Context.Set("winFormsControlsOnly", false);
        shell.Workbench.Context.Set("winFormsCanFormat", false);
        shell.Workbench.Context.Set("winFormsCanAlign", false);
        shell.Workbench.Context.Set("winFormsCanSpace", false);
        shell.Workbench.Context.Set("winFormsCanOrder", false);
        shell.Workbench.Context.Set("winFormsCanCopy", false);
        shell.Workbench.Context.Set("winFormsCanDelete", false);
        shell.Workbench.Context.Set("winFormsCanPaste", false);
        shell.Workbench.Context.Set("winFormsSelectionHasParent", false);
        shell.Workbench.Context.Set("winFormsSelectionHasActions", false);
        shell.Workbench.Context.Set("winFormsControlsLocked", false);
    }

    // ------------------------------------------------------------------ the context menu's verbs

    /// <summary>The one component whose verbs the context menu lists: a single selection that isn't a part (the classic IDE lists verbs for one).</summary>
    private string? VerbsTarget() => _selection.Count == 1 && Find(_selection[0]) is { IsPart: false } view ? view.Name : null;

    /// <summary>
    /// The verbs fetched ahead (#67), when the selection settles (150 ms) or the surface changes (a change can enable one,
    /// as Remove Tab once there's a tab page), or at once (<paramref name="now"/>: a right-button press), so the context
    /// menu has them when it opens. Kept per component and surface; an answer for another one is never shown.
    /// </summary>
    private void RefreshVerbs(bool now = false)
    {
        _verbsDelay?.Cancel();
        _verbsDelay = null;
        if (_host is null || _disposed || VerbsTarget() is not { } component || HasVerbsFor(component))
        {
            return;
        }

        if (now)
        {
            _ = StartVerbs(component);
            return;
        }

        var delay = _verbsDelay = new CancellationTokenSource();
        _ = StartLaterAsync();

        async Task StartLaterAsync()
        {
            try
            {
                await Task.Delay(150, delay.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_disposed && VerbsTarget() == component && !HasVerbsFor(component))
            {
                _ = StartVerbs(component);
            }
        }
    }

    /// <summary>A right-button press on the surface: the verbs of what it selected, now.</summary>
    internal void PrefetchVerbs() => RefreshVerbs(now: true);

    /// <summary>
    /// The selection's verbs as the host answers, however long it takes: the ones fetched ahead, else fetched now (the
    /// smoke test, before it checks the menu on a busy machine; the menu itself waits only 300 ms for them).
    /// </summary>
    internal Task<IReadOnlyList<Workbench.WinForms.DesignerActionItem>> SelectionVerbsAsync() =>
        _host is null || VerbsTarget() is not { } component ? Task.FromResult<IReadOnlyList<Workbench.WinForms.DesignerActionItem>>([])
        : HasVerbsFor(component) ? _verbs!.Value.Verbs : StartVerbs(component);

    private bool HasVerbsFor(string component) => _verbs is { } cached && cached.Component == component && cached.Version == _renderVersion;

    private Task<IReadOnlyList<Workbench.WinForms.DesignerActionItem>> StartVerbs(string component)
    {
        var task = _host is { } host ? VerbsAsync(host, ComponentOf(component)) : Task.FromResult<IReadOnlyList<Workbench.WinForms.DesignerActionItem>>([]);
        _verbs = (component, _renderVersion, task);
        return task;
    }

    private static async Task<IReadOnlyList<Workbench.WinForms.DesignerActionItem>> VerbsAsync(IWinFormsDesignerHost host, string component)
    {
        try
        {
            return (await host.ActionsAsync(new DesignerComponentsParams([component]), CancellationToken.None).ConfigureAwait(false)).Verbs;
        }
        catch (Exception ex) when (ex is PreviewerException or Rpc.JsonRpcConnectionLostException or Rpc.JsonRpcRemoteException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // A host that ended or restarted meanwhile: the menu opens without verbs.
            return [];
        }
    }

    /// <summary>
    /// The verbs the context menu lists for the selection: the ones fetched ahead, else fetched now; an answer still on its
    /// way gets 300 ms, then the menu opens without them. None when the selection changed meanwhile.
    /// </summary>
    public async Task<IReadOnlyList<Workbench.WinForms.DesignerActionItem>> ContextVerbsAsync()
    {
        if (_host is null || VerbsTarget() is not { } component)
        {
            return [];
        }

        var task = HasVerbsFor(component) ? _verbs!.Value.Verbs : StartVerbs(component);
        try
        {
            var verbs = await task.WaitAsync(VerbsWait).ConfigureAwait(true);
            return VerbsTarget() == component ? verbs : [];
        }
        catch (TimeoutException)
        {
            _shell.WinFormsDesignerLog.AppendLine($"{component}'s verbs didn't come within {VerbsWait.TotalMilliseconds:0} ms: its context menu opened without them.");
            return [];
        }
    }

    /// <summary>How long a context menu waits for verbs still on their way (tests: a late answer that comes within it).</summary>
    internal TimeSpan VerbsWait { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A verb of the context menu, on the primary selection: as a smart tag's method (a collection's editor for Edit Items…).</summary>
    public Task InvokeVerbAsync(Workbench.WinForms.DesignerActionItem verb) =>
        PrimarySelection is { } component ? InvokeActionAsync(component, verb, text: null) : Task.CompletedTask;

    // ------------------------------------------------------------------ a default .resx before its first metadata

    /// <summary>
    /// A form without a default .resx gets one before an edit of it (#67: Lock Controls' <c>Locked</c> metadata): the
    /// empty one the C# host would create, in the designer file's line endings, held before the write, so the edit
    /// changes a model the designer holds and undoes with it. A new culture's .resx keeps the C# host's creation.
    /// TODO(issue): the same for a new culture's .resx, whose first edit doesn't undo with the designer's step today.
    /// </summary>
    private async Task EnsureDefaultResourcesAsync(IReadOnlyList<DesignerEdit> edits)
    {
        if (!edits.Any(e => e.Kind == DesignerEditKinds.SetResource && string.IsNullOrEmpty(e.Culture) && e.Resource is not null)
            || WinFormsDesignerService.FormResources(CodePath).Any(r => r.Culture.Length == 0))
        {
            return;
        }

        // TODO(issue): a .NET Framework (non-SDK) project also needs the new file as an EmbeddedResource with DependentUpon; the C# host's own creation (a culture's .resx) has the same gap.
        var path = CodePath[..^".cs".Length] + ".resx";
        var newLine = _designerCode?.Text.Text.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
        try
        {
            await File.WriteAllTextAsync(path, DesignerResx.Empty.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.WinFormsDesignerLog.AppendLine($"{Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        await HoldResourcesAsync().ConfigureAwait(true);
    }
}
