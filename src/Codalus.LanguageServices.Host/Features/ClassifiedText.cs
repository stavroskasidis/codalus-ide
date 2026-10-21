using System.Collections.Immutable;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Tags;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Roslyn's display parts and tagged text as the protocol's classified runs (the <c>codalus/</c> extensions on hover,
/// signature help and completion resolve): each run's kind is the semantic token type the editor colors the same code
/// with, so Quick Info, signature help and the completion tooltip match the code.
/// </summary>
internal static class ClassifiedText
{
    /// <summary>A symbol display part's run.</summary>
    public static ClassifiedRun Run(SymbolDisplayPart part) => new(part.ToString(), Kind(part.Kind));

    public static ClassifiedRun[] Runs(IEnumerable<SymbolDisplayPart> parts) => [.. parts.Select(Run)];

    public static string Kind(SymbolDisplayPartKind kind) => kind switch
    {
        SymbolDisplayPartKind.ClassName or SymbolDisplayPartKind.ErrorTypeName or SymbolDisplayPartKind.AliasName => "class",
        SymbolDisplayPartKind.RecordClassName => "record",
        SymbolDisplayPartKind.StructName => "struct",
        SymbolDisplayPartKind.RecordStructName => "recordStruct",
        SymbolDisplayPartKind.InterfaceName => "interface",
        SymbolDisplayPartKind.EnumName => "enum",
        SymbolDisplayPartKind.DelegateName => "delegate",
        SymbolDisplayPartKind.TypeParameterName => "typeParameter",
        SymbolDisplayPartKind.ModuleName => "type",
        SymbolDisplayPartKind.Keyword => "keyword",
        SymbolDisplayPartKind.MethodName => "method",
        SymbolDisplayPartKind.ExtensionMethodName => "extensionMethod",
        SymbolDisplayPartKind.PropertyName => "property",
        SymbolDisplayPartKind.FieldName => "field",
        SymbolDisplayPartKind.EventName => "event",
        SymbolDisplayPartKind.ConstantName => "constant",
        SymbolDisplayPartKind.EnumMemberName => "enumMember",
        SymbolDisplayPartKind.LocalName or SymbolDisplayPartKind.RangeVariableName => "local",
        SymbolDisplayPartKind.ParameterName => "parameter",
        SymbolDisplayPartKind.NamespaceName => "namespace",
        SymbolDisplayPartKind.LabelName => "label",
        SymbolDisplayPartKind.NumericLiteral => "number",
        SymbolDisplayPartKind.StringLiteral => "string",
        SymbolDisplayPartKind.Operator => "operator",
        SymbolDisplayPartKind.Punctuation => "punctuation",
        SymbolDisplayPartKind.LineBreak => ClassifiedRun.LineBreak,
        _ => ClassifiedRun.PlainText,
    };

    /// <summary>Tagged text's runs (Quick Info, completion descriptions); line breaks become <c>lineBreak</c> runs.</summary>
    public static List<ClassifiedRun> Runs(IEnumerable<TaggedText> parts)
    {
        var runs = new List<ClassifiedRun>();
        void Add(string text, string kind)
        {
            // Plain text next to plain text is one run (Roslyn splits spaces and words).
            if (runs.Count > 0 && kind == ClassifiedRun.PlainText && runs[^1].Kind == ClassifiedRun.PlainText)
            {
                runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            }
            else
            {
                runs.Add(new ClassifiedRun(text, kind));
            }
        }

        foreach (var part in parts)
        {
            var kind = Kind(part.Tag);
            if (kind == ClassifiedRun.LineBreak)
            {
                runs.Add(new ClassifiedRun("\n", ClassifiedRun.LineBreak));
                continue;
            }

            // Line ends inside text (Roslyn writes some as text) become line breaks too.
            var lines = part.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    runs.Add(new ClassifiedRun("\n", ClassifiedRun.LineBreak));
                }

                if (lines[i].Length > 0)
                {
                    Add(lines[i], kind);
                }
            }
        }

        return runs;
    }

    public static string Kind(string tag) => tag switch
    {
        TextTags.Class or TextTags.ErrorType or TextTags.Alias => "class",
        TextTags.Record => "record",
        TextTags.Struct => "struct",
        TextTags.RecordStruct => "recordStruct",
        TextTags.Interface => "interface",
        TextTags.Enum => "enum",
        TextTags.Delegate => "delegate",
        TextTags.TypeParameter => "typeParameter",
        TextTags.Module => "type",
        TextTags.Keyword => "keyword",
        TextTags.Method => "method",
        TextTags.ExtensionMethod => "extensionMethod",
        TextTags.Property => "property",
        TextTags.Field => "field",
        TextTags.Event => "event",
        TextTags.Constant => "constant",
        TextTags.EnumMember => "enumMember",
        TextTags.Local or TextTags.RangeVariable => "local",
        TextTags.Parameter => "parameter",
        TextTags.Namespace => "namespace",
        TextTags.Label => "label",
        TextTags.NumericLiteral => "number",
        TextTags.StringLiteral => "string",
        TextTags.Operator => "operator",
        TextTags.Punctuation => "punctuation",
        TextTags.LineBreak => ClassifiedRun.LineBreak,
        _ => ClassifiedRun.PlainText,
    };

    /// <summary>The runs split at line breaks (without them), as lines.</summary>
    public static List<List<ClassifiedRun>> Lines(IEnumerable<ClassifiedRun> runs)
    {
        var lines = new List<List<ClassifiedRun>> { new() };
        foreach (var run in runs)
        {
            if (run.Kind == ClassifiedRun.LineBreak)
            {
                lines.Add([]);
            }
            else
            {
                lines[^1].Add(run);
            }
        }

        return lines;
    }

    /// <summary>The runs without leading and trailing white space and line breaks.</summary>
    public static ClassifiedRun[] Trim(IEnumerable<ClassifiedRun> runs)
    {
        var list = runs.ToList();
        while (list.Count > 0 && (list[0].Kind == ClassifiedRun.LineBreak || string.IsNullOrWhiteSpace(list[0].Text)))
        {
            list.RemoveAt(0);
        }

        while (list.Count > 0 && (list[^1].Kind == ClassifiedRun.LineBreak || string.IsNullOrWhiteSpace(list[^1].Text)))
        {
            list.RemoveAt(list.Count - 1);
        }

        if (list.Count > 0)
        {
            list[0] = list[0] with { Text = list[0].Text.TrimStart() };
            list[^1] = list[^1] with { Text = list[^1].Text.TrimEnd() };
        }

        return [.. list];
    }

    /// <summary>
    /// The overload count Roslyn appends to a signature ("(+ 1 overload)", "(+ 17 overloads)"): the runs before it, and
    /// the count (0 when there is none).
    /// </summary>
    public static (ClassifiedRun[] Signature, int Overloads) SplitOverloads(IReadOnlyList<ClassifiedRun> runs)
    {
        var text = string.Concat(runs.Select(r => r.Text));
        var at = text.LastIndexOf("(+", StringComparison.Ordinal);
        if (at < 0 || !text.TrimEnd().EndsWith(')'))
        {
            return ([.. runs], 0);
        }

        var digits = new string([.. text[(at + 2)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsDigit)]);
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            return ([.. runs], 0);
        }

        // Keep the runs up to the "(+".
        var kept = new List<ClassifiedRun>();
        var position = 0;
        foreach (var run in runs)
        {
            if (position + run.Text.Length <= at)
            {
                kept.Add(run);
            }
            else if (position < at)
            {
                kept.Add(run with { Text = run.Text[..(at - position)] });
            }

            position += run.Text.Length;
        }

        return (Trim(kept), count);
    }

    /// <summary>The glyph of Roslyn's tags (completion items, Quick Info), or null.</summary>
    public static string? Glyph(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Class: return SymbolGlyphs.Class;
                case WellKnownTags.Structure: return SymbolGlyphs.Struct;
                case WellKnownTags.Interface: return SymbolGlyphs.Interface;
                case WellKnownTags.Enum: return SymbolGlyphs.Enum;
                case WellKnownTags.EnumMember: return SymbolGlyphs.EnumMember;
                case WellKnownTags.Delegate: return SymbolGlyphs.Delegate;
                case WellKnownTags.ExtensionMethod: return SymbolGlyphs.ExtensionMethod;
                case WellKnownTags.Method: return SymbolGlyphs.Method;
                case WellKnownTags.Property: return SymbolGlyphs.Property;
                case WellKnownTags.Field: return SymbolGlyphs.Field;
                case WellKnownTags.Event: return SymbolGlyphs.Event;
                case WellKnownTags.Constant: return SymbolGlyphs.Constant;
                case WellKnownTags.Local:
                case WellKnownTags.RangeVariable: return SymbolGlyphs.Local;
                case WellKnownTags.Parameter: return SymbolGlyphs.Parameter;
                case WellKnownTags.Namespace: return SymbolGlyphs.Namespace;
                case WellKnownTags.TypeParameter: return SymbolGlyphs.TypeParameter;
                case WellKnownTags.Keyword: return SymbolGlyphs.Keyword;
                case WellKnownTags.Snippet: return SymbolGlyphs.Snippet;
                case WellKnownTags.Operator: return SymbolGlyphs.Operator;
                case WellKnownTags.Label: return SymbolGlyphs.Label;
                case WellKnownTags.Module: return SymbolGlyphs.Module;
            }
        }

        return null;
    }

    /// <summary>A symbol's glyph (signature help, Quick Info without tags).</summary>
    public static string Glyph(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolGlyphs.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolGlyphs.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolGlyphs.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolGlyphs.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => SymbolGlyphs.Delegate,
        INamedTypeSymbol { TypeKind: TypeKind.Module } => SymbolGlyphs.Module,
        ITypeParameterSymbol => SymbolGlyphs.TypeParameter,
        ITypeSymbol => SymbolGlyphs.Class,
        IMethodSymbol { IsExtensionMethod: true } or IMethodSymbol { ReducedFrom: not null } => SymbolGlyphs.ExtensionMethod,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.BuiltinOperator } => SymbolGlyphs.Operator,
        IMethodSymbol => SymbolGlyphs.Method,
        IPropertySymbol => SymbolGlyphs.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolGlyphs.EnumMember,
        IFieldSymbol { IsConst: true } => SymbolGlyphs.Constant,
        IFieldSymbol => SymbolGlyphs.Field,
        IEventSymbol => SymbolGlyphs.Event,
        ILocalSymbol or IRangeVariableSymbol => SymbolGlyphs.Local,
        IParameterSymbol => SymbolGlyphs.Parameter,
        ILabelSymbol => SymbolGlyphs.Label,
        _ => SymbolGlyphs.Text,
    };
}
