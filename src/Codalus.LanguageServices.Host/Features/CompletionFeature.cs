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
/// the item in the last list computed (one per server, like Roslyn's own LSP server).
/// </summary>
internal sealed class CompletionFeature
{
    /// <summary>Characters that commit the selected item, unless the list is in suggestion mode.</summary>
    private static readonly string[] s_commitCharacters = [" ", "{", "}", "[", "]", "(", ")", ".", ",", ":", ";", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "=", "<", ">", "?", "@", "#", "'", "\"", "\\"];

    private readonly Lock _gate = new();
    private Cached? _last;
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

    internal sealed record ItemData(long ResultId, int Index);

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

        var suggestionMode = list.SuggestionModeItem is not null;
        var items = new LspCompletionItem[list.ItemsList.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var item = list.ItemsList[i];
            var label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
            items[i] = new LspCompletionItem(label)
            {
                Kind = Kind(item.Tags),
                Glyph = ClassifiedText.Glyph(item.Tags),
                Detail = string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
                SortText = item.SortText == label ? null : item.SortText,
                FilterText = item.FilterText == label ? null : item.FilterText,
                Preselect = item.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
                CommitCharacters = suggestionMode ? [] : null,
                Data = JsonSerializer.SerializeToElement(new ItemData(resultId, i), HostJsonContext.Default.ItemData),
            };
        }

        return new LspCompletionList(false, items)
        {
            ItemDefaults = new CompletionItemDefaults(Positions.ToRange(text, list.Span)),
        };
    }

    /// <summary>Fills the description and the edits. Items from an older list are returned unchanged.</summary>
    public async Task<LspCompletionItem> ResolveAsync(LspCompletionItem item, CancellationToken cancellationToken)
    {
        if (item.Data is not { } data || data.Deserialize(HostJsonContext.Default.ItemData) is not { } key)
        {
            return item;
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

        var description = await service.GetDescriptionAsync(document, roslynItem, cancellationToken: cancellationToken).ConfigureAwait(false);
        var change = await service.GetChangeAsync(document, roslynItem, commitCharacter: null, cancellationToken).ConfigureAwait(false);

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
            };
        }

        return item with
        {
            Documentation = documentation,
            Description = classified,
            TextEdit = new Protocol.TextEdit(Positions.ToRange(text, main.Span), main.NewText ?? string.Empty),
            AdditionalTextEdits = additional.Length == 0 ? null : additional,
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
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
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
