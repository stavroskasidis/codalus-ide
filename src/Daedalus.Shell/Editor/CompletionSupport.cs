using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Daedalus.LanguageServices.Protocol;
using LspCompletionItem = Daedalus.LanguageServices.Protocol.CompletionItem;

namespace Daedalus.Shell.Editor;

/// <summary>
/// One completion item in AvaloniaEdit's completion window. Filtering uses <see cref="Text"/> (the filter text);
/// committing applies the resolved edit (prefetched while the item is selected) and any additional edits.
/// </summary>
internal sealed class CompletionData(LspCompletionItem item, Func<LspCompletionItem, Task<LspCompletionItem>> resolve, Action<CompletionData, ISegment> commit) : ICompletionData
{
    private Task<LspCompletionItem>? _resolved;

    public LspCompletionItem Item { get; } = item;

    public Avalonia.Media.IImage? Image => null;

    public string Text => Item.FilterText ?? Item.Label;

    public object Content => new TextBlock
    {
        Inlines =
        [
            new Run(KindGlyph(Item.Kind)) { Foreground = KindBrush(Item.Kind), FontWeight = FontWeight.Bold },
            new Run(" " + Item.Label),
            new Run(Item.Detail is null ? string.Empty : "  " + Item.Detail) { Foreground = Brushes.Gray },
        ],
    };

    public object? Description => _resolved is { IsCompletedSuccessfully: true, Result.Documentation.Value: { Length: > 0 } doc }
        ? new TextBlock { Text = PlainText(doc), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }
        : null;

    public double Priority => Item.Preselect == true ? 1 : 0;

    /// <summary>Starts resolving (description and edits) in the background.</summary>
    public Task<LspCompletionItem> Resolve() => _resolved ??= resolve(Item);

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

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => commit(this, completionSegment);

    /// <summary>Markdown without code fences (the completion tooltip is plain text).</summary>
    private static string PlainText(string markdown) =>
        string.Join('\n', markdown.Split('\n').Where(l => !l.StartsWith("```", StringComparison.Ordinal))).Replace("\\", string.Empty, StringComparison.Ordinal).Trim();

    private static string KindGlyph(int? kind) => kind switch
    {
        CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => "ƒ",
        CompletionItemKind.Property => "◆",
        CompletionItemKind.Field or CompletionItemKind.Variable => "▪",
        CompletionItemKind.Class or CompletionItemKind.Struct => "◼",
        CompletionItemKind.Interface => "◻",
        CompletionItemKind.Enum or CompletionItemKind.EnumMember or CompletionItemKind.Constant => "≡",
        CompletionItemKind.Event => "⚡",
        CompletionItemKind.Keyword => "k",
        CompletionItemKind.Module => "{}",
        CompletionItemKind.Snippet => "⎘",
        CompletionItemKind.TypeParameter => "T",
        _ => "·",
    };

    private static IBrush KindBrush(int? kind) => kind switch
    {
        CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => Brushes.MediumPurple,
        CompletionItemKind.Property or CompletionItemKind.Field or CompletionItemKind.Variable => Brushes.SteelBlue,
        CompletionItemKind.Class or CompletionItemKind.Struct or CompletionItemKind.Interface or CompletionItemKind.TypeParameter => Brushes.Orange,
        CompletionItemKind.Enum or CompletionItemKind.EnumMember or CompletionItemKind.Constant => Brushes.Goldenrod,
        _ => Brushes.Gray,
    };
}

/// <summary>Signature help in AvaloniaEdit's overload window: the signature with the active parameter in bold.</summary>
internal sealed class SignatureProvider : IOverloadProvider
{
    private readonly SignatureHelp _help;
    private int _selectedIndex;

    public SignatureProvider(SignatureHelp help)
    {
        _help = help;
        _selectedIndex = Math.Clamp(help.ActiveSignature, 0, Math.Max(0, help.Signatures.Length - 1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = Math.Clamp(value, 0, Math.Max(0, _help.Signatures.Length - 1));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentIndexText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
        }
    }

    public int Count => _help.Signatures.Length;

    public string? CurrentIndexText => Count > 1 ? $"{SelectedIndex + 1} of {Count}" : null;

    public object? CurrentHeader
    {
        get
        {
            var signature = _help.Signatures[SelectedIndex];
            var active = signature.ActiveParameter ?? _help.ActiveParameter;
            var block = new TextBlock();
            var label = signature.Label;
            var position = 0;
            for (var i = 0; i < signature.Parameters.Length; i++)
            {
                var index = label.IndexOf(signature.Parameters[i].Label, position, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                block.Inlines!.Add(new Run(label[position..index]));
                block.Inlines.Add(new Run(signature.Parameters[i].Label) { FontWeight = i == active ? FontWeight.Bold : FontWeight.Normal });
                position = index + signature.Parameters[i].Label.Length;
            }

            block.Inlines!.Add(new Run(label[position..]));
            return block;
        }
    }

    public object? CurrentContent
    {
        get
        {
            var signature = _help.Signatures[SelectedIndex];
            var active = signature.ActiveParameter ?? _help.ActiveParameter;
            var parts = new List<string>();
            if (signature.Documentation?.Value is { Length: > 0 } doc)
            {
                parts.Add(doc.Replace("\\", string.Empty, StringComparison.Ordinal));
            }

            if (active >= 0 && active < signature.Parameters.Length && signature.Parameters[active].Documentation?.Value is { Length: > 0 } parameter)
            {
                parts.Add(signature.Parameters[active].Label.Split(' ').Last() + ": " + parameter.Replace("\\", string.Empty, StringComparison.Ordinal));
            }

            return parts.Count == 0 ? null : new TextBlock { Text = string.Join('\n', parts), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        }
    }
}
