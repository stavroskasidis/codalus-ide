using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Markup.Host.EditorConfig;

/// <summary>An <c>.editorconfig</c> key: what it does and its values (null: free text or a number).</summary>
internal sealed record EditorConfigKey(string Name, string Description, string[]? Values);

/// <summary>
/// <c>.editorconfig</c> (ADR 0016): completion of the common keys (EditorConfig's own and .NET's code style and
/// analyzer options) and their values, hover, and warnings for values a key doesn't take. Unknown keys are allowed:
/// analyzers define their own.
/// </summary>
internal static class EditorConfigLanguage
{
    private static readonly string[] Booleans = ["true", "false"];
    private static readonly string[] Severities = ["none", "silent", "suggestion", "warning", "error", "default"];
    private static readonly string[] StylePreference = ["true:silent", "true:suggestion", "true:warning", "true:error", "false:silent", "false:suggestion", "false:warning", "false:error", "true", "false"];

    public static readonly EditorConfigKey[] Keys =
    [
        new("root", "Stop looking for .editorconfig files in parent folders.", Booleans),
        new("indent_style", "Indent with spaces or tabs.", ["space", "tab"]),
        new("indent_size", "Columns per indentation level (or `tab`).", null),
        new("tab_width", "Columns a tab character takes.", null),
        new("end_of_line", "Line endings.", ["lf", "crlf", "cr"]),
        new("charset", "File encoding.", ["utf-8", "utf-8-bom", "latin1", "utf-16be", "utf-16le"]),
        new("trim_trailing_whitespace", "Remove whitespace at the ends of lines.", Booleans),
        new("insert_final_newline", "End files with a newline.", Booleans),
        new("max_line_length", "The maximum line length (or `off`).", null),
        new("dotnet_sort_system_directives_first", "Sort `System` usings first.", Booleans),
        new("dotnet_separate_import_directive_groups", "Separate using groups with blank lines.", Booleans),
        new("dotnet_style_qualification_for_field", "Prefer `this.` for fields.", StylePreference),
        new("dotnet_style_qualification_for_property", "Prefer `this.` for properties.", StylePreference),
        new("dotnet_style_qualification_for_method", "Prefer `this.` for methods.", StylePreference),
        new("dotnet_style_qualification_for_event", "Prefer `this.` for events.", StylePreference),
        new("dotnet_style_predefined_type_for_locals_parameters_members", "Prefer `int` over `Int32` in declarations.", StylePreference),
        new("dotnet_style_predefined_type_for_member_access", "Prefer `int.MaxValue` over `Int32.MaxValue`.", StylePreference),
        new("dotnet_style_require_accessibility_modifiers", "Require accessibility modifiers.", ["always", "for_non_interface_members", "never", "omit_if_default"]),
        new("dotnet_style_readonly_field", "Prefer `readonly` fields.", StylePreference),
        new("dotnet_style_object_initializer", "Prefer object initializers.", StylePreference),
        new("dotnet_style_collection_initializer", "Prefer collection initializers.", StylePreference),
        new("dotnet_style_prefer_collection_expression", "Prefer collection expressions (`[1, 2]`).", ["never", "when_types_exactly_match", "when_types_loosely_match", "true", "false"]),
        new("dotnet_style_coalesce_expression", "Prefer `??`.", StylePreference),
        new("dotnet_style_null_propagation", "Prefer `?.`.", StylePreference),
        new("dotnet_style_prefer_auto_properties", "Prefer auto-properties.", StylePreference),
        new("dotnet_style_prefer_conditional_expression_over_assignment", "Prefer `?:` for assignments.", StylePreference),
        new("dotnet_style_prefer_conditional_expression_over_return", "Prefer `?:` for returns.", StylePreference),
        new("dotnet_style_namespace_match_folder", "Namespaces should match folders.", StylePreference),
        new("dotnet_code_quality_unused_parameters", "Report unused parameters.", ["all", "non_public"]),
        new("csharp_style_var_for_built_in_types", "Prefer `var` for built-in types.", StylePreference),
        new("csharp_style_var_when_type_is_apparent", "Prefer `var` when the type is apparent.", StylePreference),
        new("csharp_style_var_elsewhere", "Prefer `var` elsewhere.", StylePreference),
        new("csharp_style_expression_bodied_methods", "Expression bodies for methods.", ["true", "false", "when_on_single_line", "true:suggestion", "false:silent", "when_on_single_line:silent"]),
        new("csharp_style_expression_bodied_properties", "Expression bodies for properties.", ["true", "false", "when_on_single_line", "true:silent"]),
        new("csharp_style_namespace_declarations", "Block or file-scoped namespaces.", ["block_scoped", "file_scoped", "file_scoped:warning", "file_scoped:suggestion", "block_scoped:silent"]),
        new("csharp_style_pattern_matching_over_is_with_cast_check", "Prefer pattern matching over `is` with a cast.", StylePreference),
        new("csharp_style_prefer_switch_expression", "Prefer switch expressions.", StylePreference),
        new("csharp_style_prefer_primary_constructors", "Prefer primary constructors.", StylePreference),
        new("csharp_style_unused_value_assignment_preference", "How to discard unused assignments.", ["discard_variable", "unused_local_variable"]),
        new("csharp_prefer_braces", "Require braces.", ["true", "false", "when_multiline", "true:warning", "when_multiline:silent"]),
        new("csharp_prefer_simple_using_statement", "Prefer `using var`.", StylePreference),
        new("csharp_using_directive_placement", "Usings inside or outside the namespace.", ["outside_namespace", "inside_namespace", "outside_namespace:warning", "inside_namespace:warning"]),
        new("csharp_new_line_before_open_brace", "Where `{` goes on a new line.", ["all", "none", "types", "methods", "properties", "accessors", "anonymous_methods", "control_blocks", "object_collection_array_initializers", "lambdas"]),
        new("csharp_new_line_before_else", "`else` on a new line.", Booleans),
        new("csharp_new_line_before_catch", "`catch` on a new line.", Booleans),
        new("csharp_new_line_before_finally", "`finally` on a new line.", Booleans),
        new("csharp_indent_case_contents", "Indent case contents.", Booleans),
        new("csharp_indent_switch_labels", "Indent switch labels.", Booleans),
        new("csharp_space_after_cast", "A space after casts.", Booleans),
        new("csharp_space_after_keywords_in_control_flow_statements", "A space after `if`, `for`, …", Booleans),
        new("csharp_preserve_single_line_blocks", "Keep single-line blocks.", Booleans),
        new("csharp_preserve_single_line_statements", "Keep several statements on a line.", Booleans),
        new("dotnet_analyzer_diagnostic.severity", "The severity of every analyzer rule.", Severities),
        new("dotnet_analyzer_diagnostic.category-Style.severity", "The severity of style rules.", Severities),
        new("dotnet_analyzer_diagnostic.category-Performance.severity", "The severity of performance rules.", Severities),
        new("dotnet_analyzer_diagnostic.category-Security.severity", "The severity of security rules.", Severities),
        new("generated_code", "Whether the files are generated (analyzers skip them).", Booleans),
    ];

