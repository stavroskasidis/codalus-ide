using AvaloniaEdit.Document;
using Codalus.Shell.Editor.IntelliSense;
using LspCompletionItem = Codalus.LanguageServices.Protocol.CompletionItem;

namespace Codalus.Shell.Editor;

/// <summary>
/// One item of the completion list. Filtering uses <see cref="Text"/> (the filter text); committing applies the resolved
/// edit (prefetched while the item is selected) and any additional edits.
/// </summary>
public sealed class CompletionData(LspCompletionItem item, Func<LspCompletionItem, Task<LspCompletionItem>> resolve, Action<CompletionData, ISegment> commit)
{
    private Task<LspCompletionItem>? _resolved;

    public LspCompletionItem Item { get; } = item;

    /// <summary>The text typed text is matched against.</summary>
    public string Text => Item.FilterText ?? Item.Label;

    /// <summary>The text the list shows.</summary>
    public string Label => Item.Label;

    /// <summary>The symbol's kind (<see cref="Codalus.LanguageServices.Protocol.SymbolGlyphs"/>).</summary>
    public string Glyph { get; } = SymbolKinds.GlyphOf(item);

    /// <summary>The icon of the item's kind.</summary>
    public string Icon => SymbolKinds.IconOf(Glyph);

    /// <summary>The filter the item belongs to, or null.</summary>
    public string? Filter => SymbolKinds.FilterOf(Glyph);

    /// <summary>The characters of <see cref="Label"/> the typed text matched (drawn bold), set by the list's filtering.</summary>
    public IReadOnlyList<int> Matches { get; internal set; } = [];

    /// <summary>
    /// Breaks ties between equally good matches of the typed text: the server's preselected item first, then the
    /// shorter name (after "Con", Console before ConcurrentExclusiveSchedulerPair, as a classic IDE picks; M34a).
    /// </summary>
    public double Priority => Item.Preselect == true ? 1 : 0.5 / (1 + Text.Length);

    /// <summary>Starts resolving (description and edits) in the background.</summary>
    public Task<LspCompletionItem> Resolve() => _resolved ??= resolve(Item);

    /// <summary>The resolved item once it has arrived, else null.</summary>
    public LspCompletionItem? Resolved => _resolved is { IsCompletedSuccessfully: true } task ? task.Result : null;

    /// <summary>The resolved item if it arrives within <paramref name="timeout"/>, else null.</summary>
    public LspCompletionItem? ResolvedWithin(TimeSpan timeout)
    {
        var task = Resolve();
        try
        {
            return task.Wait(timeout) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>Commits the item over <paramref name="segment"/> (the typed text).</summary>
    public void Complete(ISegment segment) => commit(this, segment);

    public override string ToString() => Label;
}
