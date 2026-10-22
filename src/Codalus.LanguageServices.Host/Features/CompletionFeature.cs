using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using LspCompletionItem = Codalus.LanguageServices.Protocol.CompletionItem;
using LspCompletionList = Codalus.LanguageServices.Protocol.CompletionList;
using CompletionContext = Codalus.LanguageServices.Protocol.CompletionContext;
using CompletionTriggerKind = Codalus.LanguageServices.Protocol.CompletionTriggerKind;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Completion with Roslyn's <see cref="CompletionService"/>. The list carries labels, kinds and sort/filter text; the
/// edit (including any <c>using</c> it adds) and the description come from <c>completionItem/resolve</c>, which finds
/// the item in the last list computed (one per server, like Roslyn's own LSP server). XAML's and Razor's own lists get
/// their summaries the same way (<see cref="SummarizeOnResolve"/>).
/// </summary>
internal sealed class CompletionFeature
{
    /// <summary>Characters that commit the selected item, unless the list is in suggestion mode.</summary>
    private static readonly string[] s_commitCharacters = [" ", "{", "}", "[", "]", "(", ")", ".", ",", ":", ";", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "=", "<", ">", "?", "@", "#", "'", "\"", "\\"];

    private readonly Lock _gate = new();
    private Cached? _last;
    private Summarized? _lastSummarized;
    private long _nextResultId;

    /// <summary>A list's items; <see cref="MapRange"/> maps the edits of a Razor file's list (ADR 0023).</summary>
    private sealed record Cached(long ResultId, Document Document, RoslynCompletionList List)
    {
        public Func<Protocol.Range, Protocol.Range?>? MapRange { get; init; }
    }

    /// <summary>
    /// The last list was for a Razor file: resolving its items maps the main edit back to the Razor file and drops the
    /// additional ones (a using added to the generated file).
    /// </summary>
    public void MapLastList(Func<Protocol.Range, Protocol.Range?> mapRange)
    {
        lock (_gate)
        {
            if (_last is { } last)
            {
                _last = last with { MapRange = mapRange };
            }
        }
    }

    /// <summary>The symbols a XAML or Razor list's items describe (<see cref="SummarizeOnResolve"/>), and how to summarize one.</summary>
    private sealed record Summarized(long ResultId, ISymbol?[] Symbols, Func<ISymbol, CancellationToken, string?> Summary);

    /// <param name="ResultId">The list the item is from.</param>
    /// <param name="Index">The item's index in the list.</param>
    /// <param name="Summary">A XAML or Razor item whose summary comes on resolve (<see cref="SummarizeOnResolve"/>); else a C# item.</param>
    internal sealed record ItemData(long ResultId, int Index, bool? Summary = null);

    /// <summary>
    /// A XAML or Razor list (<paramref name="items"/>, each with the symbol it describes or null) whose summaries come from
    /// <c>completionItem/resolve</c>, as a C# item's description does, instead of with the list. Most items' summaries are
    /// never shown, and the first summary from a referenced assembly reads its whole XML file (PresentationCore's 4.5 MB
    /// takes about 0.3 s, without cancellation), which the list mustn't wait for. The summary is then read for the
    /// selected item only.
    /// </summary>
    public LspCompletionItem[] SummarizeOnResolve(IReadOnlyList<(LspCompletionItem Item, ISymbol? Symbol)> items, Func<ISymbol, CancellationToken, string?> summary)
    {
        if (items.All(i => i.Symbol is null))
        {
            return [.. items.Select(i => i.Item)];
        }

        long resultId;
        lock (_gate)
        {
            resultId = ++_nextResultId;
            _lastSummarized = new Summarized(resultId, [.. items.Select(i => i.Symbol)], summary);
        }

        return [.. items.Select((i, index) => i.Symbol is null ? i.Item : i.Item with
        {
            Data = JsonSerializer.SerializeToElement(new ItemData(resultId, index, Summary: true), HostJsonContext.Default.ItemData),
        })];
    }

    /// <summary>A XAML or Razor item with its summary (<see cref="SummarizeOnResolve"/>); one being committed needs none.</summary>
    private LspCompletionItem Summarize(LspCompletionItem item, ItemData key, CancellationToken cancellationToken)
    {
        Summarized? summarized;
        lock (_gate)
        {
            summarized = _lastSummarized;
        }

        if (item.CommitCharacter is not null || summarized is null || summarized.ResultId != key.ResultId
            || key.Index >= summarized.Symbols.Length || summarized.Symbols[key.Index] is not { } symbol)
        {
            return item;
        }

        return summarized.Summary(symbol, cancellationToken) is { } text ? item with { Documentation = MarkupContent.Markdown(text) } : item;
    }