    public static List<Diagnostic> Diagnostics(TextDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var (line, key, keyStart, value, valueStart) in Assignments(document))
        {
            var allowed = ValuesFor(key);
            if (allowed is not null && value.Length > 0 && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
                && !(key.EndsWith(".severity", StringComparison.Ordinal) && Severities.Contains(value, StringComparer.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new Diagnostic(new LanguageServices.Protocol.Range(new Position(line, valueStart), new Position(line, valueStart + value.Length)),
                    DiagnosticSeverity.Warning, "editorconfig.value", "editorconfig", $"'{value}' isn't a value of {key}. Expected: {string.Join(", ", allowed.Take(10))}{(allowed.Length > 10 ? ", …" : string.Empty)}."));
            }
        }

        return diagnostics;
    }

    public static CompletionList Completion(TextDocument document, int offset)
    {
        var position = document.PositionAt(offset);
        var lineStart = document.LineStarts[position.Line];
        var lineText = document.Text[lineStart..offset];
        if (lineText.TrimStart().StartsWith('[') || lineText.TrimStart().StartsWith('#') || lineText.TrimStart().StartsWith(';'))
        {
            return new CompletionList(false, []);
        }

        var equals = lineText.IndexOf('=', StringComparison.Ordinal);
        var items = new List<CompletionItem>();
        if (equals < 0)
        {
            var start = lineStart + (lineText.Length - lineText.TrimStart().Length);
            var range = document.RangeOf(start, offset);
            items.AddRange(Keys.Select(k => new CompletionItem(k.Name) { Kind = CompletionItemKind.Property, Documentation = MarkupContent.Markdown(k.Description), TextEdit = new TextEdit(range, k.Name + " = ") }));
            items.Add(new CompletionItem("dotnet_diagnostic.<id>.severity") { Kind = CompletionItemKind.Snippet, TextEdit = new TextEdit(range, "dotnet_diagnostic.CA0000.severity = "), Documentation = MarkupContent.Markdown("The severity of one analyzer rule.") });
        }
        else
        {
            var key = lineText[..equals].Trim();
            var valueStart = lineStart + equals + 1;
            while (valueStart < offset && document.Text[valueStart] == ' ')
            {
                valueStart++;
            }

            var range = document.RangeOf(valueStart, offset);
            items.AddRange((ValuesFor(key) ?? []).Select(v => new CompletionItem(v) { Kind = CompletionItemKind.EnumMember, TextEdit = new TextEdit(range, v) }));
        }

        return new CompletionList(false, [.. items]);
    }

    public static Hover? Hover(TextDocument document, int offset)
    {
        var line = document.PositionAt(offset).Line;
        foreach (var (l, key, keyStart, _, _) in Assignments(document))
        {
            if (l == line && Keys.FirstOrDefault(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                var values = known.Values is { } v ? "\n\nValues: " + string.Join(", ", v.Select(x => "`" + x + "`")) : string.Empty;
                return new Hover(MarkupContent.Markdown($"**{known.Name}**\n\n{known.Description}{values}"),
                    new LanguageServices.Protocol.Range(new Position(l, keyStart), new Position(l, keyStart + key.Length)));
            }
        }

        return null;
    }

    private static string[]? ValuesFor(string key) =>
        key.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase)
            ? Severities
            : Keys.FirstOrDefault(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Values;

    private static IEnumerable<(int Line, string Key, int KeyStart, string Value, int ValueStart)> Assignments(TextDocument document)
    {
        var lines = document.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].TrimEnd('\r');
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';' or '[')
            {
                continue;
            }

            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = text[..equals].Trim();
            var keyStart = text.IndexOf(key, StringComparison.Ordinal);
            var raw = text[(equals + 1)..];
            var value = raw.Trim();
            var comment = value.IndexOfAny(['#', ';']);
            if (comment > 0)
            {
                value = value[..comment].TrimEnd();
            }

            yield return (i, key, keyStart, value, equals + 1 + (raw.Length - raw.TrimStart().Length));
        }
    }
}
