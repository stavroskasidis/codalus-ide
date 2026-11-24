using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// A Visual Basic form's events hooked by <c>Handles</c> clauses (ADR 0073, Visual Basic forms): the classic IDE's
/// designer declares components <c>Friend WithEvents</c> and hooks a new handler with <c>Handles Button1.Click</c> (the
/// form's own events with <c>Handles MyBase.Load</c>) on the method in the code file, not in InitializeComponent. They
/// are read from both of the class's files into the model's event statements, and added and removed here.
/// </summary>
internal static class VisualBasicHandles
{
    /// <summary>One item of a <c>Handles</c> clause: the component (empty for <c>MyBase</c>, <c>Me</c>, <c>MyClass</c>), the event, the method.</summary>
    internal sealed record Item(string Component, string Event, string Handler, int Line);

    public static SyntaxTree Parse(SourceText text, string path, ParseOptions? options) =>
        VisualBasicSyntaxTree.ParseText(text, options as VisualBasicParseOptions, path);

    /// <summary>The methods of <paramref name="className"/> in <paramref name="root"/> (every part of it the file declares).</summary>
    private static IEnumerable<MethodBlockSyntax> Methods(SyntaxNode root, string className) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or NamespaceBlockSyntax or ClassBlockSyntax).OfType<ClassBlockSyntax>()
            .Where(c => string.Equals(c.ClassStatement.Identifier.ValueText, className, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Members.OfType<MethodBlockSyntax>());

    /// <summary>The <c>Handles</c> items of the class's methods in <paramref name="root"/>.</summary>
    public static IEnumerable<Item> Of(SyntaxNode root, string className)
    {
        foreach (var method in Methods(root, className))
        {
            foreach (var item in method.SubOrFunctionStatement.HandlesClause?.Events ?? default)
            {
                if (ComponentOf(item) is { } component)
                {
                    yield return new Item(component, item.EventMember.Identifier.ValueText, method.SubOrFunctionStatement.Identifier.ValueText,
                        root.SyntaxTree.GetLineSpan(item.Span).StartLinePosition.Line);
                }
            }
        }
    }

    /// <summary>The component a <c>Handles</c> item names: empty for the form itself, null for a property's events (<c>Handles Panel1.Child.Click</c>).</summary>
    private static string? ComponentOf(HandlesClauseItemSyntax item) => item.EventContainer switch
    {
        KeywordEventContainerSyntax => string.Empty,
        WithEventsEventContainerSyntax container => container.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// <paramref name="model"/> with the events the class's <c>Handles</c> clauses hook (in <paramref name="roots"/>: the code
    /// file's and the designer file's), after InitializeComponent's statements, each named as its component is and its event
    /// as <paramref name="declaredEvent"/> says it's declared (<c>Handles Button1.click</c> is <c>Click</c>; without it, as
    /// written). Their <see cref="DesignerStatement.Line"/> is -1: they aren't InitializeComponent's.
    /// </summary>
    public static DesignerModel WithHandles(DesignerModel model, Func<string, string, string>? declaredEvent, params SyntaxNode?[] roots)
    {
        var names = model.Components.Where(c => !c.IsRoot).ToDictionary(c => c.Name, c => c.Name, StringComparer.OrdinalIgnoreCase);
        var events = new List<DesignerStatement>();
        foreach (var root in roots.OfType<SyntaxNode>())
        {
            foreach (var item in Of(root, model.ClassName))
            {
                var component = item.Component.Length == 0 ? string.Empty : names.GetValueOrDefault(item.Component);
                if (component is null || model.Statements.Any(s => s.Kind == DesignerStatementKinds.Event && s.Target == component && string.Equals(s.Member, item.Event, StringComparison.OrdinalIgnoreCase))
                    || events.Any(e => e.Target == component && string.Equals(e.Member, item.Event, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                events.Add(new DesignerStatement { Kind = DesignerStatementKinds.Event, Target = component, Member = declaredEvent?.Invoke(component, item.Event) ?? item.Event, Handler = item.Handler, Line = -1 });
            }
        }

        return events.Count == 0 ? model : model with { Statements = [.. model.Statements, .. events] };
    }

    /// <summary>
    /// Whether a new handler of <paramref name="component"/>'s event is hooked with <c>Handles</c>, as the classic IDE
    /// hooks it (and as .NET's analyzers require of a designer file, WFO3011 and WFO3012): the form's own events and its
    /// components' (a field without <c>WithEvents</c> gets it, <see cref="WithEvents"/>). An event already hooked with
    /// <c>AddHandler</c> (a form written for .NET Framework by hand) keeps it, and so does a local's, which can't be
    /// <c>WithEvents</c>.
    /// </summary>
    public static bool UsesHandles(DesignerSource source, string component, string eventName) =>
        !source.Statements.Any(s => s.Statement is { Kind: DesignerStatementKinds.Event } e && e.Target == component && string.Equals(e.Member, eventName, StringComparison.OrdinalIgnoreCase))
        && (component.Length == 0 || source.Fields.ContainsKey(component));

    /// <summary>The designer file with <paramref name="component"/>'s field declared <c>WithEvents</c> (the same text when it is, or isn't a field).</summary>
    public static SourceText WithEvents(DesignerSource source, string component)
    {
        if (!source.Fields.TryGetValue(component, out var field) || field.IsWithEvents)
        {
            return source.Text;
        }

        // Before the declaration's first name: Friend OkButton As Button → Friend WithEvents OkButton As Button.
        var first = source.Fields.TryGetValue(field.Declared[0], out var declared) ? declared : field;
        return source.Text.WithChanges(new TextChange(new TextSpan(first.Identifier.Start, 0), "WithEvents "));
    }

    /// <summary>The text of a <c>Handles</c> item: <c>Button1.Click</c>, <c>MyBase.Load</c>.</summary>
    public static string ItemText(string component, string eventName) => $"{(component.Length == 0 ? "MyBase" : component)}.{eventName}";

    /// <summary>
    /// <paramref name="text"/> without the <c>Handles</c> items <paramref name="remove"/> picks (a clause left empty goes
    /// with them), or the same text when it has none.
    /// </summary>
    public static SourceText Remove(SourceText text, string path, ParseOptions? options, string className, Func<string, string, string, bool> remove)
    {
        var root = Parse(text, path, options).GetRoot();
        var changes = new List<TextChange>();
        foreach (var method in Methods(root, className))
        {
            if (method.SubOrFunctionStatement.HandlesClause is not { } clause)
            {
                continue;
            }

            var kept = clause.Events.Where(i => ComponentOf(i) is not { } component || !remove(component, i.EventMember.Identifier.ValueText, method.SubOrFunctionStatement.Identifier.ValueText)).ToList();
            if (kept.Count == clause.Events.Count)
            {
                continue;
            }

            changes.Add(kept.Count == 0
                ? new TextChange(TextSpan.FromBounds(clause.GetFirstToken().GetPreviousToken().Span.End, clause.Span.End), string.Empty)
                : new TextChange(clause.Span, "Handles " + string.Join(", ", kept.Select(i => i.ToString().Trim()))));
        }

        return changes.Count == 0 ? text : text.WithChanges(changes);
    }

    /// <summary>
    /// Adds <paramref name="item"/> to the <c>Handles</c> clause of the class's method <paramref name="handler"/> in
    /// <paramref name="text"/> (making the clause); null when the file doesn't declare the method; the same text when it
    /// already handles it.
    /// </summary>
    public static SourceText? AddTo(SourceText text, string path, ParseOptions? options, string className, string handler, string component, string eventName)
    {
        var root = Parse(text, path, options).GetRoot();
        if (Methods(root, className).FirstOrDefault(m => string.Equals(m.SubOrFunctionStatement.Identifier.ValueText, handler, StringComparison.OrdinalIgnoreCase)) is not { } method)
        {
            return null;
        }

        var statement = method.SubOrFunctionStatement;
        if (statement.HandlesClause is { } clause)
        {
            if (clause.Events.Any(i => ComponentOf(i) is { } c && string.Equals(c, component, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.EventMember.Identifier.ValueText, eventName, StringComparison.OrdinalIgnoreCase)))
            {
                return text;
            }

            return text.WithChanges(new TextChange(new TextSpan(clause.Span.End, 0), ", " + ItemText(component, eventName)));
        }

        return text.WithChanges(new TextChange(new TextSpan(statement.Span.End, 0), " Handles " + ItemText(component, eventName)));
    }

    /// <summary>
    /// A new empty handler at the end of the class in the code file (the classic IDE's shape, with an empty line in its
    /// body): <c>Private Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click</c>.
    /// </summary>
    public static SourceText AddMethod(SourceText text, string path, ParseOptions? options, string className, string handler, string[] parameters, string? handles)
    {
        var root = Parse(text, path, options).GetRoot();
        var type = root.DescendantNodes(n => n is CompilationUnitSyntax or NamespaceBlockSyntax or ClassBlockSyntax).OfType<ClassBlockSyntax>()
            .FirstOrDefault(c => string.Equals(c.ClassStatement.Identifier.ValueText, className, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't declare {className}: the event handler goes there.");
        var newLine = DesignerReader.NewLineOf(text);
        var classIndent = DesignerReader.IndentationAt(text, type.ClassStatement.SpanStart);
        var indent = type.Members.FirstOrDefault() is { } member
            ? DesignerReader.IndentationAt(text, member.SpanStart)
            : classIndent + (classIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
        var lead = type.Members.Count > 0 ? newLine : string.Empty;
        var clause = handles is null ? string.Empty : " Handles " + handles;
        var method = $"{lead}{indent}Private Sub {handler}({string.Join(", ", parameters)}){clause}{newLine}{newLine}{indent}End Sub{newLine}";
        return text.WithChanges(new TextChange(new TextSpan(text.Lines.GetLineFromPosition(type.EndClassStatement.SpanStart).Start, 0), method));
    }

    /// <summary>Where the caret goes in the class's method <paramref name="handler"/>: its first statement, else its empty body's line; null when the file doesn't declare it.</summary>
    public static Protocol.Position? HandlerPosition(SourceText text, string path, ParseOptions? options, string className, string handler)
    {
        var root = Parse(text, path, options).GetRoot();
        if (Methods(root, className).FirstOrDefault(m => string.Equals(m.SubOrFunctionStatement.Identifier.ValueText, handler, StringComparison.OrdinalIgnoreCase)) is not { } method)
        {
            return null;
        }

        if (method.Statements.FirstOrDefault() is { } first)
        {
            return Positions.ToRange(text, new TextSpan(first.SpanStart, 0)).Start;
        }

        var line = text.Lines.GetLineFromPosition(method.SubOrFunctionStatement.Span.End).LineNumber + 1;
        var indent = DesignerReader.IndentationAt(text, method.SubOrFunctionStatement.DeclarationKeyword.SpanStart);
        var bodyIndent = indent + (indent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
        return line < text.Lines.Count && line < text.Lines.GetLineFromPosition(method.EndSubOrFunctionStatement.SpanStart).LineNumber
            ? new Protocol.Position(line, bodyIndent.Length)
            : Positions.ToRange(text, new TextSpan(method.SubOrFunctionStatement.Identifier.SpanStart, 0)).Start;
    }
}
