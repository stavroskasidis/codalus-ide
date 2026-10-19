using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
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
    /// Breaks ties between equally good matches of the typed text: the server's preselected item first, then an item of
    /// the type expected there (a local or parameter before a member), then the shorter name (after "Con", Console before
    /// ConcurrentExclusiveSchedulerPair, as a classic IDE picks; M34a).
    /// </summary>
    public double Priority => Item.Preselect == true ? 1
        : MatchesTargetType ? 0.6 + (Glyph is SymbolGlyphs.Local or SymbolGlyphs.Parameter ? 0.2 : 0) + (0.1 / (1 + Text.Length))
        : 0.5 / (1 + Text.Length);

    /// <summary>The item's type matches the one expected where it goes (the server's <c>codalusMatchesTargetType</c>).</summary>
    public bool MatchesTargetType => Item.MatchesTargetType == true;

    /// <summary>
    /// The characters that commit the item when typed (then are typed themselves): the item's, else the server's for every
    /// item; none for words and our snippets.
    /// </summary>
    public IReadOnlySet<char> CommitCharacters { get; init; } = new HashSet<char>();

    /// <summary>
    /// Resolves the item for the character that commits it (<c>codalusCommitCharacter</c>): set for the C# service's
    /// items, whose edit may depend on it.
    /// </summary>
    public Func<LspCompletionItem, Task<LspCompletionItem>>? ResolveForCommit { get; init; }

    /// <summary>The character being typed that commits the item, while it's committed that way.</summary>
    public char? CommitCharacter { get; set; }

    /// <summary>The committed edit already typed <see cref="CommitCharacter"/> (the server said so).</summary>
    public bool IncludedCommitCharacter { get; set; }

    /// <summary>Starts resolving (description and edits) in the background.</summary>
    public Task<LspCompletionItem> Resolve() => _resolved ??= resolve(Item);

    /// <summary>The resolved item once it has arrived, else null.</summary>
    public LspCompletionItem? Resolved => _resolved is { IsCompletedSuccessfully: true } task ? task.Result : null;

    /// <summary>
    /// The resolved item if it arrives within <paramref name="timeout"/>, else null. While a typed character commits it,
    /// the server resolves it again for that character (its edit may change: <c>List</c> before <c>&lt;</c>), falling back
    /// to the edit resolved already.
    /// </summary>
    public LspCompletionItem? ResolvedWithin(TimeSpan timeout)
    {
        if (CommitCharacter is { } typed && ResolveForCommit is { } resolveForCommit)
        {
            return Wait(resolveForCommit(Item with { CommitCharacter = typed.ToString() }), timeout) ?? Wait(Resolve(), TimeSpan.Zero);
        }

        return Wait(Resolve(), timeout);

        static LspCompletionItem? Wait(Task<LspCompletionItem> task, TimeSpan timeout)
        {
            try
            {
                return task.Wait(timeout) ? task.Result : null;
            }
            catch (AggregateException)
            {
                return null;
            }
        }
    }

    /// <summary>Commits the item over <paramref name="segment"/> (the typed text).</summary>
    public void Complete(ISegment segment) => commit(this, segment);

    public override string ToString() => Label;
}