    public static CompletionOptions Options => new(TriggerCharacters: [".", " ", "<", "#", "(", "[", ",", "\"", ":", "~", "@", "$"], AllCommitCharacters: s_commitCharacters, ResolveProvider: true);

    public async Task<LspCompletionList> CompleteAsync(Document document, int position, CompletionContext? context, CancellationToken cancellationToken)
    {
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return new LspCompletionList(false, []);
        }

        // Until a project loaded without its framework loads again with it, the list is empty and incomplete (asked again).
        if (await FrameworkGuard.IsMissingAsync(document, cancellationToken).ConfigureAwait(false))
        {
            return new LspCompletionList(true, []);
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var trigger = context is { TriggerKind: CompletionTriggerKind.TriggerCharacter, TriggerCharacter: [var ch, ..] }
            ? CompletionTrigger.CreateInsertionTrigger(ch)
            : CompletionTrigger.Invoke;
        if (trigger.Kind == Microsoft.CodeAnalysis.Completion.CompletionTriggerKind.Insertion && !service.ShouldTriggerCompletion(text, position, trigger))
        {
            return new LspCompletionList(false, []);
        }

        var list = await service.GetCompletionsAsync(document, position, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (list.ItemsList.Count == 0)
        {
            return new LspCompletionList(false, []);
        }

        long resultId;
        lock (_gate)
        {
            resultId = ++_nextResultId;
            _last = new Cached(resultId, document, list);
        }

        // Suggestion mode (a lambda's parameter, a new name): nothing typed commits an item.
        var suggestionMode = list.SuggestionModeItem is not null;
        var targetMatches = await TargetTypeMatchesAsync(document, list, cancellationToken).ConfigureAwait(false);
        var defaultEnterRule = DefaultEnterKeyRule(document);
        var items = new LspCompletionItem[list.ItemsList.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var item = list.ItemsList[i];
            var label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
            var preselect = item.Rules.MatchPriority == MatchPriority.Preselect;
            items[i] = new LspCompletionItem(label)
            {
                Kind = Kind(item.Tags),
                Glyph = ClassifiedText.Glyph(item.Tags),
                Detail = string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
                SortText = item.SortText == label ? null : item.SortText,
                FilterText = item.FilterText == label ? null : item.FilterText,
                Preselect = preselect ? true : null,
                SoftSelection = preselect && item.Rules.SelectionBehavior != CompletionItemSelectionBehavior.HardSelection ? true : null,
                CommitCharacters = suggestionMode ? [] : CommitCharacters(item.Rules),
                EnterKeyRule = EnterKeyRuleName(item.Rules.EnterKeyRule == EnterKeyRule.Default ? defaultEnterRule : item.Rules.EnterKeyRule),
                MatchesTargetType = targetMatches?[i] == true ? true : null,
                Data = JsonSerializer.SerializeToElement(new ItemData(resultId, i), HostJsonContext.Default.ItemData),
            };
        }

        return new LspCompletionList(false, items)
        {
            ItemDefaults = new CompletionItemDefaults(Positions.ToRange(text, list.Span)),
        };
    }

    /// <summary>
    /// An item's commit characters when its rules change the defaults (Roslyn's <c>CommitCharacterRules</c>: a keyword
    /// that isn't committed by "(", say), evaluated as the editor does: the first rule naming a character decides; null
    /// when they're the defaults (<see cref="CompletionOptions.AllCommitCharacters"/>).
    /// </summary>
    internal static string[]? CommitCharacters(CompletionItemRules rules)
    {
        if (rules.CommitCharacterRules.IsDefaultOrEmpty)
        {
            return null;
        }

        var candidates = s_commitCharacters.Select(c => c[0]).Concat(rules.CommitCharacterRules.SelectMany(r => r.Characters)).Distinct();
        var commit = candidates.Where(c => IsCommitCharacter(rules, c)).Select(c => c.ToString()).ToArray();
        return commit.SequenceEqual(s_commitCharacters) ? null : commit;

        static bool IsCommitCharacter(CompletionItemRules rules, char c)
        {
            foreach (var rule in rules.CommitCharacterRules)
            {
                switch (rule.Kind)
                {
                    case CharacterSetModificationKind.Add when rule.Characters.Contains(c):
                        return true;
                    case CharacterSetModificationKind.Remove when rule.Characters.Contains(c):
                        return false;
                    case CharacterSetModificationKind.Replace:
                        return rule.Characters.Contains(c);
                }
            }

            return s_commitCharacters.Contains(c.ToString());
        }
    }

    /// <summary>
    /// What Enter does after committing an item whose rule is the default: in C# it never adds a line, in Visual Basic
    /// it always does (Roslyn's services, keeping each language's previous behavior).
    /// </summary>
    private static EnterKeyRule DefaultEnterKeyRule(Document document) =>
        document.Project.Language == LanguageNames.VisualBasic ? EnterKeyRule.Always : EnterKeyRule.Never;

    private static string? EnterKeyRuleName(EnterKeyRule rule) => rule switch
    {
        EnterKeyRule.Always => EnterKeyRules.Always,
        EnterKeyRule.AfterFullyTypedWord => EnterKeyRules.AfterFullyTypedWord,
        _ => null,
    };

    /// <summary>The items matching the type expected where they go (<see cref="TargetTyping"/>), or null.</summary>
    private static async Task<bool[]?> TargetTypeMatchesAsync(Document document, RoslynCompletionList list, CancellationToken cancellationToken)
    {
        try
        {
            return await TargetTyping.MatchAsync(document, list.Span.Start, list.ItemsList, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refinement of the list: code the semantic model can't answer for (it shouldn't throw) still completes.
            return null;
        }
    }

    /// <summary>Fills the description and the edits. Items from an older list are returned unchanged.</summary>
    public async Task<LspCompletionItem> ResolveAsync(LspCompletionItem item, CancellationToken cancellationToken)
    {
        if (item.Data is not { } data || data.Deserialize(HostJsonContext.Default.ItemData) is not { } key)
        {
            return item;
        }

        if (key.Summary == true)
        {
            return Summarize(item, key, cancellationToken);
        }

        Cached? cached;
        lock (_gate)
        {
            cached = _last;
        }

        if (cached is null || cached.ResultId != key.ResultId || key.Index >= cached.List.ItemsList.Count)
        {
            return item;
        }

        var document = cached.Document;
        var roslynItem = cached.List.ItemsList[key.Index];
        var service = CompletionService.GetService(document)!;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        // Committed by a typed character: Roslyn may change the edit for it, and it needs no description.
        char? commitCharacter = item.CommitCharacter is [var typed] ? typed : null;
        var description = commitCharacter is null ? await service.GetDescriptionAsync(document, roslynItem, cancellationToken: cancellationToken).ConfigureAwait(false) : null;
        var change = await service.GetChangeAsync(document, roslynItem, commitCharacter, cancellationToken).ConfigureAwait(false);
        var includesCommitCharacter = commitCharacter is not null && change.IncludesCommitCharacter ? true : (bool?)null;

        // The main edit is the one at the completed span; others (an added using) are additional edits.
        var changes = change.TextChanges.IsDefaultOrEmpty ? ImmutableArray.Create(change.TextChange) : change.TextChanges;
        var main = changes.FirstOrDefault(c => c.Span.IntersectsWith(roslynItem.Span) || c.Span.End == roslynItem.Span.Start);
        if (main == default)
        {
            main = change.TextChange;
        }

        var additional = changes.Where(c => c != main).Select(c => new Protocol.TextEdit(Positions.ToRange(text, c.Span), c.NewText ?? string.Empty)).ToArray();
        var documentation = description is null || description.TaggedParts.IsEmpty ? null : MarkupContent.Markdown(Markdown.FromDescription(description.TaggedParts, Markdown.Fence(document)));
        var classified = description is null || description.TaggedParts.IsEmpty ? null : Describe(description.TaggedParts, ClassifiedText.Glyph(roslynItem.Tags));
        if (cached.MapRange is { } map)
        {
            return item with
            {
                Documentation = documentation,
                Description = classified,
                TextEdit = map(Positions.ToRange(text, main.Span)) is { } range ? new Protocol.TextEdit(range, main.NewText ?? string.Empty) : item.TextEdit,
                AdditionalTextEdits = null,
                IncludesCommitCharacter = includesCommitCharacter,
            };
        }

        return item with
        {
            Documentation = documentation,
            Description = classified,
            TextEdit = new Protocol.TextEdit(Positions.ToRange(text, main.Span), main.NewText ?? string.Empty),
            AdditionalTextEdits = additional.Length == 0 ? null : additional,
            IncludesCommitCharacter = includesCommitCharacter,
        };
    }

    /// <summary>
    /// A completion description (the signature and its overload count on the first line, then the documentation's
    /// paragraphs, some under a heading such as "Exceptions:") as classified text.
    /// </summary>
    internal static SymbolDescription Describe(ImmutableArray<TaggedText> parts, string? glyph)
    {
        var lines = ClassifiedText.Lines(ClassifiedText.Runs(parts));
        var (signature, overloads) = ClassifiedText.SplitOverloads(ClassifiedText.Trim(lines[0]));
        var sections = new List<DescriptionSection>();
        var paragraph = new List<List<ClassifiedRun>>();
        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            var kind = sections.Count == 0 ? DescriptionSectionKinds.Summary : DescriptionSectionKinds.Other;
            var heading = string.Concat(paragraph[0].Select(r => r.Text)).Trim();
            if (paragraph.Count > 1 && paragraph[0].All(r => r.Kind == ClassifiedRun.PlainText) && heading.EndsWith(':'))
            {
                kind = heading switch
                {
                    "Returns:" => DescriptionSectionKinds.Returns,
                    "Value:" => DescriptionSectionKinds.Value,
                    "Exceptions:" => DescriptionSectionKinds.Exceptions,
                    "Remarks:" => DescriptionSectionKinds.Remarks,
                    "Usage:" => DescriptionSectionKinds.Usage,
                    _ => DescriptionSectionKinds.Other,
                };
                if (kind != DescriptionSectionKinds.Other)
                {
                    paragraph.RemoveAt(0);
                }
            }

            var runs = new List<ClassifiedRun>();
            foreach (var line in paragraph)
            {
                if (runs.Count > 0)
                {
                    runs.Add(new ClassifiedRun("\n", ClassifiedRun.LineBreak));
                }

                runs.AddRange(line.Select((r, i) => i == 0 && r.Kind == ClassifiedRun.PlainText ? r with { Text = r.Text.TrimStart() } : r));
            }

            if (ClassifiedText.Trim(runs) is { Length: > 0 } body)
            {
                sections.Add(new DescriptionSection(kind, body));
            }

            paragraph.Clear();
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.All(r => string.IsNullOrWhiteSpace(r.Text)))
            {
                Flush();
            }
            else
            {
                paragraph.Add(line);
            }
        }

