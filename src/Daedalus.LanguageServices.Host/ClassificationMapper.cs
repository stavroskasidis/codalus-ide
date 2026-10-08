using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.Classification;

namespace Daedalus.LanguageServices.Host;

/// <summary>Maps Roslyn classifications onto the Daedalus semantic token legend.</summary>
public static class ClassificationMapper
{
    private static readonly Dictionary<string, int> s_types = new(StringComparer.Ordinal)
    {
        [ClassificationTypeNames.Comment] = TokenTypes.IndexOf("comment"),
        [ClassificationTypeNames.ExcludedCode] = TokenTypes.IndexOf("excludedCode"),
        [ClassificationTypeNames.Keyword] = TokenTypes.IndexOf("keyword"),
        [ClassificationTypeNames.ControlKeyword] = TokenTypes.IndexOf("controlKeyword"),
        [ClassificationTypeNames.NumericLiteral] = TokenTypes.IndexOf("number"),
        [ClassificationTypeNames.Operator] = TokenTypes.IndexOf("operator"),
        [ClassificationTypeNames.OperatorOverloaded] = TokenTypes.IndexOf("operator"),
        [ClassificationTypeNames.PreprocessorKeyword] = TokenTypes.IndexOf("preprocessor"),
        [ClassificationTypeNames.Punctuation] = TokenTypes.IndexOf("punctuation"),
        [ClassificationTypeNames.StringLiteral] = TokenTypes.IndexOf("string"),
        [ClassificationTypeNames.VerbatimStringLiteral] = TokenTypes.IndexOf("stringVerbatim"),
        [ClassificationTypeNames.StringEscapeCharacter] = TokenTypes.IndexOf("stringEscape"),
        [ClassificationTypeNames.ClassName] = TokenTypes.IndexOf("class"),
        [ClassificationTypeNames.RecordClassName] = TokenTypes.IndexOf("record"),
        [ClassificationTypeNames.DelegateName] = TokenTypes.IndexOf("delegate"),
        [ClassificationTypeNames.EnumName] = TokenTypes.IndexOf("enum"),
        [ClassificationTypeNames.InterfaceName] = TokenTypes.IndexOf("interface"),
        [ClassificationTypeNames.ModuleName] = TokenTypes.IndexOf("type"),
        [ClassificationTypeNames.StructName] = TokenTypes.IndexOf("struct"),
        [ClassificationTypeNames.RecordStructName] = TokenTypes.IndexOf("recordStruct"),
        [ClassificationTypeNames.TypeParameterName] = TokenTypes.IndexOf("typeParameter"),
        [ClassificationTypeNames.FieldName] = TokenTypes.IndexOf("field"),
        [ClassificationTypeNames.EnumMemberName] = TokenTypes.IndexOf("enumMember"),
        [ClassificationTypeNames.ConstantName] = TokenTypes.IndexOf("constant"),
        [ClassificationTypeNames.LocalName] = TokenTypes.IndexOf("local"),
        [ClassificationTypeNames.ParameterName] = TokenTypes.IndexOf("parameter"),
        [ClassificationTypeNames.MethodName] = TokenTypes.IndexOf("method"),
        [ClassificationTypeNames.ExtensionMethodName] = TokenTypes.IndexOf("extensionMethod"),
        [ClassificationTypeNames.PropertyName] = TokenTypes.IndexOf("property"),
        [ClassificationTypeNames.EventName] = TokenTypes.IndexOf("event"),
        [ClassificationTypeNames.NamespaceName] = TokenTypes.IndexOf("namespace"),
        [ClassificationTypeNames.LabelName] = TokenTypes.IndexOf("label"),
    };

    public static IReadOnlyList<TokenSpan> Map(IEnumerable<ClassifiedSpan> spans)
    {
        // Roslyn reports several classifications for one span (e.g. "class name" + "static symbol").
        // Collapse them into one token type plus a modifier bitset.
        var bySpan = new Dictionary<(int Start, int Length), (int Type, int Modifiers)>();
        foreach (var span in spans)
        {
            var key = (span.TextSpan.Start, span.TextSpan.Length);
            bySpan.TryGetValue(key, out var current);
            if (!bySpan.ContainsKey(key))
            {
                current = (-1, 0);
            }

            if (span.ClassificationType == ClassificationTypeNames.StaticSymbol)
            {
                current.Modifiers |= TokenModifiers.Static;
            }
            else if (current.Type < 0 && TryMapType(span.ClassificationType, out var type))
            {
                current.Type = type;
            }

            bySpan[key] = current;
        }

        var result = new List<TokenSpan>(bySpan.Count);
        foreach (var ((start, length), (type, modifiers)) in bySpan)
        {
            if (type >= 0 && length > 0)
            {
                result.Add(new TokenSpan(start, length, type, modifiers));
            }
        }

        return result;
    }

    private static bool TryMapType(string classification, out int type)
    {
        if (s_types.TryGetValue(classification, out type))
        {
            return true;
        }

        if (classification.StartsWith("xml doc comment", StringComparison.Ordinal))
        {
            type = TokenTypes.IndexOf("xmlDocComment");
            return true;
        }

        if (classification.StartsWith("regex", StringComparison.Ordinal))
        {
            type = TokenTypes.IndexOf("regexp");
            return true;
        }

        if (classification.StartsWith("json", StringComparison.Ordinal))
        {
            type = TokenTypes.IndexOf("string");
            return true;
        }

        type = -1;
        return false;
    }
}

/// <summary>A classified, possibly multi-line, possibly nested span of text.</summary>
public readonly record struct TokenSpan(int Start, int Length, int Type, int Modifiers)
{
    public int End => Start + Length;
}
