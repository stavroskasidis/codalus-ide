using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell;

/// <summary>
/// CodeLens clicks (<c>editor.codeLensStyle</c>): in the popup style (the default) a count lists its results in a popup
/// under it, whose header button shows them in the References window instead; in the References window style a count
/// of references runs Find All References and a count of implementations or derived types lists them in the References
/// window (one goes straight there).
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The most derived types a popup lists: a type hierarchy is walked one level at a time.</summary>
    private const int MaxLensTypes = 500;

    void IEditorServices.RunCodeLens(CodeLensClick click) => _ = RunCodeLensAsync(click);

    /// <summary>
    /// Runs a CodeLens click at the lens's symbol (<see cref="CodeLensClick.Position"/>, in the pane that was clicked:
    /// the split pane's caret isn't the primary controller's).
    /// </summary>
    internal async Task RunCodeLensAsync(CodeLensClick click)
    {
        if (click.Subject == LensSubject.Other)
        {
            Run(click.CommandId); // another language's lens: its own command
            return;
        }

        if (ActiveLanguageEditor() is not var (editor, _, client))
        {
            return;
        }

        var position = click.Position;
        await GuardLensAsync(async () =>
        {
            if (EditorOptions.For(editor.Model.LanguageId).CodeLensPopups)
            {
                await ShowLensPopupAsync(click, editor, client, position).ConfigureAwait(true);
            }
            else if (click.Subject == LensSubject.Usages)
            {
                Run(click.CommandId);
            }
            else
            {
                await ShowLensImplementationsAsync(editor, client, position, alwaysWindow: false).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs a CodeLens step and logs what the language host's failures (a restart, a remote error, a cancellation)
    /// leave: also the popup's row and header button, which run after the click's own call has returned.
    /// </summary>
    private async Task GuardLensAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException)
        {
            Workbench.Log.AppendLine($"[codelens] {ex.Message}");
        }
    }

    /// <summary>
    /// The implementations and overrides of the symbol at <paramref name="position"/>, without the symbol's own
    /// declarations: Go to Implementation lists a non-abstract class or a virtual member among its own, and the lens
    /// counts only the others.
    /// </summary>
    private static async Task<Location[]> LensImplementationLocationsAsync(CodeEditorDocument editor, LanguageHostClient client, Position position)
    {
        var locations = await client.ImplementationAsync(editor.Model.Uri, position, CancellationToken.None).ConfigureAwait(true);
        if (locations.Length == 0)
        {
            return locations;
        }

        var own = await client.DefinitionAsync(editor.Model.Uri, position, CancellationToken.None).ConfigureAwait(true);
        return [.. locations.Where(l => !own.Any(d => d.Uri == l.Uri && d.Range.Start == l.Range.Start))];
    }

    /// <summary>
    /// The default style's implementations: one opens; more go to the References window (<c>'Shape' implementations</c>),
    /// as <paramref name="alwaysWindow"/> (the popup's window button) sends even one.
    /// </summary>
    private async Task ShowLensImplementationsAsync(CodeEditorDocument editor, LanguageHostClient client, Position position, bool alwaysWindow)
    {
        var locations = await LensImplementationLocationsAsync(editor, client, position).ConfigureAwait(true);
        if (locations.Length == 0)
        {
            ShowMessage(Codalus.Workbench.Localization.Localizer.Get("msg.codeLens.noImplementations", "No implementations found."));
            return;
        }

        if (locations.Length == 1 && !alwaysWindow)
        {
            OpenLocations(locations, "Go to Implementation");
            return;
        }

        await ShowReferenceLocationsAsync(IdentifierAt(editor.Model.Text, editor.Model.OffsetAt(position)), locations, implementations: true).ConfigureAwait(true);
    }

    private async Task ShowLensPopupAsync(CodeLensClick click, CodeEditorDocument editor, LanguageHostClient client, Position position)
    {
        var name = IdentifierAt(editor.Model.Text, editor.Model.OffsetAt(position));
        string title;
        IReadOnlyList<LensResultItem> items;
        Action openWindow;
        if (click.Subject == LensSubject.Usages)
        {
            (name, items, openWindow) = await LensUsagesAsync(editor, client, position, name).ConfigureAwait(true);
            title = Codalus.Workbench.Localization.Localizer.Format("codeLens.popup.usages", "Usages of '{0}'", name);
        }
        else
        {
            items = click.Subject is LensSubject.DerivedTypes or LensSubject.Implementations
                ? await LensDerivedTypesAsync(editor, client, position).ConfigureAwait(true)
                : [];
            if (items.Count == 0)
            {
                // A member's implementations and overrides, or a server without a type hierarchy.
                items = await LensRowsAsync(await LensImplementationLocationsAsync(editor, client, position).ConfigureAwait(true), SymbolKind.Method).ConfigureAwait(true);
            }

            title = click.Subject == LensSubject.DerivedTypes
                ? Codalus.Workbench.Localization.Localizer.Format("codeLens.popup.derivedTypes", "Derived types of '{0}'", name)
                : Codalus.Workbench.Localization.Localizer.Format("codeLens.popup.implementations", "Implementations of '{0}'", name);
            openWindow = () => _ = GuardLensAsync(() => ShowLensImplementationsAsync(editor, client, position, alwaysWindow: true));
        }

        if (items.Count == 0)
        {
            ShowMessage(click.Subject == LensSubject.Usages
                ? Codalus.Workbench.Localization.Localizer.Get("msg.codeLens.noReferences", "No references found.")
                : Codalus.Workbench.Localization.Localizer.Get("msg.codeLens.noImplementations", "No implementations found."));
            return;
        }

        editor.View?.ShowLensResults(click.Target, click.Anchor, title, items, location => GuardLensAsync(() =>
        {
            RememberLocation();
            return OpenLocationAsync(location);
        }), openWindow, click.ReturnFocus);
    }

    /// <summary>The uses of the symbol (not its declarations), and how to show all of them in the References window.</summary>
    private async Task<(string Name, IReadOnlyList<LensResultItem> Items, Action OpenWindow)> LensUsagesAsync(CodeEditorDocument editor, LanguageHostClient client, Position position, string name)
    {
        if (editor.Model.LanguageId == CSharpLanguageId)
        {
            if (await client.ReferenceItemsAsync(editor.Model.Uri, position, CancellationToken.None).ConfigureAwait(true) is not { } classified)
            {
                return (name, [], () => { });
            }

            var items = classified.Items
                .Where(i => i.Kind != ReferenceItemKinds.Declaration)
                .Select(i => new LensResultItem("references", i.LineText, Place(i.Location), i.Project, i.Location))
                .ToList();
            return (classified.SymbolName, items, () => ShowReferences(classified.SymbolName, classified.Items));
        }

        var locations = await client.ReferencesAsync(editor.Model.Uri, position, includeDeclaration: false, CancellationToken.None).ConfigureAwait(true);
        return (name, await LensRowsAsync(locations, icon: null).ConfigureAwait(true), () => _ = GuardLensAsync(() => ShowReferenceLocationsAsync(name, locations)));
    }

    /// <summary>
    /// The types derived from the type at <paramref name="position"/> (or implementing it), every level, as the type
    /// hierarchy lists them: the name, its namespace and its project, up to <see cref="MaxLensTypes"/>. Empty when the
    /// position isn't a type's name. The interfaces derived from an interface are walked through to reach their
    /// implementers but aren't rows: the lens counts the types that implement it, no interfaces.
    /// </summary>
    private async Task<IReadOnlyList<LensResultItem>> LensDerivedTypesAsync(CodeEditorDocument editor, LanguageHostClient client, Position position)
    {
        if (client.Capabilities?.TypeHierarchyProvider != true)
        {
            return [];
        }

        var roots = await client.PrepareTypeHierarchyAsync(editor.Model.Uri, position, CancellationToken.None).ConfigureAwait(true);
        // The hierarchy is prepared for a member's containing type too: only a type's own name lists its derived types.
        if (roots.FirstOrDefault(r => Contains(r.SelectionRange, position)) is not { } root)
        {
            return [];
        }

        var projectOf = ErrorListViewModel.ProjectNames([.. Solutions.Projects.Values]);
        var subtypes = await LensSubtypesAsync(root, item => client.SubtypesAsync(item, CancellationToken.None), MaxLensTypes).ConfigureAwait(true);
        return [.. subtypes.Select(subtype =>
        {
            var path = DocumentUris.ToPath(subtype.Uri);
            return new LensResultItem(
                SymbolNodeViewModel.IconFor(subtype.Kind),
                subtype.Name,
                subtype.Detail is { Length: > 0 } ns ? $"(in {ns})" : null,
                path is null ? null : projectOf(path) is { Length: > 0 } project ? project : null,
                new Location(subtype.Uri, subtype.SelectionRange));
        })];
    }

    /// <summary>
    /// The types under <paramref name="root"/> in the type hierarchy, level by level, each once, up to
    /// <paramref name="max"/>: derived interfaces are walked through (their implementers are under them) but left out,
    /// as the lens counts no interfaces.
    /// </summary>
    internal static async Task<List<TypeHierarchyItem>> LensSubtypesAsync(TypeHierarchyItem root, Func<TypeHierarchyItem, Task<TypeHierarchyItem[]>> subtypesOf, int max)
    {
        var seen = new HashSet<(string, int, int)> { Key(root) };
        var queue = new Queue<TypeHierarchyItem>([root]);
        var items = new List<TypeHierarchyItem>();
        while (items.Count < max && queue.TryDequeue(out var item))
        {
            foreach (var subtype in await subtypesOf(item).ConfigureAwait(true))
            {
                if (items.Count >= max)
                {
                    break; // an interface with thousands of direct implementers
                }

                if (!seen.Add(Key(subtype)))
                {
                    continue; // a type that implements two interfaces of the hierarchy
                }

                queue.Enqueue(subtype);
                if (subtype.Kind != SymbolKind.Interface)
                {
                    items.Add(subtype);
                }
            }
        }

        return items;

        static (string, int, int) Key(TypeHierarchyItem item) => (item.Uri, item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
    }

    /// <summary>Rows of plain locations: the line's code, the file and line, and the project.</summary>
    private async Task<IReadOnlyList<LensResultItem>> LensRowsAsync(IReadOnlyList<Location> locations, int? icon)
    {
        var comparer = Codalus.Workbench.Projects.SolutionService.PathComparer;
        var paths = locations.Select(l => DocumentUris.ToPath(l.Uri)).OfType<string>().Distinct(comparer).ToList();
        var open = new Dictionary<string, string>(comparer);
        foreach (var path in paths)
        {
            if (OpenTextOf(path) is { } text)
            {
                open[path] = text;
            }
        }

        var (lines, projects) = await Task.Run(() => ReadReferencedFilesAsync(paths, open, [.. Solutions.Projects.Values])).ConfigureAwait(true);
        return [.. locations.Select(l =>
        {
            var path = DocumentUris.ToPath(l.Uri);
            var line = path is not null && lines.TryGetValue(path, out var text) && l.Range.Start.Line < text.Length ? text[l.Range.Start.Line].Trim() : string.Empty;
            return new LensResultItem(
                icon is int kind ? SymbolNodeViewModel.IconFor(kind) : "references",
                line.Length > 0 ? line : Place(l),
                Place(l),
                path is null ? null : projects.GetValueOrDefault(path),
                l);
        })];
    }

    /// <summary><c>Shapes.cs:12</c>.</summary>
    private static string Place(Location location) =>
        $"{Path.GetFileName(DocumentUris.ToPath(location.Uri) ?? DocumentUris.GeneratedPath(location.Uri) ?? location.Uri)}:{location.Range.Start.Line + 1}";

    private static bool Contains(Codalus.LanguageServices.Protocol.Range range, Position position) =>
        (position.Line > range.Start.Line || (position.Line == range.Start.Line && position.Character >= range.Start.Character))
        && (position.Line < range.End.Line || (position.Line == range.End.Line && position.Character <= range.End.Character));
}