        Flush();
        return new SymbolDescription(signature, [.. sections]) { Glyph = glyph, Overloads = overloads };
    }

    private static int Kind(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Class: return CompletionItemKind.Class;
                case WellKnownTags.Structure: return CompletionItemKind.Struct;
                case WellKnownTags.Interface: return CompletionItemKind.Interface;
                case WellKnownTags.Enum: return CompletionItemKind.Enum;
                case WellKnownTags.EnumMember: return CompletionItemKind.EnumMember;
                case WellKnownTags.Delegate: return CompletionItemKind.Function;
                case WellKnownTags.Method: return CompletionItemKind.Method;
                case WellKnownTags.ExtensionMethod: return CompletionItemKind.Method;
                case WellKnownTags.Property: return CompletionItemKind.Property;
                case WellKnownTags.Field: return CompletionItemKind.Field;
                case WellKnownTags.Event: return CompletionItemKind.Event;
                case WellKnownTags.Constant: return CompletionItemKind.Constant;
                case WellKnownTags.Local:
                case WellKnownTags.Parameter:
                case WellKnownTags.RangeVariable: return CompletionItemKind.Variable;
                case WellKnownTags.Namespace: return CompletionItemKind.Module;
                case WellKnownTags.TypeParameter: return CompletionItemKind.TypeParameter;
                case WellKnownTags.Keyword: return CompletionItemKind.Keyword;
                case WellKnownTags.Snippet: return CompletionItemKind.Snippet;
                case WellKnownTags.Operator: return CompletionItemKind.Operator;
                case WellKnownTags.Label: return CompletionItemKind.Reference;
                case WellKnownTags.File: return CompletionItemKind.File;
                case WellKnownTags.Folder: return CompletionItemKind.Folder;
                case WellKnownTags.Assembly:
                case WellKnownTags.Module: return CompletionItemKind.Module;
            }
        }

        return CompletionItemKind.Text;
    }
}

/// <summary>Host-only JSON (completion item data).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompletionFeature.ItemData))]
internal sealed partial class HostJsonContext : JsonSerializerContext;

/// <summary>Roslyn tagged text → Markdown: the first line (the signature) as code of the document's language, the rest as text.</summary>
internal static class Markdown
{
    /// <summary>The Markdown fence's language for a document: <c>vb</c> for Visual Basic (M35), <c>csharp</c> otherwise.</summary>
    public static string Fence(Microsoft.CodeAnalysis.Document document) => document.Project.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic ? "vb" : "csharp";

    public static string FromDescription(ImmutableArray<TaggedText> parts, string fence = "csharp")
    {
        var text = string.Concat(parts.Select(p => p.Text)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var signature = newline < 0 ? text : text[..newline];
        var rest = newline < 0 ? string.Empty : text[(newline + 1)..].Trim();
        var builder = new StringBuilder();
        builder.Append("```").Append(fence).Append('\n').Append(signature).Append("\n```");
        if (rest.Length > 0)
        {
            builder.Append("\n\n").Append(Escape(rest));
        }

        return builder.ToString();
    }

    public static string Code(string code, string fence = "csharp") => "```" + fence + "\n" + code + "\n```";

    /// <summary>Escapes the characters that would start Markdown formatting in plain documentation text.</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
